#if OSX
using Keysharp.Builtins;
using MonoMac.AppKit;
using MonoMac.Foundation;

namespace Keysharp.Internals.Window.MacOS
{
	/// <summary>
	/// macOS <see cref="IWindowEventBackend"/>. The active-application stream is sourced from
	/// <c>NSWorkspace</c>'s activation notification (the same mechanism the keyboard hook already uses for
	/// hotstring-buffer resets — see <c>MacNativeWindows.RegisterFrontmostAppObserver</c>), so on macOS the
	/// <see cref="WindowEventType.Active"/> handle is the frontmost <em>application</em>'s pid. Per-window
	/// granularity, and the Create/Close/Move/Show/Minimize/Restore/TitleChange categories, require
	/// <c>AXObserver</c> notifications (Accessibility-permission gated) and are left as TODO; because the
	/// backend is decoupled from <c>Ks.WinEvent</c> via <see cref="WindowEventHub"/>, they can be added here
	/// without touching the WinEvent feature.
	/// </summary>
	internal sealed class WindowEventBackend : IWindowEventBackend
	{
		private NSObject activeAppObserver;     // retained so the notification dispatcher stays alive
		private bool disposed;

		public Action<WindowEventRaw> Sink { get; set; }

		// NSWorkspace observer registration must happen on the main thread, but it is posted asynchronously
		// (not InvokeOnUIThread) because Start/Stop run under the hub's lock; a synchronous main-thread round
		// trip there could deadlock against the observer callback, which also runs on the main thread and
		// re-enters the hub. The notification dispatcher delivers on whichever run loop registered the observer.
		public void Start(WindowEventMask mask)
		{
			if (disposed || (mask & WindowEventMask.Active) == 0)
				return;

			Script.PostToUIThread(StartActiveObserver);
		}

		public void Stop(WindowEventMask mask)
		{
			if ((mask & WindowEventMask.Active) == 0)
				return;

			Script.PostToUIThread(StopActiveObserver);
		}

		public void Dispose()
		{
			disposed = true;
			Script.PostToUIThread(StopActiveObserver);
		}

		private void StartActiveObserver()
		{
			if (activeAppObserver != null)
				return;

			try
			{
				var wsnc = NSWorkspace.SharedWorkspace.NotificationCenter;
				activeAppObserver = wsnc.AddObserver("NSWorkspaceDidActivateApplicationNotification", _ => OnActiveAppChanged());
			}
			catch (Exception ex)
			{
				Ks.OutputDebugLine($"macOS active-app observer registration failed: {ex.Message}");
			}
		}

		private void StopActiveObserver()
		{
			if (activeAppObserver == null)
				return;

			try
			{
				NSWorkspace.SharedWorkspace.NotificationCenter.RemoveObserver(activeAppObserver);
			}
			catch
			{
			}
			finally
			{
				activeAppObserver = null;
			}
		}

		private void OnActiveAppChanged()
		{
			var sink = Sink;

			if (sink == null)
				return;

			try
			{
				var app = NSWorkspace.SharedWorkspace.FrontmostApplication;
				var pid = app != null ? app.ProcessIdentifier : 0;

				if (pid != 0)
					sink(new WindowEventRaw(WindowEventType.Active, (nint)pid, Environment.TickCount64));
			}
			catch (Exception ex)
			{
				Ks.OutputDebugLine($"macOS active-app notification failed: {ex.Message}");
			}
		}
	}
}
#endif
