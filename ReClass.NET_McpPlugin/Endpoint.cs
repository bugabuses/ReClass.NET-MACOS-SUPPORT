using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using McpPlugin.Rpc;

namespace McpPlugin
{
	/// <summary>
	/// The endpoint file tells the Python bridge where to connect and which
	/// token to present: <c>{"port":N,"token":"hex32","pid":P}</c> at
	/// <c>~/.reclass-mcp.json</c>.
	///
	/// On macOS ReClass.NET is launched through <c>run-macos.sh</c>, which
	/// re-executes mono under <c>sudo</c> because <c>task_for_pid</c> needs
	/// root. That means the process' own home directory is root's, while the
	/// MCP bridge runs as the ordinary desktop user and looks in that user's
	/// home. So when <c>SUDO_USER</c> is set we write the file into the
	/// invoking user's home directory and <c>chown</c> it to them; the file
	/// stays mode 0600 so only that user (and root) can read the token.
	/// </summary>
	public static class Endpoint
	{
		public const string FileName = ".reclass-mcp.json";

		public static string GenerateToken()
		{
			var bytes = new byte[16];
			using (var rng = RandomNumberGenerator.Create())
			{
				rng.GetBytes(bytes);
			}

			var builder = new StringBuilder(bytes.Length * 2);
			foreach (var b in bytes)
			{
				builder.Append(b.ToString("x2"));
			}
			return builder.ToString();
		}

		private static bool IsUnix
		{
			get
			{
				var platform = Environment.OSVersion.Platform;
				return platform == PlatformID.Unix || platform == PlatformID.MacOSX;
			}
		}

		/// <summary>Resolves the home directory the bridge will look in.</summary>
		public static string HomeDirectory
		{
			get
			{
				if (IsUnix)
				{
					var sudoUser = Environment.GetEnvironmentVariable("SUDO_USER");
					if (!string.IsNullOrEmpty(sudoUser))
					{
						var home = ResolveUnixHome(sudoUser);
						if (home != null)
						{
							return home;
						}
					}

					var envHome = Environment.GetEnvironmentVariable("HOME");
					if (!string.IsNullOrEmpty(envHome))
					{
						return envHome;
					}
				}

				var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
				if (!string.IsNullOrEmpty(profile))
				{
					return profile;
				}

				return Path.GetTempPath();
			}
		}

		private static string ResolveUnixHome(string user)
		{
			// getent(1) does not exist on macOS; ask the shell's tilde
			// expansion via `sh -c 'echo ~user'`, falling back to /Users/<user>
			// and /home/<user>.
			try
			{
				var startInfo = new ProcessStartInfo("/bin/sh", "-c \"echo ~" + user + "\"")
				{
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					UseShellExecute = false,
					CreateNoWindow = true
				};
				using (var process = System.Diagnostics.Process.Start(startInfo))
				{
					var output = process.StandardOutput.ReadToEnd().Trim();
					process.WaitForExit(5000);

					if (output.Length > 0 && output[0] == '/' && Directory.Exists(output))
					{
						return output;
					}
				}
			}
			catch (Exception)
			{
				// fall through to the guesses below
			}

			foreach (var candidate in new[] { "/Users/" + user, "/home/" + user })
			{
				if (Directory.Exists(candidate))
				{
					return candidate;
				}
			}

			return null;
		}

		public static string FilePath => Path.Combine(HomeDirectory, FileName);

		/// <summary>Writes the endpoint file atomically (temp file + rename) and returns its path.</summary>
		public static string Write(int port, string token)
		{
			var path = FilePath;
			var temp = path + "." + System.Diagnostics.Process.GetCurrentProcess().Id + ".tmp";

			var content = Json.Serialize(new Dictionary<string, object>
			{
				{ "port", port },
				{ "token", token },
				{ "pid", System.Diagnostics.Process.GetCurrentProcess().Id }
			});

			File.WriteAllText(temp, content, new UTF8Encoding(false));

			Chmod600(temp);
			ChownToInvokingUser(temp);

			// File.Move does not overwrite on .NET Framework / Mono.
			if (File.Exists(path))
			{
				File.Delete(path);
			}
			File.Move(temp, path);

			return path;
		}

		public static void Delete()
		{
			try
			{
				var path = FilePath;
				if (File.Exists(path))
				{
					File.Delete(path);
				}
			}
			catch (Exception)
			{
				// ignored
			}
		}

		private static void Chmod600(string path)
		{
			if (!IsUnix)
			{
				return;
			}
			RunQuietly("/bin/chmod", "600 \"" + path + "\"");
		}

		private static void ChownToInvokingUser(string path)
		{
			if (!IsUnix)
			{
				return;
			}

			var uid = Environment.GetEnvironmentVariable("SUDO_UID");
			var gid = Environment.GetEnvironmentVariable("SUDO_GID");
			if (string.IsNullOrEmpty(uid))
			{
				return;
			}

			var owner = string.IsNullOrEmpty(gid) ? uid : uid + ":" + gid;

			foreach (var chown in new[] { "/usr/sbin/chown", "/bin/chown", "/usr/bin/chown" })
			{
				if (File.Exists(chown) && RunQuietly(chown, owner + " \"" + path + "\""))
				{
					return;
				}
			}
		}

		private static bool RunQuietly(string fileName, string arguments)
		{
			try
			{
				var startInfo = new ProcessStartInfo(fileName, arguments)
				{
					UseShellExecute = false,
					CreateNoWindow = true,
					RedirectStandardError = true,
					RedirectStandardOutput = true
				};
				using (var process = System.Diagnostics.Process.Start(startInfo))
				{
					process.StandardOutput.ReadToEnd();
					process.StandardError.ReadToEnd();
					process.WaitForExit(5000);
					return process.ExitCode == 0;
				}
			}
			catch (Exception)
			{
				return false;
			}
		}
	}
}
