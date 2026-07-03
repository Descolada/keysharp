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
		// X11 lets any client grab the screen without asking, so routing capture through keysharp-helper is
		// consent/awareness only — a determined script could call XGetImage directly and bypass it. It still
		// lets the user see and refuse Keysharp-driven capture, and unifies the prompt with the Wayland paths.
		// The consent is resolved once (cached) and degrades to "allow" when no privileged helper is installed.
		public override bool TryCaptureRegion(int x, int y, int width, int height, out Bitmap bmp, out double scaleX, out double scaleY)
		{
			if (!Wl.HelperClient.EnsureCaptureConsent())
			{
				bmp = null;
				scaleX = scaleY = 1;
				return false;
			}

			return base.TryCaptureRegion(x, y, width, height, out bmp, out scaleX, out scaleY);
		}

		public override bool TryCaptureWindow(nint h, bool includeDecoration, out Bitmap bmp, out double scale)
		{
			scale = 1;

			if (!Wl.HelperClient.EnsureCaptureConsent())
			{
				bmp = null;
				return false;
			}

			// X11 images the client window, so decorations are never included (the flag is moot here).
			bmp = X11Cap.TryCaptureWindow(h.ToInt64());
			return bmp != null;
		}

		public override bool RequiresAuthorization => true;

		// prompt == true is an explicit request (may prompt); prompt == false is a status query
		// (RequestCapabilities with no request) which must never prompt — peek the cached decision instead.
		public override Os.PermissionResult RequestCaptureAuthorization(string operation, bool prompt)
			=> prompt ? Wl.HelperClient.AuthorizeCapture(true) : Wl.HelperClient.PeekCaptureConsent();
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

	/// <summary>KWin Wayland via the keysharp-helper: region grabs through the ScreenShot2 interface,
	/// window grabs keyed by the window's internalId UUID (occlusion-independent).</summary>
	internal sealed class KWinScreen : WaylandScreen
	{
		private readonly Wl.WaylandBackend.KWinBackend kwin;

		internal KWinScreen(Wl.WaylandBackend.KWinBackend backend) : base(backend) => kwin = backend;

		public override bool TryCaptureRegion(int x, int y, int width, int height, out Bitmap bmp, out double scaleX, out double scaleY)
		{
			bmp = Wl.HelperClient.Capture(x, y, width, height);

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
				bmp = Wl.HelperClient.CaptureKWinWindow(uuid, includeDecoration);
				return bmp != null;
			}

			bmp = null;
			return false;
		}

		public override bool RequiresAuthorization => true;

		public override Os.PermissionResult RequestCaptureAuthorization(string operation, bool prompt)
			=> Wl.HelperClient.Authorize(operation, prompt);
	}

	/// <summary>GNOME Wayland via the keysharp-helper + Shell extension: region grabs through the
	/// extension, window grabs image the window actor's own buffer (occlusion-independent; includes decoration).</summary>
	internal sealed class GnomeScreen : WaylandScreen
	{
		private readonly Wl.WaylandBackend.GnomeBackend gnome;

		internal GnomeScreen(Wl.WaylandBackend.GnomeBackend backend) : base(backend) => gnome = backend;

		public override bool TryCaptureRegion(int x, int y, int width, int height, out Bitmap bmp, out double scaleX, out double scaleY)
		{
			bmp = Wl.HelperClient.CaptureGnome(x, y, width, height);

			if (bmp != null) { scaleX = scaleY = 1; return true; }

			return base.TryCaptureRegion(x, y, width, height, out bmp, out scaleX, out scaleY);
		}

		public override bool TryCaptureWindow(nint h, bool includeDecoration, out Bitmap bmp, out double scale)
		{
			scale = 1;

			// The extension matches by raw stable_sequence, so strip the marker first (TryGetWindowSeq does that);
			// the actor image is clipped to the frame rect (includes decorations), so includeDecoration is ignored.
			if (gnome.TryGetWindowSeq(h, out var seq))
			{
				bmp = Wl.HelperClient.CaptureGnomeWindow(seq);

				if (bmp == null)
					return false;

				// The actor buffer is DEVICE pixels while the frame bounds are logical, and the extension
				// clips the image to the frame — so the size ratio IS the buffer scale (mirroring the
				// Cinnamon/macOS/Windows branches). Max of the two axes guards fractional-scaling rounding.
				var bounds = Platform.Window.GetBounds(h);

				if (bounds.Width > 0 && bounds.Height > 0)
					scale = Math.Max((double)bmp.Width / bounds.Width, (double)bmp.Height / bounds.Height);

				return true;
			}

			bmp = null;
			return false;
		}

		public override bool RequiresAuthorization => true;

		public override Os.PermissionResult RequestCaptureAuthorization(string operation, bool prompt)
			=> Wl.HelperClient.AuthorizeGnome(operation, prompt);
	}

	/// <summary>Cinnamon Wayland via the keysharp-helper + Shell extension: region grabs go through
	/// keysharp-helper --serve cinnamon (which enforces the user's capture consent) to the extension, which
	/// captures via Cinnamon.Screenshot. Window capture images the window actor's own buffer through the
	/// extension's CaptureWindow (occlusion-independent, like KWin/GNOME), falling back to a rectangle grab
	/// of the on-screen frame when the extension can't capture (older extension, minimized window).</summary>
	internal sealed class CinnamonScreen : WaylandScreen
	{
		private readonly Wl.CinnamonBackend cinnamon;

		internal CinnamonScreen(Wl.CinnamonBackend backend) : base(backend) => cinnamon = backend;

		public override bool TryCaptureRegion(int x, int y, int width, int height, out Bitmap bmp, out double scaleX, out double scaleY)
		{
			bmp = Wl.HelperClient.CaptureCinnamon(x, y, width, height);

			if (bmp != null) { scaleX = scaleY = 1; return true; }

			return base.TryCaptureRegion(x, y, width, height, out bmp, out scaleX, out scaleY);
		}

		public override bool TryCaptureWindow(nint h, bool includeDecoration, out Bitmap bmp, out double scale)
		{
			bmp = null;
			scale = 1;

			// Preferred: the extension images the window actor's own buffer, clipped to the frame rect —
			// occluded windows capture correctly, and the size ratio to the logical frame IS the device
			// scale. includeDecoration is moot (Cinnamon reports client == frame).
			var bounds = Platform.Window.GetBounds(h);

			if (cinnamon.TryGetWindowSeq(h, out var seq)
					&& Wl.HelperClient.CaptureCinnamonWindow(seq) is Bitmap actorBmp)
			{
				bmp = actorBmp;

				if (bounds.Width > 0 && bounds.Height > 0)
					scale = Math.Max((double)bmp.Width / bounds.Width, (double)bmp.Height / bounds.Height);

				return true;
			}

			// Fallback (older installed extension, minimized window): rectangle-grab the on-screen frame —
			// occlusion-dependent, but still routed through the consent-enforcing helper.
			if (bounds.Width <= 0 || bounds.Height <= 0)
				return false;

			bmp = Wl.HelperClient.CaptureCinnamon(bounds.X, bounds.Y, bounds.Width, bounds.Height);

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

		public override bool RequiresAuthorization => true;

		public override Os.PermissionResult RequestCaptureAuthorization(string operation, bool prompt)
			=> Wl.HelperClient.AuthorizeCinnamon(operation, prompt);
	}

	/// <summary>wlroots compositors (sway/Hyprland/Wayfire/…): region grabs via zwlr_screencopy; no foreign
	/// per-window protocol, so window capture falls back to a rectangle grab.</summary>
	internal sealed class WlrootsScreen : WaylandScreen
	{
		internal WlrootsScreen(Wl.IWaylandBackend backend) : base(backend) { }

		// wlroots screencopy is a direct client protocol (no consent), so — like X11 — gating through
		// keysharp-helper is consent/awareness only. Window capture inherits the rectangle-grab fallback,
		// which routes back through this (gated) region path, so no separate window gate is needed.
		public override bool TryCaptureRegion(int x, int y, int width, int height, out Bitmap bmp, out double scaleX, out double scaleY)
		{
			if (!Wl.HelperClient.EnsureCaptureConsent())
			{
				bmp = null;
				scaleX = scaleY = 1;
				return false;
			}

			bmp = Wl.WaylandScreenCapture.TryCapture(x, y, width, height);

			if (bmp != null) { scaleX = scaleY = 1; return true; }

			return base.TryCaptureRegion(x, y, width, height, out bmp, out scaleX, out scaleY);
		}

		public override bool RequiresAuthorization => true;

		// prompt == true is an explicit request (may prompt); prompt == false is a status query
		// (RequestCapabilities with no request) which must never prompt — peek the cached decision instead.
		public override Os.PermissionResult RequestCaptureAuthorization(string operation, bool prompt)
			=> prompt ? Wl.HelperClient.AuthorizeCapture(true) : Wl.HelperClient.PeekCaptureConsent();
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
