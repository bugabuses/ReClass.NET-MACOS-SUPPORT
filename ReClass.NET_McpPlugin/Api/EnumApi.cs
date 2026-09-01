using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using McpPlugin.Rpc;
using ReClassNET.Project;

namespace McpPlugin.Api
{
	/// <summary>The enum descriptions of the current project.</summary>
	public class EnumApi
	{
		public void Register(RpcDispatcher dispatcher)
		{
			dispatcher.Register("enum.list", List);
			dispatcher.Register("enum.set", Set);
			dispatcher.Register("enum.delete", Delete);
		}

		private object List(Dictionary<string, object> p)
		{
			return UiThread.Invoke(() => (object)ProjectAccess.Project.Enums
				.Select(e => (object)Describe(e))
				.ToList());
		}

		private static Dictionary<string, object> Describe(EnumDescription description)
		{
			var values = new Dictionary<string, object>();
			foreach (var pair in description.Values)
			{
				values[pair.Key] = pair.Value.ToString(CultureInfo.InvariantCulture);
			}

			return new Dictionary<string, object>
			{
				{ "name", description.Name },
				{ "size", (int)description.Size },
				{ "flags", description.UseFlagsMode },
				{ "values", values }
			};
		}

		private object Set(Dictionary<string, object> p)
		{
			var name = Params.Get<string>(p, "name");
			var size = Params.GetOptional(p, "size", 4);
			var flags = Params.GetOptional(p, "flags", false);

			if (size != 1 && size != 2 && size != 4 && size != 8)
			{
				throw RpcException.BadArgument("'size' must be 1, 2, 4 or 8");
			}

			var raw = Params.AsObject(Params.GetRaw(p, "values"), "values");
			if (raw.Count == 0)
			{
				throw RpcException.BadArgument("'values' must not be empty");
			}

			var values = new List<KeyValuePair<string, long>>(raw.Count);
			foreach (var pair in raw)
			{
				long value;
				try
				{
					value = pair.Value is string text
						? long.Parse(text.Trim(), CultureInfo.InvariantCulture)
						: System.Convert.ToInt64(pair.Value, CultureInfo.InvariantCulture);
				}
				catch (Exception)
				{
					throw RpcException.BadArgument($"the value of '{pair.Key}' is not an integer");
				}

				values.Add(new KeyValuePair<string, long>(pair.Key, value));
			}

			return UiThread.Invoke(() =>
			{
				var project = ProjectAccess.Project;

				var description = project.Enums.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal));

				var created = description == null;
				if (created)
				{
					description = new EnumDescription { Name = name };
				}

				try
				{
					description.SetData(flags, (EnumDescription.UnderlyingTypeSize)size, values);
				}
				catch (ArgumentOutOfRangeException)
				{
					throw RpcException.BadArgument($"a value does not fit into {size} byte(s)");
				}

				if (created)
				{
					project.AddEnum(description);
				}

				ProjectAccess.Refresh();

				return (object)new Dictionary<string, object>
				{
					{ "ok", true },
					{ "created", created }
				};
			});
		}

		private object Delete(Dictionary<string, object> p)
		{
			var name = Params.Get<string>(p, "name");

			return UiThread.Invoke(() =>
			{
				var project = ProjectAccess.Project;

				var description = project.Enums.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal));
				if (description == null)
				{
					throw RpcException.NotFound($"no enum named '{name}'");
				}

				try
				{
					project.RemoveEnum(description);
				}
				catch (EnumReferencedException ex)
				{
					throw RpcException.Referenced(ex.Message, ex.References.Select(c => (object)c.Name));
				}

				ProjectAccess.Refresh();

				return (object)Json.Ok();
			});
		}
	}
}
