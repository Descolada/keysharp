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

		/// <summary>Work area (monitor minus panels/struts) of the primary monitor, in screen coordinates.</summary>
		Task<(int X, int Y, int Width, int Height)> GetWorkAreaAsync();

		/// <summary>Attach this process as an overlay owner so the shell can reap our overlays when we die.
		/// ownerKey = "pid:starttime"; busName = our unique D-Bus connection name.</summary>
		Task<bool> RegisterHighlightOwnerAsync(string ownerKey, string busName);

		/// <summary>Window handle is the stable_sequence uint32 of Meta.Window. False = no such window.</summary>
		Task<bool> FocusWindowAsync(ulong handle);

		/// <summary>Raise the window to the top of the stack. False = no such window.</summary>
		Task<bool> RaiseWindowAsync(ulong handle);

		/// <summary>Lower the window to the bottom of the stack. False = no such window.</summary>
		Task<bool> LowerWindowAsync(ulong handle);

		/// <summary>
		/// Pass x = int.MinValue to leave position unchanged;
		/// width/height &lt;= 0 to leave size unchanged. False = no such window.
		/// </summary>
		Task<bool> MoveResizeWindowAsync(ulong handle, int x, int y, int width, int height);

		/// <summary>state: 0 = normal, 1 = minimized, 2 = maximized. False = no such window.</summary>
		Task<bool> SetWindowStateAsync(ulong handle, int state);

		/// <summary>Keep the window above all others (true) or clear keep-above (false). False = no such window.</summary>
		Task<bool> SetWindowAboveAsync(ulong handle, bool above);

		/// <summary>Show (true) or hide (false) the window's titlebar/decorations. False = no such window.</summary>
		Task<bool> SetWindowDecoratedAsync(ulong handle, bool decorated);

		/// <summary>Set the window's opacity, 0 (transparent) to 255 (opaque). False = no such window.</summary>
		Task<bool> SetWindowOpacityAsync(ulong handle, int opacity);

		/// <summary>Request the window to close. False = no such window.</summary>
		Task<bool> CloseWindowAsync(ulong handle);

		/// <summary>Force-kill the window's client (SIGKILL-equivalent), falling back to close. False = no such window.</summary>
		Task<bool> KillWindowAsync(ulong handle);

		/// <summary>
		/// Draw or update a click-through rectangle-outline overlay (GNOME's stand-in for the
		/// wlr-layer-shell highlight Keysharp uses on KWin/wlroots). id identifies the overlay so it can
		/// be moved/resized or hidden; geometry is in screen coordinates; color is an "RRGGBB" hex string;
		/// thickness is the outline width in pixels. ownerKey/busName attribute it for owner-death cleanup.
		/// </summary>
		Task<bool> ShowHighlightAsync(uint id, string ownerKey, string busName, int x, int y, int width, int height, string color, int thickness);

		/// <summary>Remove the overlay previously created with the given id.</summary>
		Task<bool> HideHighlightAsync(uint id, string ownerKey, string busName);

		/// <summary>Create or update a generic PNG-backed click-through overlay. False = the shell rejected it.</summary>
		Task<bool> ShowImageOverlayAsync(uint id, string ownerKey, string busName, int x, int y, int width, int height, byte[] pngBytes);

		/// <summary>Reposition/resize an existing overlay by id, reusing its already-uploaded pixels (no re-encode).
		/// False = no such overlay; the caller should re-Show instead.</summary>
		Task<bool> MoveImageOverlayAsync(uint id, string ownerKey, string busName, int x, int y, int width, int height);

		/// <summary>Remove a generic PNG-backed overlay.</summary>
		Task<bool> HideImageOverlayAsync(uint id, string ownerKey, string busName);

		/// <summary>True when the shell can draw a click-through tooltip on the caller's behalf.</summary>
		Task<bool> SupportsTooltipAsync();

		/// <summary>Draw or update a click-through tooltip in slot for this owner; empty text clears it.</summary>
		Task<bool> ShowTooltipAsync(int slot, string ownerKey, string busName, string text, int x, int y);

		/// <summary>Remove the tooltip in the given slot for this owner.</summary>
		Task<bool> HideTooltipAsync(int slot, string ownerKey, string busName);

		Task<bool> SendMouseMoveAbsoluteAsync(int x, int y);
		Task<bool> SendMouseMoveRelativeAsync(int dx, int dy);

		/// <summary>button: 1 = left, 2 = middle, 3 = right (X11 convention).</summary>
		Task<bool> SendMouseButtonAsync(uint button, bool pressed);

		/// <summary>
		/// delta in 120-unit wheel increments (positive = up/right).
		/// vertical: true = vertical scroll, false = horizontal.
		/// </summary>
		Task<bool> SendMouseScrollAsync(int delta, bool vertical);

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

		// Backoff before retrying owner registration after a miss (extension still loading / absent).
		private const long RegisterRetryBackoffMs = 5000;

		private static readonly SemaphoreSlim initSemaphore = new(1, 1);
		private static Connection connection;
		private static IGnomeShell proxy;
		private static bool initFailed;

		// Overlay-ownership plumbing (mirrors CinnamonBackend): our stable process key, the unique name of
		// our D-Bus connection, and the sticky "already registered under this connection" latch + retry gate.
		private static readonly string HighlightOwnerKey = WaylandOverlayOwner.Key;
		private static string connectionLocalName = "";
		private static string registeredHighlightOwnerBusName = "";
		private static long highlightOwnerRegisterRetryAfter;

		// Cache the tooltip-support probe so backing selection doesn't make a blocking D-Bus call each time.
		private const long TooltipSupportPresentCacheMs = 30000;
		private const long TooltipSupportMissingCacheMs = 5000;
		private static bool tooltipSupportCached;
		private static long tooltipSupportCacheUntil;

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

		internal static bool QueryWorkArea(out Rectangle area)
		{
			area = Rectangle.Empty;

			try
			{
				var p = EnsureProxy();

				if (p == null)
					return false;

				using var cts = new CancellationTokenSource(TimeoutMs);
				var (x, y, w, h) = Task.Run(() => p.GetWorkAreaAsync(), cts.Token).GetAwaiter().GetResult();

				if (w <= 0 || h <= 0)
					return false;

				area = new Rectangle(x, y, w, h);
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
			=> Run(p => p.FocusWindowAsync(handle));

		internal static bool SendRaiseWindow(ulong handle)
			=> Run(p => p.RaiseWindowAsync(handle));

		internal static bool SendLowerWindow(ulong handle)
			=> Run(p => p.LowerWindowAsync(handle));

		internal static bool SendMoveResize(ulong handle, int x, int y, int width, int height)
			=> Run(p => p.MoveResizeWindowAsync(handle, x, y, width, height));

		internal static bool SendSetWindowState(ulong handle, int state)
			=> Run(p => p.SetWindowStateAsync(handle, state));

		internal static bool SendSetWindowAbove(ulong handle, bool above)
			=> Run(p => p.SetWindowAboveAsync(handle, above));

		internal static bool SendSetWindowDecorated(ulong handle, bool decorated)
			=> Run(p => p.SetWindowDecoratedAsync(handle, decorated));

		internal static bool SendSetOpacity(ulong handle, object value)
		{
			// "off" clears transparency (fully opaque); otherwise coerce to a 0-255 alpha. Mirrors CinnamonBackend.
			var alpha = value is string s && s.Equals("off", StringComparison.OrdinalIgnoreCase)
				? 255
				: Math.Clamp((int)value.Al(), 0, 255);

			return Run(p => p.SetWindowOpacityAsync(handle, alpha));
		}

		internal static bool SendCloseWindow(ulong handle)
			=> Run(p => p.CloseWindowAsync(handle));

		internal static bool SendKillWindow(ulong handle)
			=> Run(p => p.KillWindowAsync(handle));

		internal static bool SendShowHighlight(uint id, int x, int y, int width, int height, string color, int thickness)
			=> Run(p => p.ShowHighlightAsync(id, HighlightOwnerKey, connectionLocalName, x, y, width, height, color, thickness));

		internal static bool SendHideHighlight(uint id)
			=> Run(p => p.HideHighlightAsync(id, HighlightOwnerKey, connectionLocalName));

		internal static bool SendShowImageOverlay(uint id, int x, int y, int width, int height, byte[] pngBytes)
			=> pngBytes is { Length: > 0 }
			   && Run(p => p.ShowImageOverlayAsync(id, HighlightOwnerKey, connectionLocalName, x, y, width, height, pngBytes));

		internal static bool SendMoveImageOverlay(uint id, int x, int y, int width, int height)
			=> Run(p => p.MoveImageOverlayAsync(id, HighlightOwnerKey, connectionLocalName, x, y, width, height));

		internal static bool SendHideImageOverlay(uint id)
			=> Run(p => p.HideImageOverlayAsync(id, HighlightOwnerKey, connectionLocalName));

		internal static bool SupportsTooltip()
		{
			var now = Environment.TickCount64;

			if (now < tooltipSupportCacheUntil)
				return tooltipSupportCached;

			var ok = Run(p => p.SupportsTooltipAsync());
			tooltipSupportCached = ok;
			tooltipSupportCacheUntil = now + (ok ? TooltipSupportPresentCacheMs : TooltipSupportMissingCacheMs);
			return ok;
		}

		internal static bool SendShowTooltip(int slot, string text, int x, int y)
			=> Run(p => p.ShowTooltipAsync(slot, HighlightOwnerKey, connectionLocalName, text ?? "", x, y));

		internal static bool SendHideTooltip(int slot)
			=> Run(p => p.HideTooltipAsync(slot, HighlightOwnerKey, connectionLocalName));

		internal static bool SendMouseMoveAbsolute(int x, int y)
			=> Run(p => p.SendMouseMoveAbsoluteAsync(x, y));

		internal static bool SendMouseMoveRelative(int dx, int dy)
			=> Run(p => p.SendMouseMoveRelativeAsync(dx, dy));

		internal static bool SendMouseButton(uint button, bool pressed)
			=> Run(p => p.SendMouseButtonAsync(button, pressed));

		internal static bool SendMouseScroll(int delta, bool vertical)
			=> Run(p => p.SendMouseScrollAsync(delta, vertical));

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

		private static IGnomeShell EnsureProxy()
		{
			// Fast path: already connected. Re-attempt owner registration each time — it is cheap and
			// self-latching (a no-op once registered under the current connection).
			if (proxy != null)
			{
				RegisterHighlightOwner(proxy);
				return proxy;
			}

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
				var info = Task.Run(() => localConn.ConnectAsync()).GetAwaiter().GetResult();
				// Our unique bus name (":1.x") — the shell tags our overlays with it and reaps them when
				// this name drops off the bus (see the extension's NameOwnerChanged watch).
				connectionLocalName = info?.LocalName ?? "";

				// Creating the proxy is cheap — it does not make any D-Bus calls.
				// Individual method calls will fail gracefully if the extension is
				// not yet running, rather than permanently locking out the bridge
				// (which would happen if we probed here and set initFailed on the
				// first miss while the extension was still loading).
				connection = localConn;
				proxy = localConn.CreateProxy<IGnomeShell>(ServiceName, new ObjectPath(ObjectPath));
				RegisterHighlightOwner(proxy);
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

		// Announce this process to the shell extension as an overlay owner so it can attribute our overlays
		// and reap them when we die. Guarded so the real D-Bus call fires at most once per connection name,
		// with a short backoff on miss (extension still loading / absent). Mirrors CinnamonBackend.
		private static void RegisterHighlightOwner(IGnomeShell p)
		{
			if (p == null || string.IsNullOrEmpty(connectionLocalName) || registeredHighlightOwnerBusName == connectionLocalName)
				return;

			if (Environment.TickCount64 < highlightOwnerRegisterRetryAfter)
				return;

			try
			{
				var task = Task.Run(() => p.RegisterHighlightOwnerAsync(HighlightOwnerKey, connectionLocalName));

				if (!task.Wait(TimeoutMs))
				{
					highlightOwnerRegisterRetryAfter = Environment.TickCount64 + RegisterRetryBackoffMs;
					return;
				}

				if (task.GetAwaiter().GetResult())
				{
					registeredHighlightOwnerBusName = connectionLocalName;
					highlightOwnerRegisterRetryAfter = 0;
				}
				else
				{
					highlightOwnerRegisterRetryAfter = Environment.TickCount64 + RegisterRetryBackoffMs;
				}
			}
			catch
			{
				highlightOwnerRegisterRetryAfter = Environment.TickCount64 + RegisterRetryBackoffMs;
			}
		}
	}
}
#endif
