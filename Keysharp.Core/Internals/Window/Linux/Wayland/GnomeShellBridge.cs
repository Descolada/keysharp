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
		/// active, minimized, maximized, visible, transparency } ... ] }
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

		/// <summary>As MoveResizeWindow but the window is identified by its X11 window id (get_xwindow),
		/// for X11 sessions where the caller has an XID rather than a stable_sequence. False = no such window.</summary>
		Task<bool> MoveResizeWindowByXidAsync(ulong xid, int x, int y, int width, int height);

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

		/// <summary>Create or update a generic PNG-backed click-through overlay. False = the shell rejected it.</summary>
		Task<bool> ShowImageOverlayAsync(uint id, string ownerKey, string busName, int x, int y, int width, int height, byte[] pngBytes);

		/// <summary>Reposition/resize an existing overlay by id, reusing its already-uploaded pixels (no re-encode).
		/// False = no such overlay; the caller should re-Show instead.</summary>
		Task<bool> MoveImageOverlayAsync(uint id, string ownerKey, string busName, int x, int y, int width, int height);

		/// <summary>Remove a generic PNG-backed overlay.</summary>
		Task<bool> HideImageOverlayAsync(uint id, string ownerKey, string busName);

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
		private const string DBusServiceName = "org.freedesktop.DBus";
		private const string DBusObjectPath  = "/org/freedesktop/DBus";
		private const int    TimeoutMs    = 2000;
		private const int    ImageOverlayTimeoutMs = 10_000;
		// The owner probe (NameHasOwner) answers INSTANTLY whether present (fast true) or genuinely absent
		// (fast false) — a timeout here only ever means a slow/cold bus, never a real absence. The old 250ms
		// budget lost that race on a warm (fast) process start: a HUD shows its overlays during startup before
		// the lazily-established session-bus connection is warm, the first probe timed out, and (see below) the
		// miss latched "extension absent" for 5s — routing that whole run's overlays to the Eto fallback with no
		// error. A cold connection's first round-trip deserves the same budget as the connect itself.
		private const int    ExtensionOwnerCheckTimeoutMs = 2000;
		private const int    ExtensionMissingCacheMs = 5000;
		private const int    ExtensionPresentCacheMs = 1000;
		// After an AMBIGUOUS probe timeout (not a definitive not-owned) with no prior positive, re-check soon
		// instead of latching "absent" for the full missing-cache window — the extension is expected on GNOME.
		private const int    ExtensionProbeTimeoutRetryMs = 250;

		// Backoff before retrying owner registration after a miss (extension still loading / absent).
		private const long RegisterRetryBackoffMs = 5000;
		// Backoff before re-attempting a session-bus connect that timed out / threw. A transient failure must
		// not permanently disable the bridge, so it arms this instead of the permanent initFailed latch.
		private const long ConnectRetryBackoffMs = 5000;

		private static readonly SemaphoreSlim initSemaphore = new(1, 1);
		private static Connection connection;
		private static IFreedesktopDBus dbusProxy;
		private static IGnomeShell proxy;
		// Permanent lockout — reserved for a definitively-unreachable session bus (see EnsureConnection).
		private static bool initFailed;
		// Time-based retry gate (Environment.TickCount64) for a transient connect failure/timeout.
		private static long nextConnectAttempt;
		private static long extensionOwnerCacheUntil;
		private static bool extensionOwnerCached;

		// Overlay-ownership plumbing (mirrors CinnamonBackend): our stable process key, the unique name of
		// our D-Bus connection, and the sticky "already registered under this connection" latch + retry gate.
		private static readonly string HighlightOwnerKey = WaylandOverlayOwner.Key;
		private static string connectionLocalName = "";
		private static string registeredHighlightOwnerBusName = "";
		private static long highlightOwnerRegisterRetryAfter;

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

				var task = Task.Run(() => p.GetCursorPositionAsync());

				if (!task.WaitWithoutInterruption(TimeoutMs))
					return false;

				(x, y) = task.GetAwaiter().GetResult();
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

				var task = Task.Run(() => p.GetWorkAreaAsync());

				if (!task.WaitWithoutInterruption(TimeoutMs))
					return false;

				var (x, y, w, h) = task.GetAwaiter().GetResult();

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

		// Move/resize by X11 window id (X11 sessions). GNOME Shell disables Eval, so this relies on the
		// extension method; a window is unreachable (returns false → caller falls back to XMoveWindow) until
		// an extension carrying MoveResizeWindowByXid is installed and the shell reloaded.
		internal static bool SendMoveResizeByXid(ulong xid, int x, int y, int width, int height)
			=> Run(p => p.MoveResizeWindowByXidAsync(xid, x, y, width, height));

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

		internal static OverlayShowResult SendShowImageOverlay(uint id, int x, int y, int width, int height, byte[] pngBytes)
			=> pngBytes is { Length: > 0 }
			   ? RunShow(p => p.ShowImageOverlayAsync(id, HighlightOwnerKey, connectionLocalName, x, y, width, height, pngBytes),
						 ImageOverlayTimeoutMs)
			   : OverlayShowResult.Failed;

		internal static bool SendMoveImageOverlay(uint id, int x, int y, int width, int height)
			=> Run(p => p.MoveImageOverlayAsync(id, HighlightOwnerKey, connectionLocalName, x, y, width, height));

		internal static bool SendHideImageOverlay(uint id)
			=> Run(p => p.HideImageOverlayAsync(id, HighlightOwnerKey, connectionLocalName));

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

				var task = Task.Run(() => p.CaptureAreaAsync(x, y, w, h));

				if (!task.WaitWithoutInterruption(captureTimeoutMs))
					return null;

				var bytes = task.GetAwaiter().GetResult();
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

				var task = Task.Run(() => p.WatchActiveWindowChangedAsync(handler));
				return task.WaitWithoutInterruption(TimeoutMs) ? task.GetAwaiter().GetResult() : null;
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

				var task = Task.Run(() => p.WatchWindowEventAsync(e => handler(e.type, e.json)));
				return task.WaitWithoutInterruption(TimeoutMs) ? task.GetAwaiter().GetResult() : null;
			}
			catch
			{
				return null;
			}
		}

		// ---- connection management ---------------------------------------

		private static T Run<T>(Func<IGnomeShell, Task<T>> call, int timeoutMs = TimeoutMs)
		{
			try
			{
				var p = EnsureProxy();

				if (p == null)
					return default;

				var task = Task.Run(() => call(p));
				return task.WaitWithoutInterruption(timeoutMs) ? task.GetAwaiter().GetResult() : default;
			}
			catch
			{
				return default;
			}
		}

		// Show an image overlay, classifying the outcome so the caller can tell an ambiguous timeout (commit to
		// the compositor) from a definitive failure (fall back to Eto). A plain Run collapses both to `false`,
		// which is exactly what caused the duplicated-overlay bug: a slow first upload timed out client-side yet
		// still created the shell actor, and the false return triggered a second, Eto, surface.
		private static OverlayShowResult RunShow(Func<IGnomeShell, Task<bool>> call, int timeoutMs)
		{
			IGnomeShell p;

			try
			{
				// Do not pre-gate the authoritative Show call on NameHasOwner. The owner probe is a cached availability
				// hint and can time out under startup/load even while this service answers ordinary calls. Calling the
				// well-known name directly fails definitively and quickly when it is truly absent.
				p = EnsureProxy(requireServiceOwner: false);
			}
			catch
			{
				return OverlayShowResult.Failed;
			}

			if (p == null)
				return OverlayShowResult.Failed;

			try
			{
				var task = Task.Run(() => call(p));

				if (!task.WaitWithoutInterruption(timeoutMs))
					return OverlayShowResult.TimedOut;

				return task.GetAwaiter().GetResult() ? OverlayShowResult.Shown : OverlayShowResult.Failed;
			}
			catch
			{
				return OverlayShowResult.Failed;
			}
		}

		private static IGnomeShell EnsureProxy(bool requireServiceOwner = true)
		{
			if (requireServiceOwner && !ExtensionServiceHasOwner())
				return null;

			// Fast path: already connected. Re-attempt owner registration each time — it is cheap and
			// self-latching (a no-op once registered under the current connection).
			if (proxy != null)
			{
				RegisterHighlightOwner(proxy);
				return proxy;
			}

			var conn = EnsureConnection();

			if (conn == null)
				return null;

			initSemaphore.Wait();
			try
			{
				if (proxy != null)
					return proxy;

				// Creating the proxy is cheap — it does not make any D-Bus calls.
				// Individual method calls will fail gracefully if the extension is
				// not yet running, rather than permanently locking out the bridge
				// (which would happen if we probed here and set initFailed on the
				// first miss while the extension was still loading).
				proxy = conn.CreateProxy<IGnomeShell>(ServiceName, new ObjectPath(ObjectPath));
				RegisterHighlightOwner(proxy);
				return proxy;
			}
			catch
			{
				proxy = null;
				return null;
			}
			finally
			{
				initSemaphore.Release();
			}
		}

		private static Connection EnsureConnection()
		{
			if (connection != null)
				return connection;

			// Permanent lockout is reserved for a definitively-unreachable session bus (no session-bus address in
			// the environment — it will never appear). A transient connect failure/timeout instead arms a short
			// time-based backoff (nextConnectAttempt) so a later call transparently re-attempts, rather than a single
			// slow first connect disabling all GNOME window management + overlays for the whole process lifetime.
			if (initFailed)
				return null;

			// Fast path: within the retry backoff after a transient failure — don't hammer the bus.
			if (Environment.TickCount64 < nextConnectAttempt)
				return null;

			initSemaphore.Wait();

			// Hoisted above the try so the catch can dispose a connection that was created but never published.
			Connection localConn = null;

			try
			{
				if (connection != null)
					return connection;

				if (initFailed || Environment.TickCount64 < nextConnectAttempt)
					return null;

				var sessionAddress = Tmds.DBus.Address.Session;

				if (string.IsNullOrEmpty(sessionAddress))
				{
					// No session bus is configured for this process; it cannot appear later, so latch permanently.
					initFailed = true;
					return null;
				}

				localConn = new Connection(sessionAddress);
				var connectTask = Task.Run(() => localConn.ConnectAsync());

				// Pump the message loop while waiting (WaitWithoutInterruption) so the connect deadline doesn't
				// freeze hotkey/UI processing; a plain .Wait would stall the pump for up to TimeoutMs.
				if (!connectTask.WaitWithoutInterruption(TimeoutMs))
				{
					try { localConn.Dispose(); } catch { }

					// Slow / not-yet-up session bus: retry after the backoff instead of a permanent latch.
					nextConnectAttempt = Environment.TickCount64 + ConnectRetryBackoffMs;
					return null;
				}

				var info = connectTask.GetAwaiter().GetResult();
				// Our unique bus name (":1.x") — the shell tags our overlays with it and reaps them when
				// this name drops off the bus (see the extension's NameOwnerChanged watch).
				connectionLocalName = info?.LocalName ?? "";
				// If gnome-shell restarts (crash / `r` in Alt+F2 / extension reload) the session-bus connection
				// is torn down; drop the cached connection + proxies so the next call transparently reconnects
				// instead of failing forever against a dead handle. Mirrors CinnamonShellBridge.
				localConn.StateChanged += (_, e) =>
				{
					if (e.State == Tmds.DBus.ConnectionState.Disconnected)
						ResetConnection(localConn);
				};
				connection = localConn;
				nextConnectAttempt = 0;
				return connection;
			}
			catch
			{
				// Creating/connecting threw unexpectedly — treat as transient and back off; do NOT permanently
				// latch (the bus may simply have been momentarily unavailable). Dispose the connection we created but
				// never published (the `connection = localConn` line is past every throw point), else a present-but-
				// faulting bus leaks one Tmds.DBus Connection — a socket/native handle — every ConnectRetryBackoffMs.
				try { localConn?.Dispose(); } catch { }
				nextConnectAttempt = Environment.TickCount64 + ConnectRetryBackoffMs;
				return null;
			}
			finally
			{
				initSemaphore.Release();
			}
		}

		// Drop a dead session-bus connection and every handle derived from it so the next call reconnects from
		// scratch. Called from the connection's StateChanged watch (deadConnection = the connection that dropped);
		// the guard ignores a stale event for a connection we have already replaced.
		private static void ResetConnection(Connection deadConnection = null)
		{
			if (deadConnection != null && connection != null && !ReferenceEquals(connection, deadConnection))
				return;

			try
			{
				if (deadConnection == null)
					connection?.Dispose();
			}
			catch
			{
			}

			connection = null;
			dbusProxy = null;
			proxy = null;
			connectionLocalName = "";
			registeredHighlightOwnerBusName = "";
			highlightOwnerRegisterRetryAfter = 0;
			extensionOwnerCached = false;
			extensionOwnerCacheUntil = 0;
			initFailed = false;
			nextConnectAttempt = 0;
		}

		// Whether the Keysharp GNOME Shell extension currently owns its D-Bus service. This is the single
		// capability gate for the extension-provided surfaces (all GNOME window ops + compositor image overlays go
		// through it). Cheap and cached; a stale/incompatible extension that owns the name but errors on a specific
		// call is caught reactively by that call (e.g. the image-overlay tri-state falls back to Eto on failure).
		internal static bool ExtensionServiceHasOwner()
		{
			var now = Environment.TickCount64;

			if (now < extensionOwnerCacheUntil)
				return extensionOwnerCached;

			try
			{
				var conn = EnsureConnection();

				if (conn == null)
					return false;

				dbusProxy ??= conn.CreateProxy<IFreedesktopDBus>(DBusServiceName, new ObjectPath(DBusObjectPath));
				var task = Task.Run(() => dbusProxy.NameHasOwnerAsync(ServiceName));

				// Pump the message loop while waiting (never a plain .Wait) so a momentarily-slow bus probe
				// cannot freeze hotkey/timer/UI processing on the calling thread.
				if (!task.WaitWithoutInterruption(ExtensionOwnerCheckTimeoutMs))
				{
					// A probe TIMEOUT is ambiguous (the bus is momentarily slow / the connection is cold at
					// startup), not a definitive "absent". If we recently saw the extension present, keep that
					// answer; otherwise return "unknown" (false) but re-probe within a frame or two rather than
					// latching "absent" for the full 5s missing-cache window. A genuinely absent name answers
					// FALSE instantly (never times out), so a timeout is never real absence — locking overlays to
					// the Eto fallback for 5s on a cold-start probe is exactly what routed a whole run to Eto.
					extensionOwnerCacheUntil = now + (extensionOwnerCached ? ExtensionPresentCacheMs : ExtensionProbeTimeoutRetryMs);

					if (!extensionOwnerCached)
						proxy = null;

					return extensionOwnerCached;
				}

				var hasOwner = task.GetAwaiter().GetResult();
				extensionOwnerCached = hasOwner;
				extensionOwnerCacheUntil = now + (hasOwner ? ExtensionPresentCacheMs : ExtensionMissingCacheMs);

				if (!hasOwner)
				{
					proxy = null;
					registeredHighlightOwnerBusName = "";
					highlightOwnerRegisterRetryAfter = 0;
				}

				return hasOwner;
			}
			catch
			{
				extensionOwnerCached = false;
				extensionOwnerCacheUntil = now + ExtensionMissingCacheMs;
				proxy = null;
				return false;
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

				// Pump the message loop while waiting (never a plain .Wait) — this runs from hotkey/timer
				// actions and must not stall the pump on a cold D-Bus channel.
				if (!task.WaitWithoutInterruption(TimeoutMs))
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
