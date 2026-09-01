using System;
using System.Collections.Generic;
using System.Linq;
using McpPlugin.Rpc;
using McpPlugin.Serialization;
using ReClassNET;
using ReClassNET.Nodes;
using ReClassNET.Project;

namespace McpPlugin.Api
{
	/// <summary>Create, inspect and modify the classes of the current project.</summary>
	public class ClassApi
	{
		/// <summary>Size of a class created without an explicit size.</summary>
		public const int DefaultClassSize = 64;

		public void Register(RpcDispatcher dispatcher)
		{
			dispatcher.Register("class.list", List);
			dispatcher.Register("class.get", Get);
			dispatcher.Register("class.create", Create);
			dispatcher.Register("class.rename", Rename);
			dispatcher.Register("class.delete", Delete);
			dispatcher.Register("class.set_address", SetAddress);
			dispatcher.Register("class.select", Select);
			dispatcher.Register("class.add_bytes", AddBytes);
			dispatcher.Register("class.insert_bytes", InsertBytes);
		}

		private object List(Dictionary<string, object> p)
		{
			return UiThread.Invoke(() => (object)ProjectAccess.Project.Classes
				.Select(c => (object)ProjectAccess.DescribeClass(c))
				.ToList());
		}

		private object Get(Dictionary<string, object> p)
		{
			var depth = Params.GetOptional(p, "depth", 1);
			var withValues = Params.GetOptional(p, "with_values", true);

			return UiThread.Invoke(() =>
			{
				var classNode = NodeSelector.ResolveClass(ProjectAccess.Project, p);

				var memory = withValues ? NodeDto.CreateMemory(classNode) : null;

				var dto = NodeDto.ToDto(classNode, memory, Math.Max(depth, 0), withValues);
				dto["address_formula"] = classNode.AddressFormula;
				dto["uuid"] = classNode.Uuid.ToString();
				dto["address"] = ResolvedAddress(classNode);

				return (object)dto;
			});
		}

		/// <summary>The class' address formula evaluated, or null if it can't be resolved.</summary>
		private static string ResolvedAddress(ClassNode classNode)
		{
			var process = Program.RemoteProcess;
			if (process?.UnderlayingProcess == null)
			{
				return null;
			}

			try
			{
				return Json.Address(process.ParseAddress(classNode.AddressFormula));
			}
			catch (Exception)
			{
				return null;
			}
		}

		private object Create(Dictionary<string, object> p)
		{
			var name = Params.GetOptional<string>(p, "name", null);
			var addressFormula = Params.GetOptional<string>(p, "address_formula", null);
			var size = Params.GetOptional(p, "size", DefaultClassSize);

			if (size < 0)
			{
				throw RpcException.BadAddress("'size' must not be negative");
			}

			return UiThread.Invoke(() =>
			{
				// ClassNode.Create fires ClassCreated, which the main form has
				// wired to ReClassNetProject.AddClass, so the class shows up in
				// the project view without any further work.
				var classNode = ClassNode.Create();

				if (!string.IsNullOrEmpty(name))
				{
					classNode.Name = name;
				}
				if (!string.IsNullOrEmpty(addressFormula))
				{
					classNode.AddressFormula = addressFormula;
				}

				classNode.BeginUpdate();
				classNode.AddBytes(size);
				classNode.EndUpdate();

				classNode.UpdateOffsets();

				Program.MainForm.CurrentClassNode = classNode;

				ProjectAccess.Refresh();

				return (object)ProjectAccess.DescribeClass(classNode);
			});
		}

		private object Rename(Dictionary<string, object> p)
		{
			var name = Params.Get<string>(p, "name");

			return UiThread.Invoke(() =>
			{
				NodeSelector.ResolveClass(ProjectAccess.Project, p).Name = name;

				ProjectAccess.Refresh();

				return (object)ProjectAccess.Ok();
			});
		}

		private object Delete(Dictionary<string, object> p)
		{
			var force = Params.GetOptional(p, "force", false);

			return UiThread.Invoke(() =>
			{
				var project = ProjectAccess.Project;
				var classNode = NodeSelector.ResolveClass(project, p);

				try
				{
					project.Remove(classNode);
				}
				catch (ClassReferencedException ex)
				{
					if (!force)
					{
						throw RpcException.Referenced(
							ex.Message,
							ex.References.Select(c => (object)c.Name)
						);
					}

					// Drop the referencing nodes first, then remove the class.
					foreach (var reference in ex.References.ToList())
					{
						reference.BeginUpdate();
						foreach (var child in reference.Nodes
							.OfType<BaseWrapperNode>()
							.Where(w => w.ResolveMostInnerNode() == classNode)
							.Cast<BaseNode>()
							.ToList())
						{
							var size = child.MemorySize;
							reference.RemoveNode(child);
							reference.AddBytes(size);
						}
						reference.EndUpdate();
						reference.UpdateOffsets();
					}

					project.Remove(classNode);
				}

				ProjectAccess.Refresh();

				return (object)ProjectAccess.Ok();
			});
		}

		private object SetAddress(Dictionary<string, object> p)
		{
			var formula = Params.Get<string>(p, "address_formula");

			return UiThread.Invoke(() =>
			{
				var classNode = NodeSelector.ResolveClass(ProjectAccess.Project, p);

				classNode.AddressFormula = formula;

				ProjectAccess.Refresh();

				return (object)new Dictionary<string, object>
				{
					{ "ok", true },
					{ "resolved", ResolvedAddress(classNode) }
				};
			});
		}

		private object Select(Dictionary<string, object> p)
		{
			return UiThread.Invoke(() =>
			{
				Program.MainForm.CurrentClassNode = NodeSelector.ResolveClass(ProjectAccess.Project, p);

				ProjectAccess.Refresh();

				return (object)ProjectAccess.Ok();
			});
		}

		private object AddBytes(Dictionary<string, object> p)
		{
			var size = Params.Get<int>(p, "size");
			if (size <= 0)
			{
				throw RpcException.BadAddress("'size' must be positive");
			}

			return UiThread.Invoke(() =>
			{
				var classNode = NodeSelector.ResolveClass(ProjectAccess.Project, p);

				classNode.BeginUpdate();
				classNode.AddBytes(size);
				classNode.EndUpdate();

				classNode.UpdateOffsets();

				ProjectAccess.Refresh();

				return (object)ProjectAccess.Ok();
			});
		}

		private object InsertBytes(Dictionary<string, object> p)
		{
			var size = Params.Get<int>(p, "size");
			if (size <= 0)
			{
				throw RpcException.BadAddress("'size' must be positive");
			}

			return UiThread.Invoke(() =>
			{
				var node = NodeSelector.ResolveNodeParam(ProjectAccess.Project, p);

				var container = node.GetParentContainer();
				if (container == null)
				{
					throw RpcException.NotFound("the node has no parent container");
				}

				container.BeginUpdate();
				if (node is BaseContainerNode ownContainer && ReferenceEquals(container, node))
				{
					ownContainer.AddBytes(size);
				}
				else
				{
					container.InsertBytes(node, size);
				}
				container.EndUpdate();

				container.UpdateOffsets();

				ProjectAccess.Refresh();

				return (object)ProjectAccess.Ok();
			});
		}
	}
}
