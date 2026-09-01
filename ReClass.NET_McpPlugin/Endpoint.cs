using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
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
	///
	/// The <c>SUDO_USER</c> lookup goes through libc <c>getpwnam(3)</c> — never
	/// through a shell — and the name is validated against a strict character
	/// class first, so a hostile <c>SUDO_USER</c> can not turn into command
	/// execution in a process running as root.
	/// </summary>
	public static class Endpoint
	{
		public const string FileName = ".reclass-mcp.json";

		/// <summary>The uid/gid the endpoint file is handed to, or null.</summary>
		private static uint? ownerUid;
		private static uint? ownerGid;

		private static string cachedHome;
		private static readonly object homeLock = new object();

		private static readonly Regex UserNamePattern = new Regex(@"^[A-Za-z0-9._-]{1,64}$", RegexOptions.CultureInvariant);

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

		private static bool IsMacOs => Directory.Exists("/System/Library/CoreServices");

		/// <summary>
		/// Resolves the home directory the bridge will look in. Computed once
		/// and cached — the answer can not change while the process runs, and
		/// the file must be deleted from the same place it was written.
		/// </summary>
		public static string HomeDirectory
		{
			get
			{
				lock (homeLock)
				{
					return cachedHome ?? (cachedHome = ResolveHomeDirectory());
				}
			}
		}

		private static string ResolveHomeDirectory()
		{
			if (IsUnix)
			{
				var sudoUser = Environment.GetEnvironmentVariable("SUDO_USER");
				if (!string.IsNullOrEmpty(sudoUser))
				{
					if (!UserNamePattern.IsMatch(sudoUser))
					{
						// Not a plausible user name: ignore it entirely rather
						// than feeding it to any lookup.
						sudoUser = null;
					}
				}

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

		/// <summary>
		/// Looks the user up in the password database via libc and returns the
		/// <c>pw_dir</c> if it exists, recording <c>pw_uid</c>/<c>pw_gid</c> for
		/// the later chown. Returns null when the user is unknown.
		/// </summary>
		private static string ResolveUnixHome(string user)
		{
			try
			{
				var entry = GetPasswordEntry(user);
				if (entry == null)
				{
					return null;
				}

				ownerUid = entry.Value.Uid;
				ownerGid = entry.Value.Gid;

				var home = entry.Value.Directory;
				if (!string.IsNullOrEmpty(home) && home[0] == '/' && Directory.Exists(home))
				{
					return home;
				}
			}
			catch (Exception)
			{
				// fall through: no home from the password database
			}

			return null;
		}

		public static string FilePath => Path.Combine(HomeDirectory, FileName);

		/// <summary>
		/// Writes the endpoint file atomically (temp file + rename) and returns
		/// its path. The temp file is created under <c>umask(0077)</c> so it is
		/// never momentarily world readable, then chmod'ed and chown'ed. If the
		/// file can not be made 0600 it is deleted and null is returned — the
		/// RPC server still runs, the endpoint is just not published.
		/// </summary>
		public static string Write(int port, string token)
		{
			var path = FilePath;
			var pid = System.Diagnostics.Process.GetCurrentProcess().Id;
			var temp = path + "." + pid + ".tmp";

			var content = Json.Serialize(new Dictionary<string, object>
			{
				{ "port", port },
				{ "token", token },
				{ "pid", pid }
			});

			WriteRestricted(temp, content);

			if (!Chmod600(temp))
			{
				// Never leave a token readable by anyone else: drop the file.
				// The server keeps running, the endpoint is just unpublished.
				TryDelete(temp);
				Log($"mcp: could not restrict the permissions of '{temp}'; the endpoint file was NOT written");
				return null;
			}

			ChownToInvokingUser(temp);

			// File.Move does not overwrite on .NET Framework / Mono.
			if (File.Exists(path))
			{
				File.Delete(path);
			}
			File.Move(temp, path);

			return path;
		}

		/// <summary>
		/// Creates the file with the process umask temporarily set to 0077, so
		/// it is 0600 from the moment it exists. The umask is process global;
		/// this runs once during plugin initialisation, before any client can
		/// make the plugin create other files.
		/// </summary>
		private static void WriteRestricted(string path, string content)
		{
			var bytes = new UTF8Encoding(false).GetBytes(content);

			if (!IsUnix)
			{
				File.WriteAllBytes(path, bytes);
				return;
			}

			TryDelete(path);

			var previousMask = SysUmask(0x3F /* 0077 */);
			try
			{
				using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
				{
					stream.Write(bytes, 0, bytes.Length);
				}
			}
			finally
			{
				SysUmask(previousMask);
			}
		}

		private static void TryDelete(string path)
		{
			try
			{
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

		public static void Delete()
		{
			TryDelete(FilePath);
		}

		// mode_t/uid_t/gid_t are 16/32 bit unsigned on macOS and Linux; the
		// extra width is ignored by the callee. Calling libc directly is more
		// reliable than spawning /bin/chmod from inside the GUI process.
		[DllImport("libc", EntryPoint = "chmod", SetLastError = true)]
		private static extern int SysChmod(string path, uint mode);

		[DllImport("libc", EntryPoint = "chown", SetLastError = true)]
		private static extern int SysChown(string path, uint owner, uint group);

		[DllImport("libc", EntryPoint = "umask", SetLastError = true)]
		private static extern uint SysUmask(uint mask);

		[DllImport("libc", EntryPoint = "getpwnam", SetLastError = true)]
		private static extern IntPtr SysGetPwNam([MarshalAs(UnmanagedType.LPStr)] string name);

		/// <summary>macOS <c>struct passwd</c> (see &lt;pwd.h&gt;).</summary>
		[StructLayout(LayoutKind.Sequential)]
		private struct PasswdDarwin
		{
			public IntPtr pw_name;
			public IntPtr pw_passwd;
			public uint pw_uid;
			public uint pw_gid;
			public long pw_change;
			public IntPtr pw_class;
			public IntPtr pw_gecos;
			public IntPtr pw_dir;
			public IntPtr pw_shell;
			public long pw_expire;
		}

		/// <summary>glibc <c>struct passwd</c> (see &lt;pwd.h&gt;).</summary>
		[StructLayout(LayoutKind.Sequential)]
		private struct PasswdLinux
		{
			public IntPtr pw_name;
			public IntPtr pw_passwd;
			public uint pw_uid;
			public uint pw_gid;
			public IntPtr pw_gecos;
			public IntPtr pw_dir;
			public IntPtr pw_shell;
		}

		private struct PasswordEntry
		{
			public uint Uid;
			public uint Gid;
			public string Directory;
		}

		private static PasswordEntry? GetPasswordEntry(string user)
		{
			// The caller has already validated `user` against
			// ^[A-Za-z0-9._-]{1,64}$; getpwnam takes it as data, not as a
			// command line.
			var pointer = SysGetPwNam(user);
			if (pointer == IntPtr.Zero)
			{
				return null;
			}

			if (IsMacOs)
			{
				var passwd = (PasswdDarwin)Marshal.PtrToStructure(pointer, typeof(PasswdDarwin));
				return new PasswordEntry
				{
					Uid = passwd.pw_uid,
					Gid = passwd.pw_gid,
					Directory = Marshal.PtrToStringAnsi(passwd.pw_dir)
				};
			}

			var linux = (PasswdLinux)Marshal.PtrToStructure(pointer, typeof(PasswdLinux));
			return new PasswordEntry
			{
				Uid = linux.pw_uid,
				Gid = linux.pw_gid,
				Directory = Marshal.PtrToStringAnsi(linux.pw_dir)
			};
		}

		/// <summary>Returns true when the file is (or does not need to be) 0600.</summary>
		private static bool Chmod600(string path)
		{
			if (!IsUnix)
			{
				return true;
			}

			try
			{
				if (SysChmod(path, 0x180 /* 0600 */) != 0)
				{
					Log($"mcp: chmod 0600 of '{path}' failed (errno {Marshal.GetLastWin32Error()})");
					return false;
				}
				return true;
			}
			catch (Exception ex)
			{
				Log($"mcp: chmod 0600 of '{path}' failed: {ex.Message}");
				return false;
			}
		}

		/// <summary>Hands the file to the user who invoked sudo, so the bridge can read it.</summary>
		private static void ChownToInvokingUser(string path)
		{
			if (!IsUnix)
			{
				return;
			}

			// Prefer the uid/gid getpwnam(3) returned for SUDO_USER; fall back
			// to the SUDO_UID/SUDO_GID the environment carries.
			var uid = ownerUid;
			var gid = ownerGid;

			if (uid == null)
			{
				if (!uint.TryParse(Environment.GetEnvironmentVariable("SUDO_UID"), out var envUid))
				{
					return;
				}
				uid = envUid;

				gid = uint.TryParse(Environment.GetEnvironmentVariable("SUDO_GID"), out var envGid)
					? envGid
					: uint.MaxValue; // -1: leave the group unchanged
			}

			try
			{
				if (SysChown(path, uid.Value, gid ?? uint.MaxValue) != 0)
				{
					Log($"mcp: chown of '{path}' to {uid}:{gid} failed (errno {Marshal.GetLastWin32Error()})");
				}
			}
			catch (Exception ex)
			{
				Log($"mcp: chown of '{path}' failed: {ex.Message}");
			}
		}

		/// <summary>Set by the plugin so permission failures reach the host log.</summary>
		public static Action<string> Logger { get; set; }

		private static void Log(string message)
		{
			try
			{
				Logger?.Invoke(message);
			}
			catch (Exception)
			{
				// ignored
			}
		}
	}
}
