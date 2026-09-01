using System;
using System.Runtime.InteropServices;

class DlopenSmoke
{
	[DllImport("__Internal")] static extern IntPtr dlopen(string f, int flags);
	[DllImport("__Internal")] static extern IntPtr dlsym(IntPtr h, string s);

	static void Main(string[] args)
	{
		var h = dlopen(args[0], 2);
		Console.WriteLine("dlopen: " + h);
		Console.WriteLine("EnumerateProcesses: " + dlsym(h, "EnumerateProcesses"));
	}
}
