namespace Keysharp.Internals
{
#if OSX
	internal sealed class MacOverlay : OverlayBase
	{
		public override PixelSize GetCanvasSize(ScreenRect bounds) => OverlayCanvasSizing.FromEtoScreen(bounds);
		protected override IImageOverlayBacking CreateBacking(uint id) => new EtoImageOverlay();
	}
#endif

#if LINUX || OSX
	/// <summary>Render-target sizing for the Eto fallback. This stays with the overlay service rather than display
	/// topology because LogicalPixelSize describes the target's backing canvas, not screen coordinates.</summary>
	internal static class OverlayCanvasSizing
	{
		internal static PixelSize FromEtoScreen(ScreenRect bounds)
		{
			var screen = Forms.Screen.FromRectangle(new RectangleF(bounds.X, bounds.Y,
				Math.Max(1, bounds.Width), Math.Max(1, bounds.Height))) ?? Forms.Screen.PrimaryScreen;
			return FromScale(bounds, ScaleFactor.Normalize(screen?.LogicalPixelSize ?? 1f));
		}

		internal static PixelSize FromScale(ScreenRect bounds, double scale)
			=> new(ToPixels(bounds.Width, scale), ToPixels(bounds.Height, scale));

		private static int ToPixels(int length, double scale)
		{
			var value = Math.Round(Math.Max(1, length) * ScaleFactor.Normalize(scale));
			return value >= int.MaxValue ? int.MaxValue : Math.Max(1, (int)value);
		}
	}
#endif

#if LINUX || OSX
	// Shared Eto (GTK/Cocoa) click-through overlay window -- the toolkit fallback on Linux and the only backing on
	// macOS. It borrows `image`: Show snapshots it on the calling thread for isolation and
	// keeps only that private `displayed` bitmap, which a same-size move just repositions.
	internal sealed class EtoImageOverlay : IImageOverlayBacking
	{
		private Keysharp.Builtins.KeysharpForm form;
#if LINUX
		// Linux draws the bitmap through a Drawable that blits it 1:1 (natural size, top-left aligned), NOT Eto's
		// ImageView. ImageView's GTK DrawingArea rescales its image to the widget's CURRENT allocation on every paint
		// (Math.Min(scaleW, scaleH), centred), and a top-level GTK window only adopts a new allocation after the WM's
		// asynchronous ConfigureNotify round-trip. So while an overlay resizes live (a zoom drag, a shrinking tooltip
		// or highlight) a paint lands at a STALE allocation and the new bitmap is scaled up (huge/blurry), down
		// (flicker), to zero (vanishes) or off-aspect (shifted). A 1:1 blit removes the scaling: a lagging allocation
		// can at most clip or leave a transparent margin on the far edge for the single frame before it catches up.
		// PaintOwned already sizes the bitmap to the exact window size, so the blit is pixel-exact in steady state.
		private Eto.Forms.Drawable imageSurface;
		// The window manager applies GTK allocations asynchronously. Keep the requested paint extent separately so
		// an intermediate paint clips/leaves a transparent edge instead of stretching the new frame to an old size.
		private int paintW, paintH;
#else
		private ImageView imageView;
#endif
		private Bitmap displayed;
		private int shownW, shownH;
#if OSX
		private double shownBackingScale = 1;
#endif

		public nint Handle => form?.Handle ?? 0;

		public bool Show(Bitmap image, ScreenRect bounds, bool clickThrough)
		{
			Bitmap snapshot = null;
			var adopted = false;

			try
			{
				// Copy `image` on THIS (calling) thread -- the borrowed canvas may be redrawn on this thread, so
				// snapshotting here (not inside the UI-thread callback) isolates the pixels the UI will show.
				snapshot = new Bitmap(image);
				var snap = snapshot;

				Script.InvokeOnUIThread(() =>
				{
					EnsureForm();
					form.CanFocus = !clickThrough;
					var windowBounds = ToToolkitBounds(bounds);
					// From here PaintOwned owns `snap` (it keeps it as `displayed`, or disposes it after resizing), so
					// mark ownership transferred BEFORE the call: a throw past this point must not ALSO dispose it on
					// the catch path (that would double-free the bitmap the form now holds).
					adopted = true;
					PaintOwned(snap, bounds, windowBounds);
					// One atomic geometry set: Eto's Window.Bounds resolves to a single gdk_window_move_resize on
					// GTK (and setFrame on macOS), so a live resize+move (an InputHUD zoom frame) is one request and
					// never flashes an intermediate new-size/old-position frame the way separate Size+Location did.
					form.Bounds = new Rectangle(windowBounds.X, windowBounds.Y,
						Math.Max(1, windowBounds.Width), Math.Max(1, windowBounds.Height));

					if (!form.Visible)
						form.Show();

					form.SetClickThrough(clickThrough);
				});

				return true;   // borrow: `image` is neither retained nor disposed
			}
			catch
			{
				// The UI-thread invoke threw before PaintOwned took ownership of the snapshot -- dispose it here so it
				// does not leak. If ownership had transferred, `displayed` owns it now and TryHide will free it.
				if (!adopted)
					snapshot?.Dispose();

				return false;
			}
		}

		public bool Move(ScreenRect bounds)
		{
			// Same-size: reposition (the ImageView keeps its bitmap). Resize: re-render via Show.
			if (form == null || bounds.Width != shownW || bounds.Height != shownH)
				return false;

			var moved = false;
			Script.InvokeOnUIThread(() =>
			{
				if (form != null)
				{
					var windowBounds = ToToolkitBounds(bounds);
#if OSX
					var screen = Forms.Screen.FromRectangle(new RectangleF(bounds.X, bounds.Y,
						Math.Max(1, bounds.Width), Math.Max(1, bounds.Height))) ?? Forms.Screen.PrimaryScreen;

					if (Math.Abs(ScaleFactor.Normalize(screen?.LogicalPixelSize ?? 1f) - shownBackingScale) > 0.0001)
						return;
#endif
					form.Location = new Point(windowBounds.X, windowBounds.Y);
					moved = true;
				}
			});

			return moved;
		}

		// Adopts `snapshot` (an owned, private copy) as the displayed bitmap, resizing it if needed. UI thread.
		// x/y are the overlay's on-screen position and width/height its on-screen size, in the toolkit's window
		// coordinate units (physical px on GTK, logical points on Cocoa). x/y are used only on macOS to pick the
		// screen the overlay actually sits on (for the right backing scale); GTK ignores them here.
		private void PaintOwned(Bitmap snapshot, ScreenRect bounds, ScreenRect windowBounds)
		{
			var size = new Size(Math.Max(1, windowBounds.Width), Math.Max(1, windowBounds.Height));
			var old = displayed;

#if OSX
			// Cocoa sizes windows in LOGICAL points but renders into a HiDPI backing store, so the visible surface
			// is `size * backingScale` device pixels. Match the bitmap to that device resolution: a hi-res card
			// bitmap (already at device size) is kept as-is for a crisp 1:1 result, while a small "stretch tile"
			// (SelFill / guide / border bars) is scaled up to fill the view exactly. Leaving a bitmap whose aspect
			// differs from the view would let NSImageView's ProportionallyUpOrDown scaling shrink-to-fit and CENTRE
			// it -- which is what made a drag selection look smaller than the real area with its corner offset.
			// macOS-unverified: use the scale of the screen the overlay is actually PLACED on (derived from x/y) rather
			// than always the primary -- a secondary monitor with a different backingScaleFactor would otherwise render
			// at the wrong resolution. Screen.FromRectangle is the Eto API for this; fall back to the primary screen.
			var screen = Forms.Screen.FromRectangle(new RectangleF(bounds.X, bounds.Y, size.Width, size.Height)) ?? Forms.Screen.PrimaryScreen;
			var backing = ScaleFactor.Normalize(screen?.LogicalPixelSize ?? 1f);
			shownBackingScale = backing;
			var devW = Math.Max(1, (int)Math.Round(size.Width * backing));
			var devH = Math.Max(1, (int)Math.Round(size.Height * backing));

			// Take ownership of the snapshot BEFORE the throwable resize: if ResizeBitmap fails (OOM/GDI), `displayed`
			// still owns a live bitmap that TryHide frees, rather than the snapshot leaking (the caller already set
			// adopted=true, so Show's catch will NOT dispose it).
			displayed = snapshot;

			if (snapshot.Width != devW || snapshot.Height != devH)
			{
				var resized = ImageHelper.ResizeBitmap(snapshot, devW, devH, exactPixels: true);
				displayed = resized;
				snapshot.Dispose();
			}
#else
			// GTK/Cairo owns the mapping from widget units to its backing surface. Keep the renderer-selected raster
			// intact and draw it into the widget's native rectangle in the Paint handler; resizing it here would throw
			// away HiDPI pixels on Wayland and would incorrectly apply GTK's scale to X11 root-pixel coordinates.
			displayed = snapshot;
#endif

#if LINUX
			// The outer window's geometry (position AND size) is applied atomically by the caller via form.Bounds so
			// a live resize does not jitter; here we size only the inner Drawable. The Drawable holds no image of its
			// own -- it reads `displayed` in its Paint handler. Resize it to the window, then Invalidate. This
			// immediate Invalidate is also what repaints a SAME-SIZE content change (a Highlight recolour, an opacity
			// re-push, a same-size tooltip text swap) -- cases where SizeChanged in EnsureForm never fires -- so it is
			// NOT redundant with that handler; the two cover different cases.
			paintW = size.Width;
			paintH = size.Height;
			imageSurface.Size = size;
			imageSurface.Invalidate();
#else
			imageView.Image = displayed;
			imageView.Size = size;
#endif
			// Free the previous frame only now that the view/Drawable references the NEW bitmap: on macOS imageView
			// still points at `old` until the assignment above, so disposing earlier briefly freed the live image.
			old?.Dispose();
			shownW = bounds.Width;
			shownH = bounds.Height;
		}

		private static ScreenRect ToToolkitBounds(ScreenRect bounds)
		{
#if LINUX
			if (!IsWaylandSession)
				return Keysharp.Internals.Window.Linux.X11.X11DisplayTopology.ToToolkitBounds(bounds);
#endif
			return bounds;
		}

		private void EnsureForm()
		{
			if (form != null)
				return;

			form = new Keysharp.Builtins.KeysharpForm
			{
				FormBorderStyle = Keysharp.Builtins.FormBorderStyle.None,
				ShowInTaskbar = false,
				ShowActivated = false,
				CanFocus = false,
				TopMost = true,
				// Must be resizable so PaintOwned can SHRINK the window, not just grow it: GTK3 forces AutoSize when
				// !Resizable and ignores gtk_window_resize() on a non-resizable window, so a smaller PaintOwned size
				// was a no-op and the previous (larger) window stretched the new smaller bitmap -- an overlay that
				// shrank in place (zoom-out, a shorter tooltip after a longer one, a shrinking highlight) blurred at
				// its old size. The overlay is click-through + borderless + non-taskbar, so this never lets the user
				// resize it; it only lets us set the exact size both ways. PaintOwned always sets form.Size explicitly.
				Resizable = true,
				BackgroundColor = Colors.Transparent
			};
#if LINUX
			imageSurface = new Eto.Forms.Drawable { BackgroundColor = Colors.Transparent };
			// Make the underlying GTK EventBox windowless so the transparent, click-through form shows through the
			// drawable instead of it painting its own opaque window (same recipe as KeysharpLinkLabel).
			try
			{
				if (imageSurface.ToNative() is Gtk.EventBox eventBox)
					eventBox.VisibleWindow = false;
			}
			catch { }
			imageSurface.Paint += (s, e) =>
			{
				// Clear to transparent first so a lagging-large allocation leaves no ghost of the previous frame in
				// the margin, then blit the bitmap 1:1 at the top-left (which the window's Location already tracks).
				e.Graphics.Clear();
				var d = displayed;

				if (d != null)
					e.Graphics.DrawImage(d, 0, 0, Math.Max(1, paintW), Math.Max(1, paintH));
			};
			// Repaint whenever the widget's allocation actually changes. A GTK window adopts its new size only after
			// the WM's async ConfigureNotify, so a frame painted mid-resize is clipped to a stale allocation; without
			// this, a resize whose final allocation lands after the last Invalidate stays cropped until the next size
			// change. Re-blitting on each real allocation guarantees the settled frame shows the whole bitmap.
			imageSurface.SizeChanged += (s, e) => imageSurface?.Invalidate();
			form.Content = imageSurface;
#else
			imageView = new ImageView { BackgroundColor = Colors.Transparent };
			form.Content = imageView;
#endif
			form.SetClickThrough(true);
			// Take the overlay out of WM control (override-redirect): it is placed at exact pixels (no reposition
			// when a live HUD resizes every frame) AND kept in the topmost layer, above even +AlwaysOnTop windows,
			// so a highlight over an always-on-top window is visible. Set before the form is mapped.
#if LINUX
			Eto.Forms.EtoExtensions.SetFormOverlayTopmost(form);
#endif
		}

		public bool TryHide()
		{
			var closed = true;

			// InvokeOnUIThread is synchronous, so `closed`/`form` reflect the outcome once it returns.
			Script.InvokeOnUIThread(() =>
			{
				try
				{
					form?.Close();
					form?.Dispose();
					form = null;   // only reached when Close/Dispose didn't throw
#if LINUX
					imageSurface = null;
					paintW = paintH = 0;
#else
					imageView = null;
#endif
					displayed?.Dispose();
					displayed = null;
				}
				catch { closed = false; }   // leave `form` set so a later retry can re-close it
			});

			return closed && form == null;
		}

		public void Dispose() => _ = TryHide();
	}
#endif
}
