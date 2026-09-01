using System;
using System.Collections.Generic;
using System.Linq;
using McpPlugin.Rpc;
using McpPlugin.Serialization;
using ReClassNET.Nodes;
using ReClassNET.Project;

namespace McpPlugin.Api
{
	/// <summary>Reads and mutates single nodes of the class tree.</summary>
	public class NodeApi
	{
		public void Register(RpcDispatcher dispatcher)
		{
			dispatcher.Register("node.get", Get);
			dispatcher.Register("node.change_type", ChangeType);
			dispatcher.Register("node.rename", Rename);
			dispatcher.Register("node.comment", Comment);
			dispatcher.Register("node.remove", Remove);
			dispatcher.Register("node.set_hidden", SetHidden);
			dispatcher.Register("node.set_array", SetArray);
			dispatcher.Register("node.set_bits", SetBits);
			dispatcher.Register("node.set_enum", SetEnum);
			dispatcher.Register("node.types", Types);
		}

		/// <summary>Builds the dto of a node with the memory of its parent class.</summary>
		private static object Describe(BaseNode node, int depth, bool withValues)
		{
			var classNode = node as ClassNode ?? node.GetParentClass();

			var memory = withValues && classNode != null ? NodeDto.CreateMemory(classNode) : null;

			return NodeDto.ToDto(node, memory, Math.Max(depth, 0), withValues);
		}

		private object Get(Dictionary<string, object> p)
		{
			var depth = Params.GetOptional(p, "depth", 1);
			var withValues = Params.GetOptional(p, "with_values", true);

			return UiThread.Invoke(() => Describe(NodeSelector.ResolveNodeParam(ProjectAccess.Project, p), depth, withValues));
		}

		private object ChangeType(Dictionary<string, object> p)
		{
			var type = NodeTypes.Resolve(Params.Get<string>(p, "type"));
			var innerTypeName = Params.GetOptional<string>(p, "inner_type", null);
			var classRef = Params.GetOptional<string>(p, "class_ref", null);

			return UiThread.Invoke(() =>
			{
				var project = ProjectAccess.Project;

				var node = NodeSelector.ResolveNodeParam(project, p);
				if (node is ClassNode)
				{
					throw RpcException.BadAddress("a class node can not change its type");
				}

				var container = node.GetParentContainer();
				if (container == null)
				{
					throw RpcException.NotFound("the node has no parent container");
				}

				// Initialize() gives wrapper nodes a sensible default inner node
				// (and creates a helper class for ClassInstance), matching what
				// the GUI does in MainForm.ReplaceSelectedNodesWithType. It is
				// skipped when the caller supplies the inner node itself, so no
				// throw-away class is added to the project.
				var hasExplicitInner = !string.IsNullOrEmpty(classRef) || !string.IsNullOrEmpty(innerTypeName);

				var newNode = BaseNode.CreateInstanceFromType(type, !hasExplicitInner);
				if (newNode == null)
				{
					throw RpcException.BadAddress($"the node type '{type.Name}' can not be instantiated");
				}

				if (newNode is BaseWrapperNode wrapper)
				{
					if (!string.IsNullOrEmpty(classRef))
					{
						wrapper.ChangeInnerNode(NodeSelector.ResolveClass(project, classRef));
					}
					else if (!string.IsNullOrEmpty(innerTypeName))
					{
						var innerNode = BaseNode.CreateInstanceFromType(NodeTypes.Resolve(innerTypeName), true);
						if (innerNode == null)
						{
							throw RpcException.BadAddress($"the node type '{innerTypeName}' can not be instantiated");
						}
						wrapper.ChangeInnerNode(innerNode);
					}
				}
				else if (!string.IsNullOrEmpty(classRef) || !string.IsNullOrEmpty(innerTypeName))
				{
					throw RpcException.BadAddress($"'{NodeTypes.ApiName(type)}' is not a wrapper node");
				}

				container.BeginUpdate();
				try
				{
					container.ReplaceChildNode(node, newNode);
				}
				catch (ArgumentException)
				{
					throw RpcException.BadAddress($"'{container.GetType().Name}' can not hold a '{NodeTypes.ApiName(type)}' node");
				}
				finally
				{
					container.EndUpdate();
				}

				container.UpdateOffsets();

				ProjectAccess.Refresh();

				return Describe(newNode, 1, true);
			});
		}

		private object Rename(Dictionary<string, object> p)
		{
			var name = Params.Get<string>(p, "name");

			return Mutate(p, node => node.Name = name);
		}

		private object Comment(Dictionary<string, object> p)
		{
			var comment = Params.Get<string>(p, "comment");

			return Mutate(p, node => node.Comment = comment);
		}

		private object SetHidden(Dictionary<string, object> p)
		{
			var hidden = Params.Get<bool>(p, "hidden");

			return Mutate(p, node => node.IsHidden = hidden);
		}

		private object Remove(Dictionary<string, object> p)
		{
			return UiThread.Invoke(() =>
			{
				var node = NodeSelector.ResolveNodeParam(ProjectAccess.Project, p);
				if (node is ClassNode)
				{
					throw RpcException.BadAddress("use 'class.delete' to remove a class");
				}

				var container = node.GetParentContainer();
				if (container == null || ReferenceEquals(container, node))
				{
					throw RpcException.NotFound("the node has no parent container");
				}

				container.BeginUpdate();
				var removed = container.RemoveNode(node);
				container.EndUpdate();

				if (!removed)
				{
					throw RpcException.NotFound("the node is not a child of its container");
				}

				container.UpdateOffsets();

				ProjectAccess.Refresh();

				return (object)ProjectAccess.Ok();
			});
		}

		private object SetArray(Dictionary<string, object> p)
		{
			var count = Params.Get<int>(p, "count");
			if (count <= 0)
			{
				throw RpcException.BadAddress("'count' must be positive");
			}

			return Mutate(p, node =>
			{
				switch (node)
				{
					case BaseWrapperArrayNode arrayNode:
						arrayNode.Count = count;
						if (arrayNode.CurrentIndex >= count)
						{
							arrayNode.CurrentIndex = count - 1;
						}
						break;
					case BaseTextNode textNode:
						textNode.Length = count;
						break;
					default:
						throw RpcException.BadAddress($"'{NodeTypes.ApiName(node.GetType())}' has no element count");
				}
			});
		}

		private object SetBits(Dictionary<string, object> p)
		{
			var bits = Params.Get<int>(p, "bits");

			return Mutate(p, node =>
			{
				if (!(node is BitFieldNode bitField))
				{
					throw RpcException.BadAddress($"'{NodeTypes.ApiName(node.GetType())}' is not a bit field");
				}
				bitField.Bits = bits;
			});
		}

		private object SetEnum(Dictionary<string, object> p)
		{
			var name = Params.Get<string>(p, "enum");

			return UiThread.Invoke(() =>
			{
				var project = ProjectAccess.Project;

				var description = project.Enums.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal));
				if (description == null)
				{
					throw RpcException.NotFound($"no enum named '{name}'");
				}

				var node = NodeSelector.ResolveNodeParam(project, p);
				if (!(node is EnumNode enumNode))
				{
					throw RpcException.BadAddress($"'{NodeTypes.ApiName(node.GetType())}' is not an enum node");
				}

				enumNode.ChangeEnum(description);

				node.GetParentContainer()?.UpdateOffsets();

				ProjectAccess.Refresh();

				return (object)ProjectAccess.Ok();
			});
		}

		private object Types(Dictionary<string, object> p)
		{
			return NodeTypes.Describe();
		}

		/// <summary>Applies a change to the selected node and repaints.</summary>
		private static object Mutate(Dictionary<string, object> p, Action<BaseNode> change)
		{
			return UiThread.Invoke(() =>
			{
				var node = NodeSelector.ResolveNodeParam(ProjectAccess.Project, p);

				var container = node.GetParentContainer();

				container?.BeginUpdate();
				try
				{
					change(node);
				}
				finally
				{
					container?.EndUpdate();
				}

				container?.UpdateOffsets();

				ProjectAccess.Refresh();

				return (object)ProjectAccess.Ok();
			});
		}
	}
}
