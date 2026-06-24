#if LINUX
using Eto.Drawing;
using Keysharp.Internals.Platform.Unix;
using Keysharp.Internals.Window.Linux.Wayland;
using Keysharp.Internals.Window.Linux.X11;

namespace Keysharp.Internals.Window.Linux
{
	/// <summary>
	/// Per-compositor dispatch for capturing a single foreign window on Linux, sitting behind
	/// <see cref="Keysharp.Builtins.GuiHelper.CaptureWindowContent"/>. Returns <c>(null, 1.0)</c> when no
	/// true window capture is possible on the current session, so the caller falls back to grabbing the
	/// window's on-screen rectangle (which is only correct while the window is unobscured).
	///
	/// <list type="bullet">
	/// <item><b>X11</b> (including Cinnamon/Muffin and KDE/GNOME running under X11): <c>XGetImage</c>,
	/// with XComposite redirection so windows hidden behind others still capture correctly.</item>
	/// <item><b>GNOME Wayland</b>: the Keysharp GNOME Shell extension reads the window actor's own buffer
	/// (<c>meta_window_actor_get_image</c>) via <c>keysharp-screencap</c>, so occluded windows work.</item>
	/// <item><b>KWin Wayland</b>: not yet wired (returns null → rectangle fallback); see the TODO below.</item>
	/// <item><b>wlroots / unknown</b>: no foreign-window protocol → rectangle fallback.</item>
	/// </list>
	/// </summary>
	internal static class LinuxWindowCapture
	{
		/// <param name="handle">The window handle as stored by the active provider: an X11 window id on
		/// X11, or the compositor window id (GNOME stable-sequence) on Wayland.</param>
		/// <returns>The captured bitmap (physical pixels) and its physical-pixels-per-logical-unit scale,
		/// or (null, 1.0) when no true window capture is available for this session.</returns>
		internal static (Bitmap bmp, double scale) TryCapture(nint handle)
		{
			if (handle == 0)
				return (null, 1.0);

			if (!PlatformManager.IsWaylandSession)
				return (X11Capture.TryCaptureWindow(handle.ToInt64()), 1.0);

			var backend = WaylandBackend.Current;

			// GNOME: occlusion-independent — the extension images the window actor's backing buffer.
			if (backend is WaylandBackend.GnomeBackend)
				return (ScreencapHelper.CaptureGnomeWindow((ulong)handle.ToInt64()), 1.0);

			// TODO(KWin window capture): KWin's org.kde.KWin.ScreenShot2.CaptureWindow re-renders a window
			// off-screen (occlusion-independent) but takes the window's internalId UUID, which must first be
			// resolved from the X11/foreign handle via KWin's scripting D-Bus, and requires a restricted
			// X-KDE-DBUS-Restricted-Interfaces .desktop for the helper. Until that plumbing exists, fall
			// through to the rectangle-grab fallback rather than capturing the wrong (active) window.
			if (backend is WaylandBackend.KWinBackend)
				return (null, 1.0);

			// wlr-screencopy captures whole outputs, not individual windows.
			return (null, 1.0);
		}
	}
}
#endif
