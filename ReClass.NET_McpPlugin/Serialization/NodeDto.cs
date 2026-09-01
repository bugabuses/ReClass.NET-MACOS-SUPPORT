using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using McpPlugin.Rpc;
using ReClassNET;
using ReClassNET.Extensions;
using ReClassNET.Memory;
using ReClassNET.Nodes;

namespace McpPlugin.Serialization
{
	/// <summary>
	/// The public node type registry. The RPC name of a node type is its class
	/// name without the <c>Node</c> suffix (<c>Hex32</c>, <c>Pointer</c>,
	/// <c>ClassInstance</c>, <c>Utf8Text</c>, …).
	/// </summary>
	public static class NodeTypes
	{
		private static readonly object sync = new object();
		private static Dictionary<string, Type> byName;
		private static List<Type> all;

		/// <summary>The RPC name of a node type.</summary>
		public static string ApiName(Type type)
		{
			var name = type.Name;
			return name.EndsWith("Node", StringComparison.Ordinal)
				? name.Substring(0, name.Length - "Node".Length)
				: name;
		}

		/// <summary>
		/// All instantiable node types of the <c>ReClassNET.Nodes</c> namespace.
		/// The legacy types below <c>ReClassNET.DataExchange.ReClass.Legacy</c>
		/// are deliberately excluded: they are stubs which throw for
		/// <see cref="BaseNode.MemorySize"/> and only exist for file loading.
		/// <see cref="ClassNode"/> is excluded too, it is created through
		/// <c>class.create</c> and never assigned to a child slot.
		/// </summary>
		public static IReadOnlyList<Type> All
		{
			get
			{
				EnsureLoaded();
				return all;
			}
		}

		private static void EnsureLoaded()
		{
			lock (sync)
			{
				if (byName != null)
				{
					return;
				}

				all = typeof(BaseNode).Assembly
					.GetTypes()
					.Where(t => !t.IsAbstract && t.IsPublic && typeof(BaseNode).IsAssignableFrom(t))
					.Where(t => t.Namespace == typeof(BaseNode).Namespace)
					.Where(t => t != typeof(ClassNode))
					.Where(t => t.GetConstructor(Type.EmptyTypes) != null)
					.OrderBy(t => t.Name, StringComparer.Ordinal)
					.ToList();

				byName = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
				foreach (var type in all)
				{
					byName[ApiName(type)] = type;
					byName[type.Name] = type;
				}
			}
		}

		/// <summary>Resolves an RPC type name, or throws <c>-32002</c>.</summary>
		public static Type Resolve(string apiName)
		{
			if (string.IsNullOrEmpty(apiName))
			{
				throw RpcException.BadArgument("missing node type");
			}

			EnsureLoaded();

			if (!byName.TryGetValue(apiName.Trim(), out var type))
			{
				throw RpcException.BadArgument($"unknown node type '{apiName}'");
			}
			return type;
		}

		/// <summary>The <c>node.types</c> listing.</summary>
		public static List<object> Describe()
		{
			return All
				.Select(t =>
				{
					int? size = null;
					try
					{
						// Uninitialized instances of wrapper nodes have no inner
						// node, so MemorySize may throw; report null then.
						var instance = BaseNode.CreateInstanceFromType(t, false);
						if (instance != null)
						{
							size = instance.MemorySize;
						}
					}
					catch (Exception)
					{
						size = null;
					}

					return (object)new Dictionary<string, object>
					{
						{ "name", ApiName(t) },
						{ "size", size },
						{ "is_container", typeof(BaseContainerNode).IsAssignableFrom(t) },
						{ "is_wrapper", typeof(BaseWrapperNode).IsAssignableFrom(t) }
					};
				})
				.ToList();
		}
	}

	/// <summary>Turns nodes into the JSON DTO described by the design document.</summary>
	public static class NodeDto
	{
		/// <summary>
		/// Creates a <see cref="MemoryBuffer"/> filled with the memory of the
		/// class, or null if no process is attached or the address is unusable.
		/// </summary>
		public static MemoryBuffer CreateMemory(ClassNode classNode)
		{
			var process = Program.RemoteProcess;
			if (process?.UnderlayingProcess == null || !process.IsValid)
			{
				return null;
			}

			IntPtr address;
			try
			{
				address = process.ParseAddress(classNode.AddressFormula);
			}
			catch (Exception)
			{
				return null;
			}

			var memory = new MemoryBuffer { Size = Math.Max(classNode.MemorySize, 1) };
			memory.UpdateFrom(process, address);

			return memory.ContainsValidData ? memory : null;
		}

		/// <summary>
		/// Creates the memory buffer a node's own values must be read from.
		///
		/// A node's <c>Offset</c> is relative to its immediate parent, and
		/// <see cref="ToDto"/> reads at <c>buffer.Offset + node.Offset</c>. For
		/// a node nested inside a <c>ClassInstance</c> the containing class'
		/// <c>AddressFormula</c> is *not* where that instance lives, so this
		/// walks the <c>ParentNode</c> chain up to the outermost node, summing
		/// the offsets, reads the outermost class at its resolved address and
		/// shifts the buffer to the node's frame.
		///
		/// Returns null (so every value is reported as null rather than read
		/// from the wrong place) when the chain crosses a
		/// <see cref="PointerNode"/> — that needs a dereference, not an offset —
		/// or when the outermost node is not a class.
		///
		/// Caveat: a class embedded in two different places has a single
		/// <c>ParentNode</c>, pointing at whichever wrapper set it last, so for
		/// a multiply-embedded class the walk follows that one parent.
		/// </summary>
		public static MemoryBuffer CreateMemoryFor(BaseNode node)
		{
			if (node == null)
			{
				return null;
			}

			var total = 0;

			var current = node;
			for (var guard = 0; current.ParentNode != null; ++guard)
			{
				if (guard > 256)
				{
					return null; // defensive: a cyclic parent chain
				}

				if (current.ParentNode is PointerNode)
				{
					return null; // the inner node lives behind a dereference
				}

				total += current.Offset;
				current = current.ParentNode;
			}

			if (!(current is ClassNode rootClass))
			{
				return null;
			}

			var memory = CreateMemory(rootClass);

			// ToDto adds node.Offset itself, so the buffer is based at the
			// node's frame origin, not at the node.
			return Shift(memory, total - node.Offset);
		}

		/// <summary>The index path of a node relative to its parent class.</summary>
		public static List<object> PathOf(BaseNode node)
		{
			var path = new List<object>();

			var current = node;
			while (current != null && !(current is ClassNode))
			{
				var parent = current.ParentNode;
				switch (parent)
				{
					case BaseContainerNode container:
						path.Insert(0, container.FindNodeIndex(current));
						break;
					case BaseWrapperNode _:
						path.Insert(0, 0);
						break;
					default:
						return path;
				}
				current = parent;
			}

			return path;
		}

		public static Dictionary<string, object> ToDto(BaseNode node, MemoryBuffer memory, int depth, bool withValues)
		{
			var dto = new Dictionary<string, object>
			{
				{ "type", NodeTypes.ApiName(node.GetType()) },
				{ "name", node.Name },
				{ "comment", node.Comment },
				{ "offset", node.Offset },
				{ "size", SafeMemorySize(node) },
				{ "hidden", node.IsHidden },
				{ "path", PathOf(node) },
				{ "value", withValues && memory != null ? ReadValue(node, memory) : null },
				{ "inner", null },
				{ "class_ref", null },
				{ "count", null },
				{ "children", null }
			};

			if (node is BaseWrapperArrayNode arrayNode)
			{
				dto["count"] = arrayNode.Count;
			}
			if (node is BitFieldNode bitField)
			{
				dto["count"] = bitField.Bits;
			}
			if (node is BaseTextNode textNode)
			{
				dto["count"] = textNode.Length;
			}

			if (node is BaseWrapperNode wrapper)
			{
				if (wrapper.InnerNode is ClassNode innerClass)
				{
					dto["class_ref"] = innerClass.Name;
				}

				if (depth > 0 && wrapper.InnerNode != null)
				{
					// A pointer dereferences; its inner node's values would need
					// a second buffer, so only structure is reported there.
					var innerMemory = node is PointerNode ? null : Shift(memory, node.Offset);

					dto["inner"] = ToDto(wrapper.InnerNode, innerMemory, depth - 1, withValues);
				}
			}

			if (node is EnumNode enumNode)
			{
				dto["class_ref"] = enumNode.Enum.Name;
			}

			if (node is BaseContainerNode containerNode && depth > 0)
			{
				var childMemory = node is ClassNode ? memory : Shift(memory, node.Offset);

				dto["children"] = containerNode.Nodes
					.Select(child => (object)ToDto(child, childMemory, depth - 1, withValues))
					.ToList();
			}

			return dto;
		}

		private static object SafeMemorySize(BaseNode node)
		{
			try
			{
				return node.MemorySize;
			}
			catch (Exception)
			{
				return null;
			}
		}

		private static MemoryBuffer Shift(MemoryBuffer memory, int offset)
		{
			if (memory == null)
			{
				return null;
			}

			var clone = memory.Clone();
			clone.Offset += offset;
			return clone;
		}

		/// <summary>Reads the displayed value of a node, or null if it has none.</summary>
		private static object ReadValue(BaseNode node, MemoryBuffer memory)
		{
			try
			{
				switch (node)
				{
					case BoolNode _:
						return memory.ReadUInt8(node.Offset) != 0;
					case Int8Node _:
						return (int)memory.ReadInt8(node.Offset);
					case UInt8Node _:
						return (int)memory.ReadUInt8(node.Offset);
					case Int16Node _:
						return (int)memory.ReadInt16(node.Offset);
					case UInt16Node _:
						return (int)memory.ReadUInt16(node.Offset);
					case Int32Node _:
						return memory.ReadInt32(node.Offset);
					case UInt32Node _:
						return (long)memory.ReadUInt32(node.Offset);
					case Int64Node _:
						return memory.ReadInt64(node.Offset).ToString(CultureInfo.InvariantCulture);
					case UInt64Node _:
						return memory.ReadUInt64(node.Offset).ToString(CultureInfo.InvariantCulture);
					case NIntNode _:
					case NUIntNode _:
						return Json.Address(memory.ReadIntPtr(node.Offset));
					case FloatNode _:
						return memory.ReadFloat(node.Offset);
					case DoubleNode _:
						return memory.ReadDouble(node.Offset);
					case Hex8Node _:
						return "0x" + memory.ReadUInt8(node.Offset).ToString("X2", CultureInfo.InvariantCulture);
					case Hex16Node _:
						return "0x" + memory.ReadUInt16(node.Offset).ToString("X4", CultureInfo.InvariantCulture);
					case Hex32Node _:
						return "0x" + memory.ReadUInt32(node.Offset).ToString("X8", CultureInfo.InvariantCulture);
					case Hex64Node _:
						return "0x" + memory.ReadUInt64(node.Offset).ToString("X16", CultureInfo.InvariantCulture);
					case BitFieldNode bitField:
						return ReadBits(bitField, memory);
					case EnumNode enumNode:
						return ReadEnum(enumNode, memory);
					case BaseTextNode textNode:
						return textNode.ReadValueFromMemory(memory);
					case BaseTextPtrNode textPtrNode:
						return ReadTextPointer(textPtrNode, memory);
					case BaseMatrixNode matrixNode:
						return ReadFloats(memory, node.Offset, node.MemorySize / matrixNode.ValueTypeSize);
					case PointerNode _:
					case FunctionPtrNode _:
					case VirtualMethodTableNode _:
					case VirtualMethodNode _:
						return Json.Address(memory.ReadIntPtr(node.Offset));
					default:
						return null;
				}
			}
			catch (Exception)
			{
				return null;
			}
		}

		private static object ReadBits(BitFieldNode node, MemoryBuffer memory)
		{
			switch (node.Bits)
			{
				case 8:
					return (long)memory.ReadUInt8(node.Offset);
				case 16:
					return (long)memory.ReadUInt16(node.Offset);
				case 32:
					return (long)memory.ReadUInt32(node.Offset);
				default:
					return memory.ReadUInt64(node.Offset).ToString(CultureInfo.InvariantCulture);
			}
		}

		private static object ReadEnum(EnumNode node, MemoryBuffer memory)
		{
			long value;
			switch (node.Enum.Size)
			{
				case ReClassNET.Project.EnumDescription.UnderlyingTypeSize.OneByte:
					value = memory.ReadInt8(node.Offset);
					break;
				case ReClassNET.Project.EnumDescription.UnderlyingTypeSize.TwoBytes:
					value = memory.ReadInt16(node.Offset);
					break;
				case ReClassNET.Project.EnumDescription.UnderlyingTypeSize.EightBytes:
					value = memory.ReadInt64(node.Offset);
					break;
				default:
					value = memory.ReadInt32(node.Offset);
					break;
			}

			var match = node.Enum.Values.FirstOrDefault(kv => kv.Value == value);

			return new Dictionary<string, object>
			{
				{ "value", value },
				{ "name", match.Key }
			};
		}

		private static object ReadTextPointer(BaseTextPtrNode node, MemoryBuffer memory)
		{
			var process = Program.RemoteProcess;
			if (process?.UnderlayingProcess == null)
			{
				return null;
			}

			return process.ReadRemoteString(memory.ReadIntPtr(node.Offset), node.Encoding, 256);
		}

		private static object ReadFloats(MemoryBuffer memory, int offset, int count)
		{
			var values = new List<object>(count);
			for (var i = 0; i < count; ++i)
			{
				values.Add(memory.ReadFloat(offset + i * sizeof(float)));
			}
			return values;
		}
	}
}
