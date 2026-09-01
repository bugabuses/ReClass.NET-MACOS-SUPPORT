using System;
using McpPlugin.Api;
using McpPlugin.Rpc;
using ReClassNET.Logger;
using ReClassNET.Plugins;

namespace McpPlugin
{
	/// <summary>
	/// Hosts a local JSON-RPC server inside the running ReClass.NET process so
	/// external tools (the Python MCP bridge) can drive ReClass.NET.
	///
	/// The type name is dictated by <c>PluginManager.CreatePluginInstance</c>:
	/// it instantiates "&lt;FileName&gt;.&lt;FileName&gt;Ext", so the assembly
	/// must be McpPlugin.dll and this class McpPlugin.McpPluginExt.
	/// </summary>
	public class McpPluginExt : Plugin
	{
		private IPluginHost host;
		private TcpJsonRpcServer server;
		private string endpointPath;

		public override bool Initialize(IPluginHost pluginHost)
		{
			host = pluginHost ?? throw new ArgumentNullException(nameof(pluginHost));

			try
			{
				UiThread.Initialize(host.MainWindow);

				var dispatcher = new RpcDispatcher();

				// Each API group registers its own methods; later chunks add
				// more groups here (project, nodes, codegen, scanner, analysis).
				new SystemApi().Register(dispatcher);
				new ProcessApi().Register(dispatcher);
				new MemoryApi().Register(dispatcher);

				var token = Endpoint.GenerateToken();

				server = new TcpJsonRpcServer(dispatcher, token, Log);
				server.Start();

				endpointPath = Endpoint.Write(server.Port, token);

				Log($"mcp: listening on 127.0.0.1:{server.Port}, endpoint file {endpointPath}");
			}
			catch (Exception ex)
			{
				Log($"mcp: failed to start: {ex.Message}");

				host.Logger.Log(ex);

				return false;
			}

			return true;
		}

		public override void Terminate()
		{
			try
			{
				server?.Stop();
			}
			catch (Exception)
			{
				// ignored
			}
			server = null;

			Endpoint.Delete();

			host = null;
		}

		private void Log(string message)
		{
			try
			{
				host?.Logger.Log(LogLevel.Information, message);
			}
			catch (Exception)
			{
				// ignored
			}

			Console.WriteLine(message);
		}
	}
}
