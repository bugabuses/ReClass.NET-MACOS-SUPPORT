using System;
using System.Reflection;
using System.Windows.Forms;

namespace McpPlugin.Rpc
{
	/// <summary>Marshals work onto the ReClass.NET UI thread.</summary>
	public static class UiThread
	{
		private static Control control;

		public static void Initialize(Control mainWindow)
		{
			control = mainWindow;
		}

		public static T Invoke<T>(Func<T> func)
		{
			var target = control;
			if (target == null || target.IsDisposed || !target.IsHandleCreated || !target.InvokeRequired)
			{
				return func();
			}

			try
			{
				return (T)target.Invoke(func);
			}
			catch (TargetInvocationException ex) when (ex.InnerException != null)
			{
				throw ex.InnerException;
			}
		}

		public static void Invoke(Action action)
		{
			Invoke(() =>
			{
				action();
				return true;
			});
		}
	}
}
