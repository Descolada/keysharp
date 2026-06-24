#if LINUX
namespace Keysharp.Internals.Window.Linux.Wayland
{
	/// <summary>The kind of a <see cref="WaylandWindowEvent"/>. These map 1:1 onto the platform-neutral
	/// <c>WindowEventType</c> the WinEvent manager consumes (Created additionally implies Show).</summary>
	internal enum WaylandWindowEventKind
	{
		Created,
		Closed,
		Activated,
		TitleChanged,
		Minimized,
		Restored,
		MoveResized
	}

	/// <summary>A normalized window event produced by an <see cref="IWaylandBackend"/> event source, carrying the
	/// kind and the backend's stable window handle (the same handle <c>TryGetWindow</c>/<c>WinExist</c> use). For
	/// MoveResized events <see cref="Bounds"/> carries the window's screen geometry (compositors already have it),
	/// so the consumer doesn't have to query it back per move.</summary>
	internal readonly record struct WaylandWindowEvent(WaylandWindowEventKind Kind, nint Handle)
	{
		internal Rectangle? Bounds { get; init; }
	}

	/// <summary>
	/// Compositor-specific privileged-introspection backend. Wayland's core protocol forbids
	/// foreign clients from querying things like the global cursor position, other windows'
	/// geometry, or other windows' z-order; the only way to get this information correctly is
	/// to talk to the compositor itself through whatever IPC channel it offers (D-Bus for
	/// KWin, JSON over Unix socket for sway and hyprland, a private Wayland extension for
	/// COSMIC, etc).
	///
	/// This interface abstracts those backends so the call sites in <c>MouseGetPos</c>,
	/// <c>WinGetPos</c>, etc. don't have to know which one is active. Implementations should
	/// return <c>false</c> from any Try* method they don't (yet) implement, so a partially
	/// finished backend is still usable for the methods it does cover. The factory at
	/// <see cref="WaylandBackend"/> picks one backend per process based on
	/// detection.
	///
	/// Compositors with no introspection IPC (labwc, river without flowing, GNOME for foreign
	/// clients) get the null backend: every Try* method returns false and the caller falls
	/// back to whatever degraded path it has (keysharp-inputd, Forms.Mouse, or a hard error).
	/// </summary>
	internal interface IWaylandBackend
	{
		/// <summary>Human-readable name for diagnostics ("KWin", "sway", "hyprland", ...).</summary>
		string Name { get; }

		/// <summary>
		/// True when the backend can simulate mouse input via
		/// <see cref="TrySendMouseMoveAbsolute"/>, <see cref="TrySendMouseButton"/>, etc.
		/// Used by <see cref="Keysharp.Internals.Input.Linux.LinuxKeyboardMouseSender"/>
		/// to prefer compositor-native mouse injection over SharpHook on Wayland.
		/// </summary>
		bool SupportsMouse => false;

		/// <summary>
		/// True when this backend can push window lifecycle/state events through
		/// <see cref="SubscribeWindowEvents"/>. When false, the WinEvent layer either falls back to a generic
		/// polling source (if the backend can list windows) or to the X11/XWayland backend.
		/// </summary>
		bool SupportsWindowEvents => false;

		/// <summary>
		/// Subscribe to window events (create/close/activate/title-change/minimize/restore/move). The sink may be
		/// invoked on any thread and must not block. Returns an <see cref="IDisposable"/> that ends the subscription
		/// (idempotent), or null if events are unsupported. Handles match those returned by the Try* query methods,
		/// so a consumer can resolve full window state on demand.
		/// </summary>
		IDisposable SubscribeWindowEvents(Action<WaylandWindowEvent> sink) => null;

		/// <summary>
		/// Best-effort global cursor position in screen coordinates. Returns true on success
		/// with <paramref name="x"/>/<paramref name="y"/> set to a valid pixel coordinate;
		/// false if the backend can't currently answer (compositor offline, IPC failed, etc.).
		/// </summary>
		bool TryGetCursorPos(out int x, out int y);

		bool TryListWindows(bool includeHidden, out IReadOnlyList<WaylandWindowInfo> windows)
		{
			windows = [];
			return false;
		}

		bool TryGetActiveWindow(out WaylandWindowInfo window)
		{
			window = null;
			return false;
		}

		bool TryGetWindow(nint handle, out WaylandWindowInfo window)
		{
			window = null;
			return false;
		}

		bool TryGetWindowAt(int x, int y, out WaylandWindowInfo window)
		{
			window = null;
			return false;
		}

		bool TryActivateWindow(nint handle) => false;

		bool TryMoveResizeWindow(nint handle, Rectangle bounds, bool setPosition, bool setSize) => false;

		bool TrySetWindowState(nint handle, FormWindowState state) => false;

		/// <summary>Keep the window above (true) / clear keep-above (false). False = unsupported.</summary>
		bool TrySetAlwaysOnTop(nint handle, bool onTop) => false;

		bool TryCloseWindow(nint handle) => false;

		// ---- Mouse simulation -------------------------------------------
		// Default implementations return false (backend does not support it).
		// GnomeBackend overrides these to use Clutter.VirtualInputDevice via
		// the shell extension D-Bus service.

		bool TrySendMouseMoveAbsolute(int x, int y) => false;

		bool TrySendMouseMoveRelative(int dx, int dy) => false;

		/// <summary>button: 1 = left, 2 = middle, 3 = right (X11 convention).</summary>
		bool TrySendMouseButton(uint button, bool pressed) => false;

		/// <summary>
		/// delta in 120-unit wheel increments (positive = up/right).
		/// vertical: true = vertical scroll axis, false = horizontal.
		/// </summary>
		bool TrySendMouseScroll(int delta, bool vertical) => false;
	}
}
#endif
