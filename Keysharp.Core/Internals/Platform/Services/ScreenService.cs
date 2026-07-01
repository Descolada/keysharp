#if LINUX
using Wl = Keysharp.Internals.Window.Linux.Wayland;
using X11Cap = Keysharp.Internals.Window.Linux.X11.X11ScreenCapture;
#endif

namespace Keysharp.Internals
{
#if LINUX
	/// <summary>
	/// Resolves the one Linux <see cref="IScreen"/> for this session — the only place the compositor flavor is
	/// inspected. After this, capture/work-area/authorization dispatch is plain virtual calls: the right
	/// implementation was already chosen, so there are no <c>IsWaylandSession</c> / <c>is …Backend</c> checks on
	/// the hot path.
	/// </summary>
	internal static class LinuxScreens
	{
		internal static IScreen Resolve()
		{
			if (!IsWaylandSession)
				return new X11Screen();

			return Wl.WaylandBackend.Current switch
			{
				Wl.WaylandBackend.KWinBackend kwin => new KWinScreen(kwin),
				Wl.WaylandBackend.GnomeBackend gnome => new GnomeScreen(gnome),
				Wl.CinnamonBackend cinnamon => new CinnamonScreen(cinnamon),
				null => new X11Screen(),                      // Wayland session but no usable backend → X11/Eto
				var other => new WlrootsScreen(other),        // sway/Hyprland/wlroots: tries zwlr, else Eto
			};
		}
	}

	/// <summary>Base Linux screen: Eto root-window grab for regions, no true window capture (caller
	/// rectangle-grabs), no work-area override (caller uses Eto's per-screen WorkingArea), no authorization.</summary>
	internal class EtoScreen : IScreen
	{
		public virtual bool TryGetWorkArea(int monitorIndex, out Rectangle area, out bool excludesPanels)
		{
			area = Rectangle.Empty;
			excludesPanels = false;
			return false;
		}

		public bool TryGetPrimaryBounds(out Rectangle bounds) { bounds = Rectangle.Empty; return false; }

		public virtual bool TryCaptureRegion(int x, int y, int width, int height, out Bitmap bmp, out double scaleX, out double scaleY)
		{
			scaleX = scaleY = 1;
			// On a HiDPI display Eto's fork sizes the grab in physical pixels; callers map back via the scale.
			bmp = Eto.Forms.Screen.PrimaryScreen.GetImage(new RectangleF(x, y, width, height)) as Bitmap;
			return bmp != null;
		}

		public virtual bool TryCaptureWindow(nint h, bool includeDecoration, out Bitmap bmp, out double scale)
		{
			bmp = null;
			scale = 1;
			return false;   // no occlusion-independent window capture here → caller falls back to a rectangle grab
		}

		public virtual bool RequiresAuthorization => false;

		public virtual Os.PermissionResult RequestCaptureAuthorization(string operation, bool prompt)
			=> new (Os.PermissionStatus.NotApplicable);
	}

	/// <summary>X11 (including KDE/GNOME/Cinnamon under X11): region grabs use Eto; window capture uses
	/// <c>XGetImage</c> + XComposite redirection, so occluded windows still capture.</summary>
	internal sealed class X11Screen : EtoScreen
	{
		public override bool TryCaptureWindow(nint h, bool includeDecoration, out Bitmap bmp, out double scale)
		{
			// X11 images the client window, so decorations are never included (the flag is moot here).
			scale = 1;
			bmp = X11Cap.TryCaptureWindow(h.ToInt64());
			return bmp != null;
		}
	}

	/// <summary>Shared Wayland-compositor base: work area comes from the compositor (a client can't compute it),
	/// and region capture falls back to the Eto grab when the compositor path returns nothing.</summary>
	internal abstract class WaylandScreen : EtoScreen
	{
		protected readonly Wl.IWaylandBackend backend;

		protected WaylandScreen(Wl.IWaylandBackend backend) => this.backend = backend;

		public override bool TryGetWorkArea(int monitorIndex, out Rectangle area, out bool excludesPanels)
		{
			if (backend != null && backend.TryGetWorkArea(out var wa) && wa.Width > 0 && wa.Height > 0)
			{
				area = wa;
				excludesPanels = true;   // the compositor work area already excludes panels/struts
				return true;
			}

			area = Rectangle.Empty;
			excludesPanels = false;
			return false;
		}
	}

	/// <summary>KWin Wayland via the keysharp-screencap helper: region grabs through the ScreenShot2 interface,
	/// window grabs keyed by the window's internalId UUID (occlusion-independent).</summary>
	internal sealed class KWinScreen : WaylandScreen
	{
		private readonly Wl.WaylandBackend.KWinBackend kwin;

		internal KWinScreen(Wl.WaylandBackend.KWinBackend backend) : base(backend) => kwin = backend;

		public override bool TryCaptureRegion(int x, int y, int width, int height, out Bitmap bmp, out double scaleX, out double scaleY)
		{
			bmp = Wl.ScreencapHelper.Capture(x, y, width, height);

			if (bmp != null) { scaleX = scaleY = 1; return true; }

			return base.TryCaptureRegion(x, y, width, height, out bmp, out scaleX, out scaleY);
		}

		public override bool TryCaptureWindow(nint h, bool includeDecoration, out Bitmap bmp, out double scale)
		{
			scale = 1;

			// org.kde.KWin.ScreenShot2.CaptureWindow re-renders off-screen → occlusion-independent. Windows
			// without a real internalId (the "windowId:" fallback) miss here → caller rectangle-grabs.
			if (kwin.TryGetWindowUuid(h, out var uuid))
			{
				bmp = Wl.ScreencapHelper.CaptureKWinWindow(uuid, includeDecoration);
				return bmp != null;
			}

			bmp = null;
			return false;
		}

		public override bool RequiresAuthorization => true;

		public override Os.PermissionResult RequestCaptureAuthorization(string operation, bool prompt)
			=> Wl.ScreencapHelper.Authorize(operation, prompt);
	}

	/// <summary>GNOME Wayland via the keysharp-screencap helper + Shell extension: region grabs through the
	/// extension, window grabs image the window actor's own buffer (occlusion-independent; includes decoration).</summary>
	internal sealed class GnomeScreen : WaylandScreen
	{
		private readonly Wl.WaylandBackend.GnomeBackend gnome;

		internal GnomeScreen(Wl.WaylandBackend.GnomeBackend backend) : base(backend) => gnome = backend;

		public override bool TryCaptureRegion(int x, int y, int width, int height, out Bitmap bmp, out double scaleX, out double scaleY)
		{
			bmp = Wl.ScreencapHelper.CaptureGnome(x, y, width, height);

			if (bmp != null) { scaleX = scaleY = 1; return true; }

			return base.TryCaptureRegion(x, y, width, height, out bmp, out scaleX, out scaleY);
		}

		public override bool TryCaptureWindow(nint h, bool includeDecoration, out Bitmap bmp, out double scale)
		{
			scale = 1;

			// The extension matches by raw stable_sequence, so strip the marker first (TryGetWindowSeq does that);
			// the actor image already includes decorations, so includeDecoration is ignored.
			if (gnome.TryGetWindowSeq(h, out var seq))
			{
				bmp = Wl.ScreencapHelper.CaptureGnomeWindow(seq);
				return bmp != null;
			}

			bmp = null;
			return false;
		}

		public override bool RequiresAuthorization => true;

		public override Os.PermissionResult RequestCaptureAuthorization(string operation, bool prompt)
			=> Wl.ScreencapHelper.AuthorizeGnome(operation, prompt);
	}

	/// <summary>Cinnamon Wayland via Muffin's D-Bus screenshot method. Region grabs are compositor-mediated.
	/// Window capture images the compositor-reported on-screen frame rectangle, so unlike KWin/GNOME it is
	/// occlusion-dependent, but it is still routed through the Cinnamon screenshot backend.</summary>
	internal sealed class CinnamonScreen : WaylandScreen
	{
		internal CinnamonScreen(Wl.CinnamonBackend backend) : base(backend) { }

		public override bool TryCaptureRegion(int x, int y, int width, int height, out Bitmap bmp, out double scaleX, out double scaleY)
		{
			bmp = Wl.CinnamonShellBridge.CaptureArea(x, y, width, height);

			if (bmp != null) { scaleX = scaleY = 1; return true; }

			return base.TryCaptureRegion(x, y, width, height, out bmp, out scaleX, out scaleY);
		}

		public override bool TryCaptureWindow(nint h, bool includeDecoration, out Bitmap bmp, out double scale)
		{
			bmp = null;
			scale = 1;

			// Cinnamon reports client == frame, so it can't honor a client-only grab; both paths capture the
			// window's on-screen rectangle (GetBounds and GetClientBounds return the same FrameGeometry here).
			var bounds = includeDecoration ? Platform.Window.GetBounds(h) : Platform.Window.GetClientBounds(h);

			if (bounds.Width <= 0 || bounds.Height <= 0)
				return false;

			bmp = Wl.CinnamonShellBridge.CaptureArea(bounds.X, bounds.Y, bounds.Width, bounds.Height);

			if (bmp == null)
				return false;

			// CaptureArea returns DEVICE pixels: on a HiDPI monitor the bitmap is larger than the logical bounds.
			// Report the real capture scale (mirroring the macOS/Windows branches) so a consumer such as OCR can
			// divide it back out; otherwise word coordinates drift on scaled monitors. Use the larger of the two
			// axis ratios to stay safe under fractional scaling where X/Y round differently.
			var sx = bounds.Width > 0 ? (double)bmp.Width / bounds.Width : 1.0;
			var sy = bounds.Height > 0 ? (double)bmp.Height / bounds.Height : 1.0;
			scale = Math.Max(sx, sy);
			return true;
		}

		public override bool RequiresAuthorization => false;

		public override Os.PermissionResult RequestCaptureAuthorization(string operation, bool prompt)
			=> new (Os.PermissionStatus.NotApplicable);
	}

	/// <summary>wlroots compositors (sway/Hyprland/Wayfire/…): region grabs via zwlr_screencopy; no foreign
	/// per-window protocol, so window capture falls back to a rectangle grab.</summary>
	internal sealed class WlrootsScreen : WaylandScreen
	{
		internal WlrootsScreen(Wl.IWaylandBackend backend) : base(backend) { }

		public override bool TryCaptureRegion(int x, int y, int width, int height, out Bitmap bmp, out double scaleX, out double scaleY)
		{
			bmp = Wl.WaylandScreenCapture.TryCapture(x, y, width, height);

			if (bmp != null) { scaleX = scaleY = 1; return true; }

			return base.TryCaptureRegion(x, y, width, height, out bmp, out scaleX, out scaleY);
		}
	}
#elif WINDOWS
	internal sealed class WindowsScreen : IScreen
	{
		public bool TryGetWorkArea(int monitorIndex, out Rectangle area, out bool excludesPanels) { area = Rectangle.Empty; excludesPanels = false; return false; }
		public bool TryGetPrimaryBounds(out Rectangle bounds) { bounds = Rectangle.Empty; return false; }
		public bool TryCaptureRegion(int x, int y, int width, int height, out Bitmap bmp, out double scaleX, out double scaleY) { bmp = null; scaleX = scaleY = 1; return false; }
		public bool TryCaptureWindow(nint h, bool includeDecoration, out Bitmap bmp, out double scale) { bmp = null; scale = 1; return false; }
		public bool RequiresAuthorization => false;
		public Os.PermissionResult RequestCaptureAuthorization(string operation, bool prompt) => new (Os.PermissionStatus.NotApplicable);
	}
#elif OSX
	internal sealed class MacScreen : IScreen
	{
		public bool TryGetWorkArea(int monitorIndex, out Rectangle area, out bool excludesPanels) { area = Rectangle.Empty; excludesPanels = false; return false; }
		public bool TryGetPrimaryBounds(out Rectangle bounds) { bounds = Rectangle.Empty; return false; }
		public bool TryCaptureRegion(int x, int y, int width, int height, out Bitmap bmp, out double scaleX, out double scaleY) { bmp = null; scaleX = scaleY = 1; return false; }
		public bool TryCaptureWindow(nint h, bool includeDecoration, out Bitmap bmp, out double scale) { bmp = null; scale = 1; return false; }
		public bool RequiresAuthorization => true;   // macOS Screen Recording permission (handled in MacPermissionManager)
		public Os.PermissionResult RequestCaptureAuthorization(string operation, bool prompt) => new (Os.PermissionStatus.NotApplicable);
	}
#endif
}
