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
		/// <summary>
		/// Longest accepted request line, matching the spec's 16 MiB transfer
		/// cap. The limit is enforced while reading, so an over-long line is
		/// never materialised.
		/// </summary>
		public const int MaxLineLength = 16 * 1024 * 1024;

		/// <summary>Maximum number of simultaneously connected clients.</summary>
		public const int MaxClients = 16;

		/// <summary>
		/// How long a freshly accepted connection has to send its <c>auth</c>
		/// line before it is dropped. Keeps an idle (or hostile) socket from
		/// occupying one of the <see cref="MaxClients"/> slots for free.
		/// </summary>
		public const int AuthTimeoutMilliseconds = 5000;

		/// <summary>
		/// How long an authenticated connection may stay silent before it is
		/// dropped. The Python bridge keeps one connection for the life of the
		/// process, so this only reaps genuinely abandoned sockets.
		/// </summary>
		public const int IdleTimeoutMilliseconds = 10 * 60 * 1000;

		private readonly RpcDispatcher dispatcher;
		private readonly string token;
		private readonly Action<string> log;

		private readonly List<TcpClient> clients = new List<TcpClient>();
		private readonly List<Thread> clientThreads = new List<Thread>();

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

		/// <summary>
		/// Stops accepting, closes every open client socket (which unblocks the
		/// client threads' reads) and joins the accept thread.
		/// </summary>
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
			listener = null;

			List<Thread> threads;
			lock (clients)
			{
				foreach (var client in clients)
				{
					CloseQuietly(client);
				}
				clients.Clear();

				threads = new List<Thread>(clientThreads);
				clientThreads.Clear();
			}

			var accept = acceptThread;
			acceptThread = null;
			if (accept != null && accept.IsAlive)
			{
				try
				{
					accept.Join(2000);
				}
				catch (Exception)
				{
					// ignored
				}
			}

			foreach (var thread in threads)
			{
				try
				{
					if (thread.IsAlive)
					{
						thread.Join(1000);
					}
				}
				catch (Exception)
				{
					// ignored
				}
			}
		}

		private static void CloseQuietly(TcpClient client)
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

				var thread = new Thread(() => ClientLoop(client))
				{
					IsBackground = true,
					Name = "McpPlugin.Client"
				};

				lock (clients)
				{
					if (clients.Count >= MaxClients)
					{
						log($"mcp: refusing a client, the limit of {MaxClients} concurrent connections is reached");

						CloseQuietly(client);
						continue;
					}

					clients.Add(client);
					clientThreads.Add(thread);

					// Threads of clients which already went away.
					clientThreads.RemoveAll(t => !t.IsAlive && t != thread);
				}

				thread.Start();
			}
		}

		private void ClientLoop(TcpClient client)
		{
			try
			{
				client.NoDelay = true;

				// Unauthenticated connections get a short deadline; the timeout
				// is widened to the idle timeout once auth succeeds. A blown
				// deadline surfaces as an IOException out of Stream.Read, which
				// the catch below turns into a plain disconnect.
				client.ReceiveTimeout = AuthTimeoutMilliseconds;

				using (var stream = client.GetStream())
				using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" })
				{
					var reader = new BoundedLineReader(stream, MaxLineLength);

					var authenticated = false;

					string line;
					while (running && (line = reader.ReadLine()) != null)
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

							client.ReceiveTimeout = IdleTimeoutMilliseconds;
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
			catch (LineTooLongException ex)
			{
				log($"mcp: {ex.Message}, closing the connection");
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
					clientThreads.RemoveAll(t => !t.IsAlive);
				}
				CloseQuietly(client);
			}
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

	/// <summary>Raised when a client sends a line longer than the transfer cap.</summary>
	public class LineTooLongException : IOException
	{
		public LineTooLongException(string message)
			: base(message)
		{
		}
	}

	/// <summary>
	/// Reads newline-delimited UTF-8 lines straight off a stream with a hard
	/// byte budget. Unlike <see cref="StreamReader.ReadLine"/> this never
	/// materialises the over-long line: the budget is checked as bytes arrive
	/// and the read is aborted the moment it is exceeded.
	/// </summary>
	internal class BoundedLineReader
	{
		private const int ChunkSize = 8192;

		private readonly Stream stream;
		private readonly int maxLineLength;
		private readonly byte[] chunk = new byte[ChunkSize];
		private readonly UTF8Encoding encoding = new UTF8Encoding(false);

		private int available;
		private int position;

		public BoundedLineReader(Stream stream, int maxLineLength)
		{
			this.stream = stream;
			this.maxLineLength = maxLineLength;
		}

		/// <summary>The next line without its terminator, or null at end of stream.</summary>
		public string ReadLine()
		{
			using (var line = new MemoryStream())
			{
				while (true)
				{
					if (position >= available)
					{
						available = stream.Read(chunk, 0, ChunkSize);
						position = 0;

						if (available <= 0)
						{
							return line.Length == 0 ? null : Materialise(line);
						}
					}

					var start = position;
					while (position < available && chunk[position] != (byte)'\n')
					{
						++position;
					}

					var count = position - start;
					if (line.Length + count > maxLineLength)
					{
						throw new LineTooLongException(
							$"a client sent a request line longer than the {maxLineLength} byte limit");
					}
					line.Write(chunk, start, count);

					if (position < available)
					{
						++position; // consume the '\n'
						return Materialise(line);
					}
				}
			}
		}

		private string Materialise(MemoryStream line)
		{
			var bytes = line.ToArray();

			var length = bytes.Length;
			if (length > 0 && bytes[length - 1] == (byte)'\r')
			{
				--length; // tolerate CRLF
			}

			return encoding.GetString(bytes, 0, length);
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
