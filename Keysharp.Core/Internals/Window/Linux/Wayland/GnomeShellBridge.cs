#if LINUX
using Tmds.DBus;
namespace Keysharp.Internals.Window.Linux.Wayland
{
	// D-Bus member names are case-sensitive; suppress the IDE naming rule.
#pragma warning disable IDE1006

	/// <summary>
	/// Tmds.DBus proxy interface for the Keysharp GNOME Shell extension.
	/// The interface must be public because Tmds.DBus generates the proxy
	/// in a separate dynamic assembly that cannot implement internal interfaces.
	/// </summary>
	[DBusInterface("io.github.keysharp.GnomeShell1")]
	public interface IGnomeShell : IDBusObject
	{
		/// <summary>
		/// Returns JSON: { ok, windows: [ { id, title, appId, pid,
		/// frame:{x,y,width,height}, client:{x,y,width,height},
		/// active, minimized, maximized, visible } ... ] }
		/// Windows are ordered bottom-to-top (index 0 = lowest z-order).
		/// </summary>
		Task<string> GetWindowListAsync(bool includeHidden);

		/// <summary>Returns JSON: { ok, window: { ... } | null }</summary>
		Task<string> GetActiveWindowAsync();

		/// <summary>Global pointer position in compositor coordinates.</summary>
		Task<(int X, int Y)> GetCursorPositionAsync();

		/// <summary>Window handle is the stable_sequence uint32 of Meta.Window.</summary>
		Task FocusWindowAsync(ulong handle);

		/// <summary>
		/// Pass x = int.MinValue to leave position unchanged;
		/// width/height &lt;= 0 to leave size unchanged.
		/// </summary>
		Task MoveResizeWindowAsync(ulong handle, int x, int y, int width, int height);

		/// <summary>state: 0 = normal, 1 = minimized, 2 = maximized.</summary>
		Task SetWindowStateAsync(ulong handle, int state);

		/// <summary>Keep the window above all others (true) or clear keep-above (false).</summary>
		Task SetWindowAboveAsync(ulong handle, bool above);

		Task CloseWindowAsync(ulong handle);

		Task SendMouseMoveAbsoluteAsync(int x, int y);
		Task SendMouseMoveRelativeAsync(int dx, int dy);

		/// <summary>button: 1 = left, 2 = middle, 3 = right (X11 convention).</summary>
		Task SendMouseButtonAsync(uint button, bool pressed);

		/// <summary>
		/// delta in 120-unit wheel increments (positive = up/right).
		/// vertical: true = vertical scroll, false = horizontal.
		/// </summary>
		Task SendMouseScrollAsync(int delta, bool vertical);

		/// <summary>
		/// Capture a screen region entirely inside the compositor process.
		/// No polkit check, no flash, no disk I/O. Returns raw PNG bytes,
		/// or an empty array on failure.
		/// </summary>
		Task<byte[]> CaptureAreaAsync(int x, int y, int width, int height);

		Task<IDisposable> WatchActiveWindowChangedAsync(
			Action<string> handler, Action<Exception> onError = null);

		/// <summary>
		/// Generic window-event stream. The handler receives (type, json) where type is one of
		/// create/close/active/title/minimize/restore/move and json is the affected window's info.
		/// </summary>
		Task<IDisposable> WatchWindowEventAsync(
			Action<(string type, string json)> handler, Action<Exception> onError = null);
	}

#pragma warning restore IDE1006

	/// <summary>
	/// Static bridge to the Keysharp GNOME Shell extension D-Bus service.
	/// Lazily connects on first use and caches the session-bus connection
	/// and proxy for subsequent calls. All public methods are thread-safe.
	/// </summary>
	internal static class GnomeShellBridge
	{
		private const string ServiceName  = "io.github.keysharp.GnomeShell";
		private const string ObjectPath   = "/io/github/keysharp/GnomeShell";
		private const int    TimeoutMs    = 2000;

		private static readonly SemaphoreSlim initSemaphore = new(1, 1);
		private static Connection connection;
		private static IGnomeShell proxy;
		private static bool initFailed;

		// ---- public query/command surface used by GnomeBackend ----------

		internal static bool QueryCursorPosition(out int x, out int y)
		{
			x = 0;
			y = 0;

			try
			{
				var p = EnsureProxy();

				if (p == null)
					return false;

				using var cts = new CancellationTokenSource(TimeoutMs);
				var (rx, ry) = Task.Run(() => p.GetCursorPositionAsync(), cts.Token).GetAwaiter().GetResult();
				x = rx;
				y = ry;
				return true;
			}
			catch
			{
				return false;
			}
		}

		internal static string QueryWindowList(bool includeHidden)
			=> Run(p => p.GetWindowListAsync(includeHidden));

		internal static string QueryActiveWindow()
			=> Run(p => p.GetActiveWindowAsync());

		internal static bool SendFocusWindow(ulong handle)
			=> RunCommand(p => p.FocusWindowAsync(handle));

		internal static bool SendMoveResize(ulong handle, int x, int y, int width, int height)
			=> RunCommand(p => p.MoveResizeWindowAsync(handle, x, y, width, height));

		internal static bool SendSetWindowState(ulong handle, int state)
			=> RunCommand(p => p.SetWindowStateAsync(handle, state));

		internal static bool SendSetWindowAbove(ulong handle, bool above)
			=> RunCommand(p => p.SetWindowAboveAsync(handle, above));

		internal static bool SendCloseWindow(ulong handle)
			=> RunCommand(p => p.CloseWindowAsync(handle));

		internal static bool SendMouseMoveAbsolute(int x, int y)
			=> RunCommand(p => p.SendMouseMoveAbsoluteAsync(x, y));

		internal static bool SendMouseMoveRelative(int dx, int dy)
			=> RunCommand(p => p.SendMouseMoveRelativeAsync(dx, dy));

		internal static bool SendMouseButton(uint button, bool pressed)
			=> RunCommand(p => p.SendMouseButtonAsync(button, pressed));

		internal static bool SendMouseScroll(int delta, bool vertical)
			=> RunCommand(p => p.SendMouseScrollAsync(delta, vertical));

		internal static byte[] CaptureArea(int x, int y, int w, int h)
		{
			const int captureTimeoutMs = 10_000;

			try
			{
				var p = EnsureProxy();

				if (p == null)
					return null;

				using var cts = new CancellationTokenSource(captureTimeoutMs);
				var bytes = Task.Run(() => p.CaptureAreaAsync(x, y, w, h), cts.Token).GetAwaiter().GetResult();
				return bytes is { Length: > 0 } ? bytes : null;
			}
			catch
			{
				return null;
			}
		}

		internal static IDisposable WatchActiveWindowChanged(Action<string> handler)
		{
			try
			{
				var p = EnsureProxy();

				if (p == null)
					return null;

				return Task.Run(() => p.WatchActiveWindowChangedAsync(handler)).GetAwaiter().GetResult();
			}
			catch
			{
				return null;
			}
		}

		internal static IDisposable WatchWindowEvent(Action<string, string> handler)
		{
			try
			{
				var p = EnsureProxy();

				if (p == null)
					return null;

				return Task.Run(() => p.WatchWindowEventAsync(e => handler(e.type, e.json))).GetAwaiter().GetResult();
			}
			catch
			{
				return null;
			}
		}

		// ---- connection management ---------------------------------------

		private static T Run<T>(Func<IGnomeShell, Task<T>> call)
		{
			try
			{
				var p = EnsureProxy();

				if (p == null)
					return default;

				using var cts = new CancellationTokenSource(TimeoutMs);
				return Task.Run(() => call(p), cts.Token).GetAwaiter().GetResult();
			}
			catch
			{
				return default;
			}
		}

		private static bool RunCommand(Func<IGnomeShell, Task> call)
		{
			try
			{
				var p = EnsureProxy();

				if (p == null)
					return false;

				using var cts = new CancellationTokenSource(TimeoutMs);
				Task.Run(() => call(p), cts.Token).GetAwaiter().GetResult();
				return true;
			}
			catch
			{
				return false;
			}
		}

		private static IGnomeShell EnsureProxy()
		{
			// Fast path: already connected.
			if (proxy != null)
				return proxy;

			// Fast path: session bus was unreachable (rare — implies broken environment).
			if (initFailed)
				return null;

			initSemaphore.Wait();

			try
			{
				if (proxy != null)
					return proxy;

				if (initFailed)
					return null;

				var localConn = new Connection(Tmds.DBus.Address.Session);
				Task.Run(() => localConn.ConnectAsync()).GetAwaiter().GetResult();

				// Creating the proxy is cheap — it does not make any D-Bus calls.
				// Individual method calls will fail gracefully if the extension is
				// not yet running, rather than permanently locking out the bridge
				// (which would happen if we probed here and set initFailed on the
				// first miss while the extension was still loading).
				connection = localConn;
				proxy = localConn.CreateProxy<IGnomeShell>(ServiceName, new ObjectPath(ObjectPath));
				return proxy;
			}
			catch
			{
				// Only mark permanently failed when we cannot reach the session bus at
				// all — not when the extension service is merely absent.
				initFailed = true;
				return null;
			}
			finally
			{
				initSemaphore.Release();
			}
		}
	}
}
#endif
