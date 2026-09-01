using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace McpPlugin.Rpc
{
	/// <summary>
	/// A newline-delimited JSON-RPC 2.0 server bound to 127.0.0.1 on an
	/// ephemeral port. One thread accepts, one thread per client dispatches.
	/// The first line a client sends must be an <c>auth</c> request carrying the
	/// shared token; anything else closes the connection.
	/// </summary>
	public class TcpJsonRpcServer
	{
		private const int MaxLineLength = 64 * 1024 * 1024;

		private readonly RpcDispatcher dispatcher;
		private readonly string token;
		private readonly Action<string> log;

		private readonly List<TcpClient> clients = new List<TcpClient>();

		private TcpListener listener;
		private Thread acceptThread;
		private volatile bool running;

		public int Port { get; private set; }

		public TcpJsonRpcServer(RpcDispatcher dispatcher, string token, Action<string> log)
		{
			this.dispatcher = dispatcher;
			this.token = token;
			this.log = log ?? (_ => { });
		}

		public void Start()
		{
			listener = new TcpListener(IPAddress.Loopback, 0);
			listener.Start();
			Port = ((IPEndPoint)listener.LocalEndpoint).Port;
			running = true;

			acceptThread = new Thread(AcceptLoop)
			{
				IsBackground = true,
				Name = "McpPlugin.Accept"
			};
			acceptThread.Start();
		}

		public void Stop()
		{
			running = false;

			try
			{
				listener?.Stop();
			}
			catch (Exception)
			{
				// ignored
			}

			lock (clients)
			{
				foreach (var client in clients)
				{
					try
					{
						client.Close();
					}
					catch (Exception)
					{
						// ignored
					}
				}
				clients.Clear();
			}
		}

		private void AcceptLoop()
		{
			while (running)
			{
				TcpClient client;
				try
				{
					client = listener.AcceptTcpClient();
				}
				catch (Exception)
				{
					if (running)
					{
						log("mcp: accept loop stopped");
					}
					return;
				}

				lock (clients)
				{
					clients.Add(client);
				}

				var thread = new Thread(() => ClientLoop(client))
				{
					IsBackground = true,
					Name = "McpPlugin.Client"
				};
				thread.Start();
			}
		}

		private void ClientLoop(TcpClient client)
		{
			try
			{
				client.NoDelay = true;

				using (var stream = client.GetStream())
				using (var reader = new StreamReader(stream, new UTF8Encoding(false), false, 8192))
				using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" })
				{
					var authenticated = false;

					string line;
					while (running && (line = ReadLine(reader)) != null)
					{
						if (line.Trim().Length == 0)
						{
							continue;
						}

						if (!authenticated)
						{
							var response = Authenticate(line, out authenticated);
							if (response != null)
							{
								writer.WriteLine(response);
							}
							if (!authenticated)
							{
								return;
							}
							continue;
						}

						var result = dispatcher.DispatchLine(line);
						if (result != null)
						{
							writer.WriteLine(result);
						}
					}
				}
			}
			catch (Exception)
			{
				// A client going away is not an error worth logging.
			}
			finally
			{
				lock (clients)
				{
					clients.Remove(client);
				}
				try
				{
					client.Close();
				}
				catch (Exception)
				{
					// ignored
				}
			}
		}

		private static string ReadLine(StreamReader reader)
		{
			var line = reader.ReadLine();
			if (line != null && line.Length > MaxLineLength)
			{
				throw new IOException("request line too long");
			}
			return line;
		}

		/// <summary>Validates the first line. On failure the caller closes the connection.</summary>
		private string Authenticate(string line, out bool authenticated)
		{
			authenticated = false;

			object request;
			try
			{
				request = Json.Deserialize(line);
			}
			catch (Exception)
			{
				return Json.Serialize(RpcDispatcher.ErrorResponse(null, -32700, "parse error", null));
			}

			if (!(request is Dictionary<string, object> req)
				|| !req.TryGetValue("method", out var method)
				|| !"auth".Equals(method as string, StringComparison.Ordinal))
			{
				return Json.Serialize(RpcDispatcher.ErrorResponse(
					(request as Dictionary<string, object>)?.TryGetValueOrNull("id"),
					RpcException.CodeUnauthorized,
					"the first request must be 'auth'",
					null));
			}

			req.TryGetValue("id", out var id);

			var parameters = req.TryGetValueOrNull("params") as Dictionary<string, object>;
			var provided = parameters?.TryGetValueOrNull("token") as string;

			if (provided == null || !FixedTimeEquals(provided, token))
			{
				log("mcp: rejected a client with an invalid token");

				return Json.Serialize(RpcDispatcher.ErrorResponse(id, RpcException.CodeUnauthorized, "invalid token", null));
			}

			authenticated = true;

			return Json.Serialize(new Dictionary<string, object>
			{
				{ "jsonrpc", "2.0" },
				{ "id", id },
				{ "result", new Dictionary<string, object>
					{
						{ "ok", true },
						{ "version", ReClassNET.Constants.ApplicationVersion }
					}
				}
			});
		}

		private static bool FixedTimeEquals(string a, string b)
		{
			if (a == null || b == null)
			{
				return false;
			}

			var difference = a.Length ^ b.Length;
			for (var i = 0; i < a.Length && i < b.Length; ++i)
			{
				difference |= a[i] ^ b[i];
			}
			return difference == 0;
		}
	}

	internal static class DictionaryExtensions
	{
		public static object TryGetValueOrNull(this Dictionary<string, object> dictionary, string key)
		{
			return dictionary != null && dictionary.TryGetValue(key, out var value) ? value : null;
		}
	}
}
