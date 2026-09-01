using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using McpPlugin.Rpc;
using ReClassNET;
using ReClassNET.Core;
using ReClassNET.Memory;

namespace McpPlugin.Api
{
	/// <summary>Process enumeration, attaching, detaching and control.</summary>
	public class ProcessApi
	{
		public void Register(RpcDispatcher dispatcher)
		{
			dispatcher.Register("process.list", List);
			dispatcher.Register("process.attach", Attach);
			dispatcher.Register("process.detach", Detach);
			dispatcher.Register("process.status", Status);
			dispatcher.Register("process.control", Control);
		}

		private static RemoteProcess Process => Program.RemoteProcess;

		internal static Dictionary<string, object> ToDto(ProcessInfo info)
		{
			return new Dictionary<string, object>
			{
				{ "id", info.Id.ToInt64() },
				{ "name", info.Name },
				{ "path", info.Path }
			};
		}

		/// <summary>Runs on the RPC client thread.</summary>
		private object List(Dictionary<string, object> p)
		{
			var filter = Params.GetOptional<string>(p, "filter", null);

			IEnumerable<ProcessInfo> processes = Program.CoreFunctions.EnumerateProcesses();
			if (!string.IsNullOrEmpty(filter))
			{
				processes = processes.Where(x => x.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
					|| x.Path.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
			}

			return processes.Select(ToDto).Cast<object>().ToList();
		}

		/// <summary>Attaching touches the main form, so it runs on the UI thread.</summary>
		private object Attach(Dictionary<string, object> p)
		{
			var processes = Program.CoreFunctions.EnumerateProcesses();

			ProcessInfo info;
			if (Params.Has(p, "id"))
			{
				var id = Params.ParseAddress(Params.GetRaw(p, "id"), "id");
				info = processes.FirstOrDefault(x => x.Id == id);
				if (info == null)
				{
					throw RpcException.NotFound($"no process with id {id.ToInt64().ToString(CultureInfo.InvariantCulture)}");
				}
			}
			else if (Params.Has(p, "name"))
			{
				var name = Params.Get<string>(p, "name");
				info = processes.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
					?? processes.FirstOrDefault(x => x.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
				if (info == null)
				{
					throw RpcException.NotFound($"no process named '{name}'");
				}
			}
			else
			{
				throw RpcException.BadArgument("either 'id' or 'name' is required");
			}

			var target = info;

			UiThread.Invoke(() => Program.MainForm.AttachToProcess(target));

			if (!Process.IsValid)
			{
				throw new RpcException(RpcException.CodeInternal, $"failed to attach to '{target.Name}'");
			}

			return ToDto(target);
		}

		private object Detach(Dictionary<string, object> p)
		{
			UiThread.Invoke(() => Process.Close());

			return Json.Ok();
		}

		/// <summary>Runs on the RPC client thread.</summary>
		private object Status(Dictionary<string, object> p)
		{
			var info = Process.UnderlayingProcess;

			var result = new Dictionary<string, object>
			{
				{ "attached", info != null },
				{ "is_valid", Process.IsValid }
			};

			if (info != null)
			{
				result["id"] = info.Id.ToInt64();
				result["name"] = info.Name;
				result["path"] = info.Path;
			}
			else
			{
				result["id"] = null;
				result["name"] = null;
				result["path"] = null;
			}

			return result;
		}

		/// <summary>Runs on the RPC client thread.</summary>
		private object Control(Dictionary<string, object> p)
		{
			MemoryApi.RequireProcess();

			var action = Params.Get<string>(p, "action");

			ControlRemoteProcessAction parsed;
			switch (action.Trim().ToLowerInvariant())
			{
				case "suspend":
					parsed = ControlRemoteProcessAction.Suspend;
					break;
				case "resume":
					parsed = ControlRemoteProcessAction.Resume;
					break;
				case "terminate":
					parsed = ControlRemoteProcessAction.Terminate;
					break;
				default:
					throw RpcException.BadArgument($"unknown action '{action}', expected suspend|resume|terminate");
			}

			Process.ControlRemoteProcess(parsed);

			return Json.Ok();
		}
	}
}
