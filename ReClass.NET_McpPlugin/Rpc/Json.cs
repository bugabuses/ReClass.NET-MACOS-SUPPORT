using System;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;

namespace McpPlugin.Rpc
{
	/// <summary>JSON (de)serialization on top of <see cref="JavaScriptSerializer"/>.</summary>
	public static class Json
	{
		private static JavaScriptSerializer CreateSerializer()
		{
			return new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 512 };
		}

		public static string Serialize(object value)
		{
			return CreateSerializer().Serialize(value);
		}

		/// <summary>Deserializes to <c>Dictionary&lt;string, object&gt;</c> / <c>object[]</c> / primitives.</summary>
		public static object Deserialize(string text)
		{
			return CreateSerializer().DeserializeObject(text);
		}

		/// <summary>
		/// Formats an address as a <c>"0x"</c>-prefixed, upper-case hex string,
		/// e.g. <c>"0x1F00A0"</c>.
		/// </summary>
		public static string Address(IntPtr address)
		{
			return "0x" + ((ulong)address.ToInt64()).ToString("X", CultureInfo.InvariantCulture);
		}

		public static Dictionary<string, object> Object()
		{
			return new Dictionary<string, object>();
		}

		/// <summary>The <c>{"ok": true}</c> result every mutation returns.</summary>
		public static Dictionary<string, object> Ok()
		{
			return new Dictionary<string, object> { { "ok", true } };
		}
	}

	/// <summary>Typed accessors for a request's <c>params</c> object.</summary>
	public static class Params
	{
		public static bool Has(Dictionary<string, object> p, string name)
		{
			return p != null && p.ContainsKey(name) && p[name] != null;
		}

		public static object GetRaw(Dictionary<string, object> p, string name)
		{
			if (!Has(p, name))
			{
				throw RpcException.BadArgument($"missing parameter '{name}'");
			}
			return p[name];
		}

		public static T Get<T>(Dictionary<string, object> p, string name)
		{
			return Convert<T>(GetRaw(p, name), name);
		}

		public static T GetOptional<T>(Dictionary<string, object> p, string name, T defaultValue)
		{
			if (!Has(p, name))
			{
				return defaultValue;
			}
			return Convert<T>(p[name], name);
		}

		private static T Convert<T>(object value, string name)
		{
			try
			{
				if (value is T typed)
				{
					return typed;
				}
				return (T)System.Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
			}
			catch (Exception)
			{
				throw RpcException.BadArgument($"parameter '{name}' has an unexpected type");
			}
		}

		/// <summary>Reads an address parameter. Accepts a number or a (optionally "0x"-prefixed) hex string.</summary>
		public static IntPtr GetAddress(Dictionary<string, object> p, string name)
		{
			return ParseAddress(GetRaw(p, name), name);
		}

		/// <summary>
		/// Parses an address. Addresses are unsigned: a negative number, or a
		/// string carrying a sign, is rejected with -32002.
		/// </summary>
		public static IntPtr ParseAddress(object value, string name)
		{
			if (value is string s)
			{
				s = s.Trim();
				if (s.StartsWith("-", StringComparison.Ordinal) || s.StartsWith("+", StringComparison.Ordinal))
				{
					throw RpcException.BadAddress($"parameter '{name}' must not be negative");
				}
				if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
				{
					s = s.Substring(2);
				}
				if (s.Length == 0 || !ulong.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed))
				{
					throw RpcException.BadAddress($"parameter '{name}' is not a valid address");
				}
				return new IntPtr(unchecked((long)parsed));
			}

			long number;
			try
			{
				number = System.Convert.ToInt64(value, CultureInfo.InvariantCulture);
			}
			catch (Exception)
			{
				throw RpcException.BadAddress($"parameter '{name}' is not a valid address");
			}

			if (number < 0)
			{
				throw RpcException.BadAddress($"parameter '{name}' must not be negative");
			}

			return new IntPtr(number);
		}

		public static List<object> GetList(Dictionary<string, object> p, string name)
		{
			var value = GetRaw(p, name);
			if (value is object[] array)
			{
				return new List<object>(array);
			}
			if (value is List<object> list)
			{
				return list;
			}
			throw RpcException.BadArgument($"parameter '{name}' must be an array");
		}

		public static Dictionary<string, object> AsObject(object value, string name)
		{
			if (value is Dictionary<string, object> dict)
			{
				return dict;
			}
			throw RpcException.BadArgument($"'{name}' must be an object");
		}
	}
}
