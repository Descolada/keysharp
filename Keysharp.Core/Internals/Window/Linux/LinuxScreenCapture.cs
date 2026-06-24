#if LINUX
using Eto.Drawing;
using Keysharp.Internals.Platform.Unix;
using Keysharp.Internals.Window.Linux.Wayland;
using Keysharp.Internals.Window.Linux.X11;

namespace Keysharp.Internals.Window.Linux
{
	/// <summary>
	/// Single entry point for screen and window capture on Linux, called from the cross-platform
	/// <see cref="Keysharp.Builtins.GuiHelper.GetScreen"/> and
	/// <see cref="Keysharp.Builtins.GuiHelper.CaptureWindowContent"/> (which keep the permission
	/// enforcement and the Windows/macOS branches). Dispatches to the X11 and Wayland-compositor backends:
	/// <see cref="X11ScreenCapture"/> and <see cref="WaylandScreenCapture"/> / <see cref="ScreencapHelper"/>.
	/// </summary>
	internal static class LinuxScreenCapture
	{
		/// <summary>
		/// Grabs a screen rectangle. On Wayland this goes through the compositor-native path
		/// (<see cref="WaylandScreenCapture"/>: wlr-screencopy, or the KWin/GNOME keysharp-screencap
		/// helper) because Gdk's root-window pixbuf path returns only our own surfaces (or nothing) for
		/// foreign clients; on X11 it falls back to Eto's root-window grab. Returns null on failure.
		/// </summary>
		internal static Bitmap GetScreen(int x, int y, int w, int h)
		{
			var bmp = PlatformManager.IsWaylandSession
				? WaylandScreenCapture.TryCapture(x, y, w, h)
				: null;

			return bmp ?? (Eto.Forms.Screen.PrimaryScreen.GetImage(new RectangleF(x, y, w, h)) as Bitmap);
		}

		/// <summary>
		/// Captures a single foreign window's contents. Returns <c>(null, 1.0)</c> when no true window
		/// capture is available on the current session, so the caller falls back to grabbing the window's
		/// on-screen rectangle (only correct while the window is unobscured).
		/// <list type="bullet">
		/// <item><b>X11</b> (including Cinnamon/Muffin and KDE/GNOME under X11): <c>XGetImage</c> with
		/// XComposite redirection, so windows hidden behind others still capture correctly.</item>
		/// <item><b>GNOME Wayland</b>: the Keysharp GNOME Shell extension images the window actor's own
		/// buffer (<c>meta_window_actor_get_image</c>) via keysharp-screencap, so occluded windows work.</item>
		/// <item><b>KWin Wayland</b>: <c>org.kde.KWin.ScreenShot2.CaptureWindow</c> via keysharp-screencap,
		/// keyed by the window's internalId UUID — occlusion-independent.</item>
		/// <item><b>wlroots / unknown</b>: no foreign-window protocol → rectangle fallback.</item>
		/// </list>
		/// </summary>
		/// <param name="handle">The window handle as stored by the active provider: an X11 window id on
		/// X11, or the compositor window id (GNOME stable-sequence) on Wayland.</param>
		/// <returns>The captured bitmap (physical pixels) and its physical-pixels-per-logical-unit scale,
		/// or (null, 1.0) when no true window capture is available for this session.</returns>
		internal static (Bitmap bmp, double scale) CaptureWindow(nint handle)
		{
			if (handle == 0)
				return (null, 1.0);

			if (!PlatformManager.IsWaylandSession)
				return (X11ScreenCapture.TryCaptureWindow(handle.ToInt64()), 1.0);

			var backend = WaylandBackend.Current;

			// GNOME: occlusion-independent — the extension images the window actor's backing buffer.
			if (backend is WaylandBackend.GnomeBackend)
				return (ScreencapHelper.CaptureGnomeWindow((ulong)handle.ToInt64()), 1.0);

			// KWin: occlusion-independent via org.kde.KWin.ScreenShot2.CaptureWindow, which re-renders the
			// window off-screen. The KWin backend already tracks each window's internalId UUID, and the
			// keysharp-screencap .desktop already whitelists the ScreenShot2 interface. Windows without a
			// real internalId (the "windowId:" fallback, e.g. some Xwayland clients) fall through to the
			// rectangle grab.
			if (backend is WaylandBackend.KWinBackend kwin && kwin.TryGetWindowUuid(handle, out var uuid))
				return (ScreencapHelper.CaptureKWinWindow(uuid), 1.0);

			// wlr-screencopy captures whole outputs, not individual windows.
			return (null, 1.0);
		}
	}
}
#endif
