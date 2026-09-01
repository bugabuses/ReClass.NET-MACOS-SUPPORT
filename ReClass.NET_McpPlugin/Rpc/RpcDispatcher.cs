using System;
using System.Collections.Generic;

namespace McpPlugin.Rpc
{
	/// <summary>
	/// Maps JSON-RPC method names to handlers and turns handler results and
	/// exceptions into JSON-RPC 2.0 response objects.
	/// </summary>
	public class RpcDispatcher
	{
		private readonly Dictionary<string, Func<Dictionary<string, object>, object>> handlers =
			new Dictionary<string, Func<Dictionary<string, object>, object>>(StringComparer.Ordinal);

		public void Register(string method, Func<Dictionary<string, object>, object> handler)
		{
			handlers[method] = handler;
		}

		public bool IsRegistered(string method)
		{
			return handlers.ContainsKey(method);
		}

		/// <summary>Dispatches one raw request line. Returns the response line, or null if nothing should be sent.</summary>
		public string DispatchLine(string line)
		{
			object request;
			try
			{
				request = Json.Deserialize(line);
			}
			catch (Exception)
			{
				return Json.Serialize(ErrorResponse(null, -32700, "parse error", null));
			}

			object response;
			if (request is object[] batch)
			{
				if (batch.Length == 0)
				{
					response = ErrorResponse(null, -32600, "invalid request", null);
				}
				else
				{
					var responses = new List<object>();
					foreach (var item in batch)
					{
						var single = DispatchObject(item);
						if (single != null)
						{
							responses.Add(single);
						}
					}
					if (responses.Count == 0)
					{
						return null;
					}
					response = responses;
				}
			}
			else
			{
				response = DispatchObject(request);
				if (response == null)
				{
					return null;
				}
			}

			return Json.Serialize(response);
		}

		/// <summary>
		/// Dispatches one parsed request object. Returns null for notifications
		/// only — a request is a notification when it carries no <c>id</c>
		/// member at all. An explicit <c>"id": null</c> is a request and is
		/// answered with <c>"id": null</c>, per JSON-RPC 2.0.
		/// </summary>
		public object DispatchObject(object request)
		{
			if (!(request is Dictionary<string, object> req))
			{
				return ErrorResponse(null, -32600, "invalid request", null);
			}

			req.TryGetValue("id", out var id);

			var isNotification = !req.ContainsKey("id");

			if (!req.TryGetValue("method", out var methodObj) || !(methodObj is string method))
			{
				return isNotification ? null : ErrorResponse(id, -32600, "invalid request", null);
			}

			Dictionary<string, object> parameters;
			if (req.TryGetValue("params", out var paramsObj) && paramsObj != null)
			{
				parameters = paramsObj as Dictionary<string, object>;
				if (parameters == null)
				{
					return isNotification ? null : ErrorResponse(id, -32602, "params must be an object", null);
				}
			}
			else
			{
				parameters = new Dictionary<string, object>();
			}

			if (!handlers.TryGetValue(method, out var handler))
			{
				return isNotification ? null : ErrorResponse(id, -32601, $"unknown method '{method}'", null);
			}

			object result;
			try
			{
				result = handler(parameters);
			}
			catch (Exception ex)
			{
				var response = ErrorFromException(id, ex);
				return isNotification ? null : response;
			}

			if (isNotification)
			{
				return null;
			}

			return new Dictionary<string, object>
			{
				{ "jsonrpc", "2.0" },
				{ "id", id },
				{ "result", result }
			};
		}

		private static object ErrorFromException(object id, Exception ex)
		{
			while (ex is AggregateException aggregate && aggregate.InnerExceptions.Count == 1)
			{
				ex = aggregate.InnerExceptions[0];
			}

			if (ex is RpcException rpc)
			{
				return ErrorResponse(id, rpc.Code, rpc.Message, rpc.ErrorData);
			}
			if (ex is ArgumentException || ex is FormatException || ex is OverflowException)
			{
				return ErrorResponse(id, RpcException.CodeBadAddress, ex.Message, null);
			}
			if (ex is KeyNotFoundException)
			{
				return ErrorResponse(id, RpcException.CodeNotFound, ex.Message, null);
			}
			return ErrorResponse(id, RpcException.CodeInternal, ex.Message, new Dictionary<string, object>
			{
				{ "exception", ex.GetType().Name }
			});
		}

		public static Dictionary<string, object> ErrorResponse(object id, int code, string message, object data)
		{
			var error = new Dictionary<string, object>
			{
				{ "code", code },
				{ "message", message ?? string.Empty }
			};
			if (data != null)
			{
				error["data"] = data;
			}

			return new Dictionary<string, object>
			{
				{ "jsonrpc", "2.0" },
				{ "id", id },
				{ "error", error }
			};
		}
	}
}
