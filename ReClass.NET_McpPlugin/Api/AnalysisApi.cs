using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using McpPlugin.Rpc;
using McpPlugin.Serialization;
using ReClassNET;
using ReClassNET.Extensions;
using ReClassNET.Memory;
using ReClassNET.Nodes;

namespace McpPlugin.Api
{
	/// <summary>
	/// The analysis helpers: node dissection and guessing, the memory preview,
	/// the disassembler, RTTI and named addresses.
	///
	/// <c>analysis.dissect</c> and <c>analysis.guess</c> touch project nodes and
	/// therefore run on the UI thread; everything else only reads memory and
	/// runs on the RPC client thread.
	/// </summary>
	public class AnalysisApi
	{
		/// <summary>Refuse previews / disassembly larger than this.</summary>
		public const int MaxPreviewSize = 4096;
		public const int MaxDisassembleLength = 64 * 1024;
		public const int MaxInstructions = 4096;

		private readonly Lazy<Disassembler> disassembler = new Lazy<Disassembler>(() => new Disassembler(Program.CoreFunctions));

		public void Register(RpcDispatcher dispatcher)
		{
			dispatcher.Register("analysis.dissect", Dissect);
			dispatcher.Register("analysis.guess", Guess);
			dispatcher.Register("analysis.pointer_preview", PointerPreview);
			dispatcher.Register("analysis.disassemble", Disassemble);
			dispatcher.Register("analysis.rtti", Rtti);
			dispatcher.Register("analysis.named_address", NamedAddress);
		}

		private static RemoteProcess Process => Program.RemoteProcess;

		// ------------------------------------------------------------------
		// Temporary (project-less) classes
		// ------------------------------------------------------------------

		/// <summary>
		/// Creates a <see cref="ClassNode"/> which is *not* added to the project.
		/// <c>ClassNode.Create()</c> raises <c>ClassCreated</c>, which the main
		/// form forwards to <c>ReClassNetProject.AddClass</c>, so it cannot be
		/// used for the throw-away classes the guessing helpers need. The
		/// non-notifying constructor is `internal`, hence the reflection; if it
		/// ever disappears we fall back to creating and removing a real class.
		/// </summary>
		private static ClassNode CreateTemporaryClass()
		{
			try
			{
				var ctor = typeof(ClassNode).GetConstructor(
					BindingFlags.Instance | BindingFlags.NonPublic,
					null,
					new[] { typeof(bool) },
					null);
				if (ctor != null)
				{
					return (ClassNode)ctor.Invoke(new object[] { false });
				}
			}
			catch (Exception)
			{
				// fall through
			}

			var classNode = ClassNode.Create();
			try
			{
				Program.MainForm?.CurrentProject?.Remove(classNode);
			}
			catch (Exception)
			{
				// ignored
			}
			return classNode;
		}

		/// <summary>Builds a temporary class of <see cref="Hex64Node"/>s covering <paramref name="size"/> bytes at <paramref name="address"/>.</summary>
		private static ClassNode CreateProbeClass(IntPtr address, int size, out MemoryBuffer memory)
		{
			var classNode = CreateTemporaryClass();
			classNode.AddressFormula = address.ToInt64().ToString("X", CultureInfo.InvariantCulture);

			classNode.BeginUpdate();
			classNode.AddBytes(Math.Max(size, IntPtr.Size));
			classNode.EndUpdate();
			classNode.UpdateOffsets();

			memory = new MemoryBuffer { Size = Math.Max(classNode.MemorySize, 1) };
			memory.UpdateFrom(Process, address);

			return classNode;
		}

		// ------------------------------------------------------------------
		// analysis.dissect / analysis.guess
		// ------------------------------------------------------------------

		/// <summary>
		/// Dissects the hex nodes of a class (or of the container selected by
		/// <c>node</c>) and returns the nodes whose type changed.
		/// </summary>
		private object Dissect(Dictionary<string, object> p)
		{
			MemoryApi.RequireProcess();

			return UiThread.Invoke(() =>
			{
				var project = ProjectAccess.Project;

				BaseNode target;
				if (Params.Has(p, "node"))
				{
					target = NodeSelector.ResolveNodeParam(project, p);
				}
				else
				{
					target = NodeSelector.ResolveClass(project, p);
				}

				var container = target as BaseContainerNode ?? target.GetParentContainer();
				if (container == null)
				{
					throw RpcException.BadArgument("the selected node has no container to dissect");
				}

				var classNode = container as ClassNode ?? FindClass(container);
				if (classNode == null)
				{
					throw RpcException.BadArgument("the selected node does not belong to a class");
				}

				var memory = NodeDto.CreateMemory(classNode);
				if (memory == null)
				{
					throw RpcException.BadArgument($"failed to read the memory of class '{classNode.Name}'");
				}

				// Node offsets are relative to their immediate parent, so a nested
				// container needs the buffer shifted by the sum of the offsets on the
				// way up to the class, not just by its own.
				if (!ReferenceEquals(container, classNode))
				{
					var shift = NodeDto.OffsetInClass(container);
					if (shift == null)
					{
						throw RpcException.BadArgument("the selected node lives behind a pointer and has no fixed offset in its class");
					}

					memory = memory.Clone();
					memory.Offset += shift.Value;
				}

				var hexNodes = container.Nodes.OfType<BaseHexNode>().ToList();
				if (target is BaseHexNode single)
				{
					hexNodes = new List<BaseHexNode> { single };
				}

				var before = hexNodes.ToDictionary(n => n, n => n.Offset);

				container.BeginUpdate();
				NodeDissector.DissectNodes(hexNodes, Process, memory);
				container.EndUpdate();

				classNode.UpdateOffsets();

				// The dissector replaces nodes in place, so anything which is no
				// longer a child of the container at its old offset changed.
				var changed = new List<object>();
				foreach (var pair in before)
				{
					var replacement = container.Nodes.FirstOrDefault(n => n.Offset == pair.Value);
					if (replacement != null && !ReferenceEquals(replacement, pair.Key))
					{
						// CreateMemoryFor walks the parent chain, so a node inside a
						// nested container reads its value from the right place.
						changed.Add(NodeDto.ToDto(replacement, NodeDto.CreateMemoryFor(replacement), 1, true));
					}
				}

				Program.MainForm.Invalidate();

				return (object)new Dictionary<string, object> { { "changed", changed } };
			});
		}

		private static ClassNode FindClass(BaseNode node)
		{
			var current = node;
			while (current != null)
			{
				if (current is ClassNode classNode)
				{
					return classNode;
				}
				current = current.ParentNode;
			}
			return null;
		}

		/// <summary>Guesses the node type of a single address using a throw-away hex node.</summary>
		private object Guess(Dictionary<string, object> p)
		{
			MemoryApi.RequireProcess();

			var address = Params.GetAddress(p, "address");

			return UiThread.Invoke(() =>
			{
				var classNode = CreateProbeClass(address, IntPtr.Size, out var memory);

				var hexNode = classNode.Nodes.OfType<BaseHexNode>().FirstOrDefault();
				if (hexNode == null)
				{
					throw new RpcException(RpcException.CodeInternal, "failed to build the probe node");
				}

				if (!NodeDissector.GuessNode(hexNode, Process, memory, out var guessed) || guessed == null)
				{
					return (object)new Dictionary<string, object>
					{
						{ "type", null },
						{ "reason", null }
					};
				}

				return (object)new Dictionary<string, object>
				{
					{ "type", NodeTypes.ApiName(guessed.GetType()) },
					{ "reason", DescribeGuess(guessed, address) }
				};
			});
		}

		/// <summary>A short human readable explanation of a guessed node type.</summary>
		private static string DescribeGuess(BaseNode guessed, IntPtr address)
		{
			switch (guessed)
			{
				case Utf8TextNode _:
					return "bytes look like printable single byte text";
				case Utf16TextNode _:
					return "bytes look like printable double byte text";
				case FloatNode _:
					return "value is in a plausible float range";
				case DoubleNode _:
					return "value is in a plausible double range";
				case Int32Node _:
					return "value is a small integer";
			}

			var target = Process.ReadRemoteIntPtr(address);
			var section = Process.GetSectionToPointer(target);
			var module = Process.GetModuleToPointer(target);
			var where = module != null ? $" into {module.Name}" : (section != null ? $" into {section.Name}" : string.Empty);

			switch (guessed)
			{
				case FunctionPtrNode _:
					return $"pointer{where} code section";
				case VirtualMethodTableNode _:
					return $"pointer{where} to a table of code pointers";
				case Utf8TextPtrNode _:
					return $"pointer{where} to printable single byte text";
				case Utf16TextPtrNode _:
					return $"pointer{where} to printable double byte text";
				case PointerNode _:
					return $"pointer{where} data section";
				default:
					return null;
			}
		}

		// ------------------------------------------------------------------
		// analysis.pointer_preview
		// ------------------------------------------------------------------

		/// <summary>
		/// The data behind an address, the way <c>MemoryPreviewPopUp</c> shows
		/// it: the raw bytes plus the section/module they live in plus the
		/// dissected node types of the covered range.
		/// </summary>
		private object PointerPreview(Dictionary<string, object> p)
		{
			MemoryApi.RequireProcess();

			var address = Params.GetAddress(p, "address");
			var size = Params.GetOptional(p, "size", 64);

			if (size <= 0 || size > MaxPreviewSize)
			{
				throw RpcException.BadArgument($"'size' must be between 1 and {MaxPreviewSize}");
			}

			var data = new byte[size];
			var readable = Process.ReadRemoteMemoryIntoBuffer(address, ref data);

			var section = Process.GetSectionToPointer(address);
			var module = Process.GetModuleToPointer(address);

			var guessed = readable ? GuessRange(address, size) : new List<object>();

			return new Dictionary<string, object>
			{
				{ "address", Json.Address(address) },
				{ "size", size },
				{ "readable", readable },
				{ "data_b64", readable ? System.Convert.ToBase64String(data) : null },
				{
					"section", section == null ? null : new Dictionary<string, object>
					{
						{ "name", section.Name },
						{ "start", Json.Address(section.Start) },
						{ "end", Json.Address(section.End) },
						{ "size", section.Size.ToInt64() },
						{ "category", section.Category.ToString() },
						{ "protection", section.Protection.ToString() },
						{ "type", section.Type.ToString() },
						{ "module_name", section.ModuleName }
					}
				},
				{
					"module", module == null ? null : new Dictionary<string, object>
					{
						{ "name", module.Name },
						{ "path", module.Path },
						{ "start", Json.Address(module.Start) },
						{ "end", Json.Address(module.End) },
						{ "size", module.Size.ToInt64() }
					}
				},
				{ "guessed", guessed }
			};
		}

		/// <summary>Runs the dissector over a temporary class covering the range.</summary>
		private static List<object> GuessRange(IntPtr address, int size)
		{
			return UiThread.Invoke(() =>
			{
				var classNode = CreateProbeClass(address, size, out var memory);

				var result = new List<object>();
				foreach (var hexNode in classNode.Nodes.OfType<BaseHexNode>().ToList())
				{
					var offset = hexNode.Offset;

					string type = null;
					if (NodeDissector.GuessNode(hexNode, Process, memory, out var guessed) && guessed != null)
					{
						type = NodeTypes.ApiName(guessed.GetType());
						classNode.ReplaceChildNode(hexNode, guessed);
					}

					var replacement = classNode.Nodes.FirstOrDefault(n => n.Offset == offset);

					result.Add(new Dictionary<string, object>
					{
						{ "offset", offset },
						{ "type", type ?? NodeTypes.ApiName(hexNode.GetType()) },
						{ "value", replacement == null ? null : ReadValueSafe(replacement, memory) }
					});
				}

				return result;
			});
		}

		private static object ReadValueSafe(BaseNode node, MemoryBuffer memory)
		{
			try
			{
				var dto = NodeDto.ToDto(node, memory, 0, true);
				return dto.TryGetValue("value", out var value) ? value : null;
			}
			catch (Exception)
			{
				return null;
			}
		}

		// ------------------------------------------------------------------
		// analysis.disassemble / rtti / named_address
		// ------------------------------------------------------------------

		private object Disassemble(Dictionary<string, object> p)
		{
			MemoryApi.RequireProcess();

			var address = Params.GetAddress(p, "address");
			var asFunction = Params.GetOptional(p, "function", false);

			// A function is disassembled until its end, so the default window is
			// much wider than the 64 bytes a plain code dump defaults to.
			var length = Params.GetOptional(p, "length", asFunction ? 4096 : 64);

			if (length <= 0 || length > MaxDisassembleLength)
			{
				throw RpcException.BadArgument($"'length' must be between 1 and {MaxDisassembleLength}");
			}

			// 'max_instructions' is honoured on both paths: the function
			// disassembler has no such parameter, so its output is truncated here.
			var maxInstructions = Params.GetOptional(p, "max_instructions", MaxInstructions);
			if (maxInstructions <= 0 || maxInstructions > MaxInstructions)
			{
				throw RpcException.BadArgument($"'max_instructions' must be between 1 and {MaxInstructions}");
			}

			IReadOnlyList<DisassembledInstruction> instructions;
			if (asFunction)
			{
				instructions = disassembler.Value.RemoteDisassembleFunction(Process, address, length);
			}
			else
			{
				instructions = disassembler.Value.RemoteDisassembleCode(Process, address, length, maxInstructions);
			}

			return (instructions ?? new List<DisassembledInstruction>())
				.Take(maxInstructions)
				.Select(i => (object)new Dictionary<string, object>
				{
					{ "address", Json.Address(i.Address) },
					{ "length", i.Length },
					{ "bytes_hex", i.Data == null ? null : string.Concat(i.Data.Take(i.Length).Select(b => b.ToString("X2", CultureInfo.InvariantCulture))) },
					{ "text", i.Instruction }
				})
				.ToList();
		}

		private object Rtti(Dictionary<string, object> p)
		{
			MemoryApi.RequireProcess();

			var address = Params.GetAddress(p, "address");

			string rtti;
			try
			{
				rtti = Process.ReadRemoteRuntimeTypeInformation(address);
			}
			catch (Exception)
			{
				rtti = null;
			}

			return new Dictionary<string, object> { { "rtti", rtti } };
		}

		private object NamedAddress(Dictionary<string, object> p)
		{
			MemoryApi.RequireProcess();

			var address = Params.GetAddress(p, "address");

			var name = Process.GetNamedAddress(address);

			return new Dictionary<string, object> { { "name", string.IsNullOrEmpty(name) ? null : name } };
		}
	}
}
