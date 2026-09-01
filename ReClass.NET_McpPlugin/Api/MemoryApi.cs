using System;
using System.Collections.Generic;
using System.Linq;
using McpPlugin.Rpc;
using McpPlugin.Serialization;
using ReClassNET;
using ReClassNET.Memory;

namespace McpPlugin.Api
{
	/// <summary>
	/// Raw and typed memory access plus the module and section listings. All of
	/// these run on the RPC client thread: <see cref="RemoteProcess"/> reads and
	/// writes are already used off the UI thread by ReClass itself.
	/// </summary>
	public class MemoryApi
	{
		/// <summary>Refuse a single read/write larger than this (16 MiB).</summary>
		public const int MaxTransferSize = 16 * 1024 * 1024;

		public void Register(RpcDispatcher dispatcher)
		{
			dispatcher.Register("memory.read", Read);
			dispatcher.Register("memory.read_batch", ReadBatch);
			dispatcher.Register("memory.write", Write);
			dispatcher.Register("memory.read_typed", ReadTyped);
			dispatcher.Register("memory.write_typed", WriteTyped);
			dispatcher.Register("memory.read_string", ReadString);
			dispatcher.Register("memory.eval_address", EvalAddress);
			dispatcher.Register("modules.list", ModulesList);
			dispatcher.Register("sections.list", SectionsList);
		}

		private static RemoteProcess Process => Program.RemoteProcess;

		internal static void RequireProcess()
		{
			if (Process.UnderlayingProcess == null || !Process.IsValid)
			{
				throw RpcException.NoProcess();
			}
		}

		private static int RequireSize(Dictionary<string, object> p, string name)
		{
			var size = Params.Get<int>(p, name);
			if (size < 0 || size > MaxTransferSize)
			{
				throw RpcException.BadAddress($"'{name}' must be between 0 and {MaxTransferSize}");
			}
			return size;
		}

		/// <summary>Reads raw memory, or throws <c>-32002</c> if the range is unreadable.</summary>
		private static byte[] ReadOrThrow(IntPtr address, int size)
		{
			var buffer = new byte[size];
			if (size > 0 && !Process.ReadRemoteMemoryIntoBuffer(address, ref buffer))
			{
				throw RpcException.BadAddress($"failed to read {size} bytes at {Json.Address(address)}");
			}
			return buffer;
		}

		private object Read(Dictionary<string, object> p)
		{
			RequireProcess();

			var address = Params.GetAddress(p, "address");
			var size = RequireSize(p, "size");

			var data = ReadOrThrow(address, size);

			return new Dictionary<string, object>
			{
				{ "address", Json.Address(address) },
				{ "size", size },
				{ "data_b64", System.Convert.ToBase64String(data) }
			};
		}

		/// <summary>Reads several ranges; unreadable ranges yield <c>data_b64: null</c> instead of an error.</summary>
		private object ReadBatch(Dictionary<string, object> p)
		{
			RequireProcess();

			var reads = Params.GetList(p, "reads");

			var results = new List<object>(reads.Count);
			foreach (var item in reads)
			{
				var entry = Params.AsObject(item, "reads[]");
				var address = Params.GetAddress(entry, "address");
				var size = RequireSize(entry, "size");

				var buffer = new byte[size];
				var ok = size == 0 || Process.ReadRemoteMemoryIntoBuffer(address, ref buffer);

				results.Add(new Dictionary<string, object>
				{
					{ "address", Json.Address(address) },
					{ "size", size },
					{ "data_b64", ok ? System.Convert.ToBase64String(buffer) : null }
				});
			}

			return results;
		}

		private object Write(Dictionary<string, object> p)
		{
			RequireProcess();

			var address = Params.GetAddress(p, "address");
			var encoded = Params.Get<string>(p, "data_b64");

			byte[] data;
			try
			{
				data = System.Convert.FromBase64String(encoded);
			}
			catch (FormatException)
			{
				throw RpcException.BadAddress("'data_b64' is not valid base64");
			}

			if (data.Length > MaxTransferSize)
			{
				throw RpcException.BadAddress($"'data_b64' decodes to more than {MaxTransferSize} bytes");
			}

			if (!Process.WriteRemoteMemory(address, data))
			{
				throw RpcException.BadAddress($"failed to write {data.Length} bytes at {Json.Address(address)}");
			}

			return ProcessApi.Ok();
		}

		private object ReadTyped(Dictionary<string, object> p)
		{
			RequireProcess();

			var address = Params.GetAddress(p, "address");
			var type = Params.Get<string>(p, "type");

			if (ValueCodec.IsText(type))
			{
				var length = Params.GetOptional(p, "length", 256);
				if (length <= 0 || length > MaxTransferSize)
				{
					throw RpcException.BadAddress($"'length' must be between 1 and {MaxTransferSize}");
				}

				var text = ValueCodec.DecodeString(type, ReadOrThrow(address, length));

				return new Dictionary<string, object> { { "values", new List<object> { text } } };
			}

			var count = Params.GetOptional(p, "count", 1);
			if (count <= 0)
			{
				throw RpcException.BadAddress("'count' must be positive");
			}

			var size = ValueCodec.SizeOf(type);
			if ((long)size * count > MaxTransferSize)
			{
				throw RpcException.BadAddress($"'count' would read more than {MaxTransferSize} bytes");
			}

			var data = ReadOrThrow(address, size * count);

			return new Dictionary<string, object> { { "values", ValueCodec.DecodeAll(type, data, count) } };
		}

		private object WriteTyped(Dictionary<string, object> p)
		{
			RequireProcess();

			var address = Params.GetAddress(p, "address");
			var type = Params.Get<string>(p, "type");
			var value = Params.GetRaw(p, "value");

			var data = ValueCodec.Encode(type, value);

			if (!Process.WriteRemoteMemory(address, data))
			{
				throw RpcException.BadAddress($"failed to write {data.Length} bytes at {Json.Address(address)}");
			}

			return ProcessApi.Ok();
		}

		private object ReadString(Dictionary<string, object> p)
		{
			RequireProcess();

			var address = Params.GetAddress(p, "address");
			var encoding = Params.GetOptional(p, "encoding", "utf8");
			var maxLength = Params.GetOptional(p, "max_length", 256);

			if (maxLength <= 0 || maxLength > MaxTransferSize)
			{
				throw RpcException.BadAddress($"'max_length' must be between 1 and {MaxTransferSize}");
			}

			var charSize = encoding.Trim().ToLowerInvariant() == "utf8" ? 1 : (encoding.Trim().ToLowerInvariant() == "utf16" ? 2 : 4);

			var data = ReadOrThrow(address, maxLength * charSize);

			return new Dictionary<string, object> { { "value", ValueCodec.DecodeString(encoding, data) } };
		}

		private object EvalAddress(Dictionary<string, object> p)
		{
			RequireProcess();

			var formula = Params.Get<string>(p, "formula");

			IntPtr address;
			try
			{
				address = Process.ParseAddress(formula);
			}
			catch (Exception ex)
			{
				throw RpcException.BadAddress($"failed to evaluate '{formula}': {ex.Message}");
			}

			return new Dictionary<string, object> { { "address", Json.Address(address) } };
		}

		private object ModulesList(Dictionary<string, object> p)
		{
			RequireProcess();

			if (Params.GetOptional(p, "refresh", false))
			{
				Process.UpdateProcessInformations();
			}

			return Process.Modules
				.Select(m => (object)new Dictionary<string, object>
				{
					{ "name", m.Name },
					{ "path", m.Path },
					{ "start", Json.Address(m.Start) },
					{ "end", Json.Address(m.End) },
					{ "size", m.Size.ToInt64() }
				})
				.ToList();
		}

		private object SectionsList(Dictionary<string, object> p)
		{
			RequireProcess();

			var module = Params.GetOptional<string>(p, "module", null);

			IEnumerable<Section> sections = Process.Sections;
			if (!string.IsNullOrEmpty(module))
			{
				sections = sections.Where(s => string.Equals(s.ModuleName, module, StringComparison.OrdinalIgnoreCase));
			}

			return sections
				.Select(s => (object)new Dictionary<string, object>
				{
					{ "name", s.Name },
					{ "start", Json.Address(s.Start) },
					{ "end", Json.Address(s.End) },
					{ "size", s.Size.ToInt64() },
					{ "category", s.Category.ToString() },
					{ "protection", s.Protection.ToString() },
					{ "type", s.Type.ToString() },
					{ "module_name", s.ModuleName }
				})
				.ToList();
		}
	}
}
