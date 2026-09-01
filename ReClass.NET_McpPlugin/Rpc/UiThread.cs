using System;
using System.Reflection;
using System.Runtime.ExceptionServices;
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

		/// <summary>Drops the reference to the main window (plugin shutdown).</summary>
		public static void Terminate()
		{
			control = null;
		}

		/// <summary>
		/// Runs <paramref name="func"/> on the UI thread and returns its result;
		/// exceptions are rethrown with their original stack trace.
		///
		/// Deadlock caveat: this is a *blocking* <see cref="Control.Invoke"/>.
		/// If the UI thread is not pumping messages — it is inside a modal loop
		/// that does not dispatch, blocked on a long operation, or waiting on
		/// this very RPC call — the calling client thread blocks for as long as
		/// that lasts, with no timeout. Every handler that touches the project
		/// or node tree goes through here, so a wedged UI thread wedges the RPC
		/// server's client threads too (the accept loop keeps running).
		/// </summary>
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
				// Rethrow the original exception with its stack trace intact.
				ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
				throw; // unreachable, keeps the compiler happy
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
