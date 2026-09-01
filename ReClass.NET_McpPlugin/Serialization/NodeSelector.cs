using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using McpPlugin.Rpc;
using ReClassNET.Nodes;
using ReClassNET.Project;

namespace McpPlugin.Serialization
{
	/// <summary>
	/// Resolves the RPC class and node selectors.
	///
	/// A class is addressed by name (exact, first match) or by UUID string.
	/// A node is addressed by <c>{class, path:[i, j, …]}</c> — indices into
	/// <see cref="BaseContainerNode.Nodes"/>, wrapper nodes are descended into
	/// with index 0 — or by <c>{class, offset:n}</c> for a direct child of the
	/// class at that offset. With neither, the class node itself is returned.
	/// </summary>
	public static class NodeSelector
	{
		public static ClassNode ResolveClass(ReClassNetProject project, object value)
		{
			if (value == null)
			{
				throw RpcException.BadArgument("missing parameter 'class'");
			}

			var identifier = System.Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
			if (identifier.Length == 0)
			{
				throw RpcException.BadArgument("parameter 'class' must not be empty");
			}

			// A well-formed uuid is only ever a uuid: falling back to the name
			// lookup would report "no class named <uuid>", which reads as if a
			// class could be called that.
			if (Guid.TryParse(identifier, out var uuid))
			{
				var byUuid = project.Classes.FirstOrDefault(c => c.Uuid.Equals(uuid));
				if (byUuid == null)
				{
					throw RpcException.NotFound($"no class with uuid '{identifier}'");
				}
				return byUuid;
			}

			var byName = project.Classes.FirstOrDefault(c => string.Equals(c.Name, identifier, StringComparison.Ordinal));
			if (byName == null)
			{
				throw RpcException.NotFound($"no class named '{identifier}'");
			}
			return byName;
		}

		/// <summary>Reads the class from a <c>class</c> parameter of the request.</summary>
		public static ClassNode ResolveClass(ReClassNetProject project, Dictionary<string, object> p)
		{
			return ResolveClass(project, Params.GetRaw(p, "class"));
		}

		/// <summary>Reads the node from a <c>node</c> parameter of the request.</summary>
		public static BaseNode ResolveNodeParam(ReClassNetProject project, Dictionary<string, object> p, string name = "node")
		{
			return Resolve(project, Params.AsObject(Params.GetRaw(p, name), name));
		}

		public static BaseNode Resolve(ReClassNetProject project, Dictionary<string, object> selector)
		{
			var classNode = ResolveClass(project, selector);

			if (Params.Has(selector, "path"))
			{
				return Descend(classNode, Params.GetList(selector, "path"));
			}

			if (Params.Has(selector, "offset"))
			{
				var offset = Params.Get<int>(selector, "offset");
				var child = classNode.Nodes.FirstOrDefault(n => n.Offset == offset);
				if (child == null)
				{
					throw RpcException.NotFound($"class '{classNode.Name}' has no node at offset {offset}");
				}
				return child;
			}

			return classNode;
		}

		private static BaseNode Descend(ClassNode classNode, IReadOnlyList<object> path)
		{
			BaseNode current = classNode;

			for (var level = 0; level < path.Count; ++level)
			{
				int index;
				try
				{
					index = System.Convert.ToInt32(path[level], CultureInfo.InvariantCulture);
				}
				catch (Exception)
				{
					throw RpcException.BadArgument($"'path[{level}]' is not an index");
				}

				switch (current)
				{
					case BaseContainerNode container:
						if (index < 0 || index >= container.Nodes.Count)
						{
							throw RpcException.NotFound($"'path[{level}]' index {index} is out of range (0..{container.Nodes.Count - 1})");
						}
						current = container.Nodes[index];
						break;
					case BaseWrapperNode wrapper:
						if (index != 0)
						{
							throw RpcException.NotFound($"'path[{level}]' must be 0 for the wrapper node '{current.Name}'");
						}
						if (wrapper.InnerNode == null)
						{
							throw RpcException.NotFound($"the wrapper node '{current.Name}' has no inner node");
						}
						current = wrapper.InnerNode;
						break;
					default:
						throw RpcException.NotFound($"the node '{current.Name}' has no children");
				}
			}

			return current;
		}
	}
}
