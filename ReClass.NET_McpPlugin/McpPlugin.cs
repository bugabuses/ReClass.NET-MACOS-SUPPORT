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
		private ScannerApi scannerApi;

		public override bool Initialize(IPluginHost pluginHost)
		{
			host = pluginHost ?? throw new ArgumentNullException(nameof(pluginHost));

			try
			{
				Endpoint.Logger = Log;

				UiThread.Initialize(host.MainWindow);

				var dispatcher = new RpcDispatcher();

				// Each API group registers its own methods.
				new SystemApi().Register(dispatcher);
				new ProcessApi().Register(dispatcher);
				new MemoryApi().Register(dispatcher);
				new ProjectApi().Register(dispatcher);
				new ClassApi().Register(dispatcher);
				new NodeApi().Register(dispatcher);
				new EnumApi().Register(dispatcher);
				new CodeGenApi().Register(dispatcher);

				scannerApi = new ScannerApi();
				scannerApi.Register(dispatcher);
				new AnalysisApi().Register(dispatcher);

				var token = Endpoint.GenerateToken();

				server = new TcpJsonRpcServer(dispatcher, token, Log);
				server.Start();

				endpointPath = Endpoint.Write(server.Port, token);

				Log(endpointPath != null
					? $"mcp: listening on 127.0.0.1:{server.Port}, endpoint file {endpointPath}"
					: $"mcp: listening on 127.0.0.1:{server.Port}, but the endpoint file could not be secured and was not written");
			}
			catch (Exception ex)
			{
				Log($"mcp: failed to start: {ex.Message}");

				host.Logger.Log(ex);

				// Endpoint.Write (or anything after Start) may have thrown with
				// the listener already up; do not leave an unreachable server
				// running when Initialize reports failure.
				try
				{
					server?.Stop();
				}
				catch (Exception)
				{
					// ignored
				}
				server = null;

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

			try
			{
				scannerApi?.Dispose();
			}
			catch (Exception)
			{
				// ignored
			}
			scannerApi = null;

			if (endpointPath != null)
			{
				Endpoint.Delete();
				endpointPath = null;
			}

			UiThread.Terminate();

			Endpoint.Logger = null;

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
		}
	}
}
