using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using McpPlugin.Rpc;
using ReClassNET;
using ReClassNET.DataExchange.ReClass;
using ReClassNET.Nodes;
using ReClassNET.Project;

namespace McpPlugin.Api
{
	/// <summary>
	/// Shared helpers for the API groups which touch the project or the node
	/// tree. Everything here must run inside <see cref="UiThread.Invoke"/>.
	/// </summary>
	internal static class ProjectAccess
	{
		private static FieldInfo memoryViewControlField;

		public static ReClassNetProject Project
		{
			get
			{
				var project = Program.MainForm?.CurrentProject;
				if (project == null)
				{
					throw new RpcException(RpcException.CodeInternal, "no project is loaded");
				}
				return project;
			}
		}

		/// <summary>The <c>project.info.classes</c> entry of a class.</summary>
		public static Dictionary<string, object> DescribeClass(ClassNode node)
		{
			return new Dictionary<string, object>
			{
				{ "name", node.Name },
				{ "uuid", node.Uuid.ToString() },
				{ "address_formula", node.AddressFormula },
				{ "size", node.MemorySize },
				{ "node_count", node.Nodes.Count }
			};
		}

		/// <summary>
		/// Repaints the GUI after a model change.
		///
		/// <c>MainForm.memoryViewControl</c> is a private designer field with no
		/// public accessor, so it is fetched by reflection to force an immediate
		/// repaint; the plain <c>Invalidate(true)</c> is always done as well and
		/// is enough on its own (the control also repaints on its timer).
		/// </summary>
		public static void Refresh()
		{
			var form = Program.MainForm;
			if (form == null)
			{
				return;
			}

			try
			{
				if (memoryViewControlField == null)
				{
					memoryViewControlField = form.GetType().GetField("memoryViewControl", BindingFlags.Instance | BindingFlags.NonPublic);
				}

				if (memoryViewControlField?.GetValue(form) is Control control)
				{
					control.Invalidate();
				}
			}
			catch (Exception)
			{
				// ignored, Invalidate below is the fallback
			}

			form.Invalidate(true);
		}
	}

	/// <summary>Project lifetime: create, load, save and describe.</summary>
	public class ProjectApi
	{
		public void Register(RpcDispatcher dispatcher)
		{
			dispatcher.Register("project.new", New);
			dispatcher.Register("project.load", Load);
			dispatcher.Register("project.save", Save);
			dispatcher.Register("project.info", Info);
		}

		private object New(Dictionary<string, object> p)
		{
			return UiThread.Invoke(() =>
			{
				Program.MainForm.SetProject(new ReClassNetProject());

				ProjectAccess.Refresh();

				return (object)Json.Ok();
			});
		}

		/// <summary>
		/// The extensions <c>MainForm.LoadProjectFromPath</c> knows an importer
		/// for. Anything else makes it log "unknown type" and return — after it
		/// has already replaced the current project — so it is rejected here.
		/// </summary>
		private static readonly string[] LoadableExtensions =
		{
			ReClassNetFile.FileExtension,
			ReClassQtFile.FileExtension,
			ReClassFile.FileExtension
		};

		private object Load(Dictionary<string, object> p)
		{
			var path = Params.Get<string>(p, "path");

			// Validated *before* the call: LoadProjectFromPath swaps in a fresh
			// project first and only then discovers it can not read the file,
			// so a bad path would silently wipe the loaded project.
			var extension = System.IO.Path.GetExtension(path)?.ToLowerInvariant() ?? string.Empty;
			if (Array.IndexOf(LoadableExtensions, extension) < 0)
			{
				throw RpcException.BadArgument(
					$"'{path}' has an unsupported project extension, expected one of {string.Join(", ", LoadableExtensions)}");
			}

			if (!System.IO.File.Exists(path))
			{
				throw RpcException.NotFound($"no such file '{path}'");
			}

			return UiThread.Invoke(() =>
			{
				Program.MainForm.LoadProjectFromPath(path);

				ProjectAccess.Refresh();

				var project = ProjectAccess.Project;

				return (object)new Dictionary<string, object>
				{
					{ "path", project.Path },
					{ "classes", project.Classes.Select(c => (object)ProjectAccess.DescribeClass(c)).ToList() }
				};
			});
		}

		private object Save(Dictionary<string, object> p)
		{
			return UiThread.Invoke(() =>
			{
				var project = ProjectAccess.Project;

				var path = Params.GetOptional(p, "path", project.Path);
				if (string.IsNullOrEmpty(path))
				{
					throw RpcException.BadArgument("the project has no path yet, pass 'path'");
				}

				new ReClassNetFile(project).Save(path, Program.Logger);

				project.Path = path;

				return (object)new Dictionary<string, object> { { "path", path } };
			});
		}

		private object Info(Dictionary<string, object> p)
		{
			return UiThread.Invoke(() =>
			{
				var project = ProjectAccess.Project;

				return (object)new Dictionary<string, object>
				{
					{ "path", project.Path },
					{ "classes", project.Classes.Select(c => (object)ProjectAccess.DescribeClass(c)).ToList() },
					{ "enums", project.Enums.Select(e => (object)e.Name).ToList() }
				};
			});
		}
	}
}
