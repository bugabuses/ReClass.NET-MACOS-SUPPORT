using System;
using System.Collections.Generic;
using System.Linq;
using McpPlugin.Rpc;
using McpPlugin.Serialization;
using ReClassNET;
using ReClassNET.CodeGenerator;
using ReClassNET.Nodes;
using ReClassNET.Project;

namespace McpPlugin.Api
{
	/// <summary>Generates C++ or C# code for the classes of the project.</summary>
	public class CodeGenApi
	{
		public void Register(RpcDispatcher dispatcher)
		{
			dispatcher.Register("codegen.generate", Generate);
		}

		private object Generate(Dictionary<string, object> p)
		{
			var language = Params.GetOptional(p, "language", "cpp").Trim().ToLowerInvariant();

			var names = Params.Has(p, "classes")
				? Params.GetList(p, "classes")
				: null;

			return UiThread.Invoke(() =>
			{
				var project = ProjectAccess.Project;

				ICodeGenerator generator;
				switch (language)
				{
					case "cpp":
					case "c++":
						generator = new CppCodeGenerator(project.TypeMapping);
						break;
					case "csharp":
					case "c#":
						generator = new CSharpCodeGenerator();
						break;
					default:
						throw RpcException.BadAddress($"unknown language '{language}', expected 'cpp' or 'csharp'");
				}

				IReadOnlyList<ClassNode> classes;
				if (names == null)
				{
					classes = project.Classes;
				}
				else
				{
					classes = names
						.Select(n => NodeSelector.ResolveClass(project, n))
						.Distinct()
						.ToList();
				}

				var enums = project.Enums;

				return (object)new Dictionary<string, object>
				{
					{ "language", language },
					{ "code", generator.GenerateCode(classes, enums, Program.Logger) }
				};
			});
		}
	}
}
