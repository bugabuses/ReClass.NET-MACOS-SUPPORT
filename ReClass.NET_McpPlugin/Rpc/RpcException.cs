using System;
using System.Collections.Generic;

namespace McpPlugin.Rpc
{
	/// <summary>An exception carrying a JSON-RPC 2.0 error object.</summary>
	public class RpcException : Exception
	{
		public const int CodeNoProcess = -32001;
		public const int CodeBadArgument = -32002;

		public const int CodeNotFound = -32003;
		public const int CodeInternal = -32004;
		public const int CodeReferenced = -32005;
		public const int CodeBusy = -32006;
		public const int CodeUnauthorized = -32007;

		public int Code { get; }

		public object ErrorData { get; }

		public RpcException(int code, string message)
			: this(code, message, null)
		{
		}

		public RpcException(int code, string message, object data)
			: base(message)
		{
			Code = code;
			ErrorData = data;
		}

		public static RpcException NoProcess()
		{
			return new RpcException(CodeNoProcess, "no process attached");
		}

		/// <summary>A malformed or rejected argument (JSON-RPC code -32002).</summary>
		public static RpcException BadArgument(string message)
		{
			return new RpcException(CodeBadArgument, message);
		}

		/// <summary>
		/// Thin alias of <see cref="BadArgument"/>, kept for the places where
		/// the rejected argument really is an address.
		/// </summary>
		public static RpcException BadAddress(string message)
		{
			return BadArgument(message);
		}

		public static RpcException NotFound(string message)
		{
			return new RpcException(CodeNotFound, message);
		}

		public static RpcException Referenced(string message, IEnumerable<object> references)
		{
			return new RpcException(CodeReferenced, message, new Dictionary<string, object> { { "references", new List<object>(references) } });
		}

		public static RpcException Busy(string message)
		{
			return new RpcException(CodeBusy, message);
		}
	}
}
