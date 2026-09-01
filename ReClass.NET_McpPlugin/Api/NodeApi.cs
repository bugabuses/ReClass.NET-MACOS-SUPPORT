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

		/// <summary>
		/// Builds the dto of a node.
		///
		/// Values need a memory buffer, and a buffer is only meaningful if we
		/// know the node's absolute address. <see cref="NodeDto.CreateMemory"/>
		/// resolves a class' own <c>AddressFormula</c>, which is only correct
		/// for a top-level class: a node reached *through* a
		/// <c>ClassInstance</c> lives at the outer class' address plus the
		/// offsets along the way, not at the inner class' formula.
		///
		/// So the buffer is built by walking up from the node to the outermost
		/// <see cref="ClassNode"/> (the one that is not itself embedded in
		/// another class through a container), summing the container offsets on
		/// the way, and reading at that class' resolved address. When the walk
		/// crosses something whose absolute address cannot be computed — a
		/// <c>Pointer</c>, which needs a dereference — no buffer is produced
		/// and every <c>value</c> comes back null rather than wrong.
		/// </summary>
		private static object Describe(BaseNode node, int depth, bool withValues)
		{
			var memory = withValues ? NodeDto.CreateMemoryFor(node) : null;

			depth = NodeDto.ClampDepth(depth);

			var dto = NodeDto.ToDto(node, memory, depth, withValues);
			dto["depth"] = depth;

			return dto;
		}

		private object Get(Dictionary<string, object> p)
		{
			// Silently clamped to NodeDto.MaxDepth; the effective value comes
			// back as the DTO's "depth".
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
					throw RpcException.BadArgument("a class node can not change its type");
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
					throw RpcException.BadArgument($"the node type '{type.Name}' can not be instantiated");
				}

				if (newNode is BaseWrapperNode wrapper)
				{
					if (!string.IsNullOrEmpty(classRef))
					{
						var innerClass = NodeSelector.ResolveClass(project, classRef);

						// Without this the node graph can be made cyclic (a
						// class reaching back to itself), and the very next
						// repaint or MemorySize walk recurses until the process
						// dies of a StackOverflowException, which .NET can not
						// catch. Same guard as MainForm.cs / MainForm.Functions.cs.
						RequireCycleFree(project, node, wrapper, innerClass);

						ChangeInner(wrapper, innerClass);
					}
					else if (!string.IsNullOrEmpty(innerTypeName))
					{
						var innerNode = BaseNode.CreateInstanceFromType(NodeTypes.Resolve(innerTypeName), true);
						if (innerNode == null)
						{
							throw RpcException.BadArgument($"the node type '{innerTypeName}' can not be instantiated");
						}
						ChangeInner(wrapper, innerNode);
					}
				}
				else if (!string.IsNullOrEmpty(classRef) || !string.IsNullOrEmpty(innerTypeName))
				{
					throw RpcException.BadArgument($"'{NodeTypes.ApiName(type)}' is not a wrapper node");
				}

				container.BeginUpdate();
				try
				{
					if (container.FindNodeIndex(node) < 0)
					{
						throw RpcException.NotFound($"the node '{node.Name}' is not a child of '{container.Name}'");
					}

					container.ReplaceChildNode(node, newNode);
				}
				catch (ArgumentException)
				{
					throw RpcException.BadArgument($"'{container.GetType().Name}' can not hold a '{NodeTypes.ApiName(type)}' node");
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
			return Mutate(p, node =>
			{
				if (node is ClassNode)
				{
					throw RpcException.BadArgument("use 'class.delete' to remove a class");
				}

				var container = node.GetParentContainer();
				if (container == null || ReferenceEquals(container, node))
				{
					throw RpcException.NotFound("the node has no parent container");
				}

				if (!container.RemoveNode(node))
				{
					throw RpcException.NotFound($"the node '{node.Name}' is not a child of '{container.Name}'");
				}
			});
		}

		private object SetArray(Dictionary<string, object> p)
		{
			var count = Params.Get<int>(p, "count");
			if (count <= 0)
			{
				throw RpcException.BadArgument("'count' must be positive");
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
						throw RpcException.BadArgument($"'{NodeTypes.ApiName(node.GetType())}' has no element count");
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
					throw RpcException.BadArgument($"'{NodeTypes.ApiName(node.GetType())}' is not a bit field");
				}
				bitField.Bits = bits;
			});
		}

		private object SetEnum(Dictionary<string, object> p)
		{
			var name = Params.Get<string>(p, "enum");

			return Mutate(p, node =>
			{
				var description = ProjectAccess.Project.Enums
					.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal));
				if (description == null)
				{
					throw RpcException.NotFound($"no enum named '{name}'");
				}

				if (!(node is EnumNode enumNode))
				{
					throw RpcException.BadArgument($"'{NodeTypes.ApiName(node.GetType())}' is not an enum node");
				}

				enumNode.ChangeEnum(description);
			});
		}

		/// <summary>
		/// Sets a wrapper's inner node, mapping the host's refusal (an
		/// incompatible inner type, e.g. a <c>ClassInstance</c> asked to wrap a
		/// <c>UInt32</c>) to -32002 instead of a -32004 internal error.
		/// </summary>
		private static void ChangeInner(BaseWrapperNode wrapper, BaseNode innerNode)
		{
			try
			{
				wrapper.ChangeInnerNode(innerNode);
			}
			catch (InvalidOperationException ex)
			{
				throw RpcException.BadArgument(ex.Message);
			}
		}

		/// <summary>
		/// Refuses a <c>class_ref</c> which would make the class graph cyclic.
		/// Mirrors <c>MainForm.IsCycleFree</c>.
		/// </summary>
		private static void RequireCycleFree(ReClassNetProject project, BaseNode target, BaseWrapperNode wrapper, BaseNode innerNode)
		{
			if (!(innerNode is ClassNode innerClass))
			{
				return;
			}

			// The node being replaced is not in the tree yet, so the parent
			// class is taken from the node it replaces.
			var parentClass = target.GetParentClass();
			if (parentClass == null)
			{
				return;
			}

			// The wrapper is freshly created and not yet parented, so it is its
			// own root wrapper; ask it whether the check applies at all.
			var rootWrapper = wrapper.GetRootWrapperNode() ?? wrapper;
			if (!rootWrapper.ShouldPerformCycleCheckForInnerNode())
			{
				return;
			}

			if (ClassUtil.IsCyclicIfClassIsAccessibleFromParent(parentClass, innerClass, project.Classes))
			{
				throw RpcException.BadArgument(
					$"'{innerClass.Name}' can not be referenced from '{parentClass.Name}': it would create a class cycle");
			}
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

				return (object)Json.Ok();
			});
		}
	}
}
