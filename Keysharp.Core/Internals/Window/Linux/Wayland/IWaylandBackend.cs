#if LINUX
namespace Keysharp.Internals.Window.Linux.Wayland
{
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

		bool TryCloseWindow(nint handle) => false;
	}
}
#endif
