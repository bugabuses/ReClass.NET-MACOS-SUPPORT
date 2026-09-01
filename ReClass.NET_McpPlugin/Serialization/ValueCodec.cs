using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using McpPlugin.Rpc;

namespace McpPlugin.Serialization
{
	/// <summary>
	/// Converts between the RPC type names and little-endian raw bytes.
	/// Numbers are returned as JSON numbers; 64 bit integers and pointers are
	/// returned as strings so no precision is lost in JSON clients.
	/// </summary>
	public static class ValueCodec
	{
		/// <summary>Size in bytes of one element of the given type, or -1 for variable-length text.</summary>
		public static int SizeOf(string type)
		{
			switch (Normalize(type))
			{
				case "int8":
				case "uint8":
				case "bool":
					return 1;
				case "int16":
				case "uint16":
					return 2;
				case "int32":
				case "uint32":
				case "float":
					return 4;
				case "int64":
				case "uint64":
				case "double":
					return 8;
				case "ptr":
					return IntPtr.Size;
				case "utf8":
				case "utf16":
				case "utf32":
					return -1;
				default:
					throw RpcException.BadArgument($"unknown value type '{type}'");
			}
		}

		public static bool IsText(string type)
		{
			return SizeOf(type) < 0;
		}

		/// <summary>
		/// Bytes per character of a text encoding. Throws -32002 for anything
		/// that is not a text type — the single place that knows this mapping.
		/// </summary>
		public static int CharSizeOf(string type)
		{
			switch (Normalize(type))
			{
				case "utf8":
					return 1;
				case "utf16":
					return 2;
				case "utf32":
					return 4;
				default:
					throw RpcException.BadArgument($"'{type}' is not a text type, expected utf8, utf16 or utf32");
			}
		}

		public static Encoding EncodingOf(string type)
		{
			switch (Normalize(type))
			{
				case "utf8":
					return Encoding.UTF8;
				case "utf16":
					return Encoding.Unicode;
				case "utf32":
					return Encoding.UTF32;
				default:
					throw RpcException.BadArgument($"'{type}' is not a text type");
			}
		}

		/// <summary>Decodes one element at <paramref name="offset"/> in <paramref name="data"/>.</summary>
		public static object Decode(string type, byte[] data, int offset)
		{
			switch (Normalize(type))
			{
				case "int8":
					return (int)unchecked((sbyte)data[offset]);
				case "uint8":
					return (int)data[offset];
				case "int16":
					return (int)BitConverter.ToInt16(data, offset);
				case "uint16":
					return (int)BitConverter.ToUInt16(data, offset);
				case "int32":
					return BitConverter.ToInt32(data, offset);
				case "uint32":
					return (long)BitConverter.ToUInt32(data, offset);
				case "int64":
					return BitConverter.ToInt64(data, offset).ToString(CultureInfo.InvariantCulture);
				case "uint64":
					return BitConverter.ToUInt64(data, offset).ToString(CultureInfo.InvariantCulture);
				case "float":
					return BitConverter.ToSingle(data, offset);
				case "double":
					return BitConverter.ToDouble(data, offset);
				case "bool":
					return data[offset] != 0;
				case "ptr":
					return Json.Address(IntPtr.Size == 8
						? new IntPtr(BitConverter.ToInt64(data, offset))
						: new IntPtr(BitConverter.ToInt32(data, offset)));
				default:
					throw RpcException.BadArgument($"unknown value type '{type}'");
			}
		}

		/// <summary>Decodes <paramref name="count"/> consecutive elements.</summary>
		public static List<object> DecodeAll(string type, byte[] data, int count)
		{
			var size = SizeOf(type);
			var values = new List<object>(count);
			for (var i = 0; i < count; ++i)
			{
				values.Add(Decode(type, data, i * size));
			}
			return values;
		}

		/// <summary>Encodes a JSON value into little-endian bytes.</summary>
		public static byte[] Encode(string type, object value)
		{
			var normalized = Normalize(type);
			if (IsText(normalized))
			{
				return EncodingOf(normalized).GetBytes(System.Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
			}

			try
			{
				switch (normalized)
				{
					case "int8":
						return new[] { unchecked((byte)System.Convert.ToSByte(value, CultureInfo.InvariantCulture)) };
					case "uint8":
						return new[] { System.Convert.ToByte(value, CultureInfo.InvariantCulture) };
					case "int16":
						return BitConverter.GetBytes(System.Convert.ToInt16(value, CultureInfo.InvariantCulture));
					case "uint16":
						return BitConverter.GetBytes(System.Convert.ToUInt16(value, CultureInfo.InvariantCulture));
					case "int32":
						return BitConverter.GetBytes(System.Convert.ToInt32(value, CultureInfo.InvariantCulture));
					case "uint32":
						return BitConverter.GetBytes(System.Convert.ToUInt32(value, CultureInfo.InvariantCulture));
					case "int64":
						return BitConverter.GetBytes(System.Convert.ToInt64(value, CultureInfo.InvariantCulture));
					case "uint64":
						return BitConverter.GetBytes(System.Convert.ToUInt64(value, CultureInfo.InvariantCulture));
					case "float":
						return BitConverter.GetBytes(System.Convert.ToSingle(value, CultureInfo.InvariantCulture));
					case "double":
						return BitConverter.GetBytes(System.Convert.ToDouble(value, CultureInfo.InvariantCulture));
					case "bool":
						return new[] { (byte)(System.Convert.ToBoolean(value, CultureInfo.InvariantCulture) ? 1 : 0) };
					case "ptr":
					{
						var address = Params.ParseAddress(value, "value");
						return IntPtr.Size == 8
							? BitConverter.GetBytes(address.ToInt64())
							: BitConverter.GetBytes(address.ToInt32());
					}
					default:
						throw RpcException.BadArgument($"unknown value type '{type}'");
				}
			}
			catch (Exception ex) when (ex is FormatException || ex is InvalidCastException || ex is OverflowException)
			{
				throw RpcException.BadArgument($"value is not convertible to '{type}': {ex.Message}");
			}
		}

		/// <summary>Decodes a NUL-terminated string of the given encoding from a raw buffer.</summary>
		public static string DecodeString(string type, byte[] data)
		{
			var encoding = EncodingOf(type);
			var charSize = CharSizeOf(type);

			var length = data.Length - data.Length % charSize;
			for (var i = 0; i + charSize <= length; i += charSize)
			{
				var isZero = true;
				for (var j = 0; j < charSize; ++j)
				{
					if (data[i + j] != 0)
					{
						isZero = false;
						break;
					}
				}
				if (isZero)
				{
					length = i;
					break;
				}
			}

			return encoding.GetString(data, 0, length);
		}

		private static string Normalize(string type)
		{
			if (type == null)
			{
				throw RpcException.BadArgument("missing value type");
			}
			return type.Trim().ToLowerInvariant();
		}
	}
}
