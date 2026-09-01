using System;
using System.Collections.Generic;
using McpPlugin.Rpc;
using ReClassNET;

namespace McpPlugin.Api
{
	/// <summary>General information about the running ReClass.NET instance.</summary>
	public class SystemApi
	{
		public void Register(RpcDispatcher dispatcher)
		{
			dispatcher.Register("system.info", Info);
		}

		/// <summary>Touches the project, so it runs on the UI thread.</summary>
		private object Info(Dictionary<string, object> p)
		{
			return UiThread.Invoke(() =>
			{
				var project = Program.MainForm?.CurrentProject;

				return (object)new Dictionary<string, object>
				{
					{ "reclass_version", Constants.ApplicationVersion },
					// Constants.Platform is a compile-time constant of the host
					// assembly; report the actual runtime pointer width instead.
					{ "platform", IntPtr.Size == 8 ? "x64" : "x86" },
					{ "os", Environment.OSVersion.Platform.ToString() },
					{ "pid", System.Diagnostics.Process.GetCurrentProcess().Id },
					{ "process_attached", Program.RemoteProcess.UnderlayingProcess != null && Program.RemoteProcess.IsValid },
					{ "project_path", project?.Path },
					{ "class_count", project?.Classes.Count ?? 0 }
				};
			});
		}
	}
}
