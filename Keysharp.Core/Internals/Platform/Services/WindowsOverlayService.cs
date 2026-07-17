#if WINDOWS
namespace Keysharp.Internals
{
	internal sealed class WindowsOverlay : OverlayBase
	{
		public override PixelSize GetCanvasSize(ScreenRect bounds)
			=> new(Math.Max(1, bounds.Width), Math.Max(1, bounds.Height));
		protected override IImageOverlayBacking CreateBacking(uint id) => new WindowsImageOverlay();
	}

	// A per-pixel-alpha layered top-level window (UpdateLayeredWindow) that is click-through and never activates.
	internal sealed class WindowsImageOverlay : IImageOverlayBacking
	{
		private LayeredOverlayForm form;
		private int shownW, shownH;

		public nint Handle => form?.IsHandleCreated == true ? form.Handle : 0;

		public bool Show(Bitmap image, ScreenRect bounds, bool clickThrough)
		{
			try
			{
				var width = Math.Max(1, bounds.Width <= 0 ? image.Width : bounds.Width);
				var height = Math.Max(1, bounds.Height <= 0 ? image.Height : bounds.Height);
				var updated = false;

				// Snapshot and premultiply on the calling thread so the UI never retains the caller's live canvas.
				using (var display = CreateDisplayBitmap(image, width, height))
				{
					var d = display;
					Script.InvokeOnUIThread(() =>
					{
						EnsureForm();
						// Apply the input mode before showing so the exstyle is right from the first CreateParams
						// evaluation (a live toggle later goes through SetWindowLong instead).
						form.SetClickThrough(clickThrough);
						updated = form.ShowImage(d, bounds.X, bounds.Y, width, height);
					});
				}

				if (!updated)
					return false;

				shownW = width;
				shownH = height;
				return true;
			}
			catch
			{
				return false;
			}
		}

		public bool Move(ScreenRect bounds)
		{
			if (form == null)
				return false;

			// A layered window retains its last UpdateLayeredWindow content across a move, so a same-size move
			// is a pure reposition (no pixels needed; matters for mouse-following highlights). A resize needs
			// new pixels, so return false and let the overlay re-render via Show.
			if (bounds.Width == shownW && bounds.Height == shownH)
			{
				var moved = false;
				Script.InvokeOnUIThread(() =>
					moved = WindowsAPI.SetWindowPos(form.Handle, new nint(WindowsAPI.HWND_TOPMOST), bounds.X, bounds.Y, 0, 0,
											WindowsAPI.SWP_NOACTIVATE | WindowsAPI.SWP_NOSIZE));
				return moved;
			}

			return false;
		}

		private void EnsureForm() => form ??= new LayeredOverlayForm();

		private static Bitmap CreateDisplayBitmap(Bitmap source, int width, int height)
		{
			var display = new Bitmap(width, height, PixelFormat.Format32bppPArgb);

			using (var g = Graphics.FromImage(display))
			{
				g.CompositingMode = CompositingMode.SourceCopy;
				// A same-size frame -- the common live-refresh case -- needs only the ARGB->PArgb premultiplication,
				// not interpolation, so skip the (costly) HighQualityBicubic resample and copy the pixels 1:1;
				// only a genuine resize interpolates.
				g.InterpolationMode = (width == source.Width && height == source.Height)
									  ? InterpolationMode.NearestNeighbor
									  : InterpolationMode.HighQualityBicubic;
				g.PixelOffsetMode = PixelOffsetMode.HighQuality;
				g.Clear(Color.Transparent);
				g.DrawImage(source, new Rectangle(0, 0, width, height), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel);
			}

			return display;
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
				}
				catch { closed = false; }   // leave `form` set so a later retry can re-close it
			});

			return closed && form == null;
		}

		public void Dispose() => _ = TryHide();
	}

	internal sealed class LayeredOverlayForm : Form
	{
		// Default true: passive HUDs/highlights pass mouse input through. Set false to make the layered window
		// interactive (it then receives clicks). Drives BOTH the WS_EX_TRANSPARENT exstyle and the WM_NCHITTEST
		// handler below, and can be toggled at runtime via SetClickThrough on an already-shown overlay.
		private bool clickThrough = true;

		internal LayeredOverlayForm()
		{
			FormBorderStyle = FormBorderStyle.None;
			ShowInTaskbar = false;
			StartPosition = FormStartPosition.Manual;
			TopMost = true;
		}

		protected override bool ShowWithoutActivation => true;

		protected override CreateParams CreateParams
		{
			get
			{
				var cp = base.CreateParams;
				cp.Style |= unchecked((int)WindowsAPI.WS_POPUP);
				cp.Style &= ~(WindowsAPI.WS_CAPTION | WindowsAPI.WS_THICKFRAME | WindowsAPI.WS_SYSMENU);
				cp.ExStyle |= WindowsAPI.WS_EX_LAYERED
							  | WindowsAPI.WS_EX_TOPMOST
							  | WindowsAPI.WS_EX_TOOLWINDOW
							  | WindowsAPI.WS_EX_NOACTIVATE;

				// Only add WS_EX_TRANSPARENT for a click-through overlay; an interactive one must be able to receive
				// the mouse. WS_EX_LAYERED stays either way (it is what makes UpdateLayeredWindow's per-pixel alpha work).
				if (clickThrough)
					cp.ExStyle |= WindowsAPI.WS_EX_TRANSPARENT;

				return cp;
			}
		}

		// Toggle the input mode. Before the handle exists the flag is picked up by CreateParams; on a live window
		// WS_EX_TRANSPARENT is flipped in place via SetWindowLong (no re-create needed for click-through).
		internal void SetClickThrough(bool enable)
		{
			clickThrough = enable;

			if (!IsHandleCreated)
				return;

			var ex = WindowsAPI.GetWindowLongPtr(Handle, WindowsAPI.GWL_EXSTYLE).ToInt64();
			var updated = enable ? ex | WindowsAPI.WS_EX_TRANSPARENT : ex & ~(long)WindowsAPI.WS_EX_TRANSPARENT;

			if (updated != ex)
				_ = WindowsAPI.SetWindowLongPtr(Handle, WindowsAPI.GWL_EXSTYLE, new nint(updated));
		}

		internal bool ShowImage(Bitmap image, int x, int y, int width, int height)
		{
			if (image == null)
				return false;

			if (!IsHandleCreated)
				_ = Handle;

			// One UpdateLayeredWindow moves, resizes AND repaints the layered surface atomically (it takes the new
			// position, size and pixels together). Moving/resizing the window FIRST -- via Bounds or a sizing SetWindowPos
			// -- briefly leaves it at the new top-left with the PREVIOUS (smaller) surface still showing; the compositor
			// runs on its own vsync and can catch that half-step, which reads as the overlay snapping toward the top-left
			// and back on nearly every frame of a live resize (a zoom-drag, a mouse-following highlight). So change
			// position and size ONLY through UpdateLayered -- never with a separate window move that precedes the repaint.
			if (!UpdateLayered(image, x, y, width, height))
				return false;
			// Keep it topmost and visible WITHOUT moving or resizing (SWP_NOMOVE|SWP_NOSIZE), so z-order upkeep can't
			// reintroduce that half-step.
			_ = WindowsAPI.SetWindowPos(Handle, new nint(WindowsAPI.HWND_TOPMOST), 0, 0, 0, 0,
										WindowsAPI.SWP_NOACTIVATE | WindowsAPI.SWP_NOMOVE | WindowsAPI.SWP_NOSIZE);
			_ = WindowsAPI.ShowWindow(Handle, WindowsAPI.SW_SHOWNOACTIVATE);
			return true;
		}

		protected override void WndProc(ref Message m)
		{
			// Report every point as transparent so the mouse falls through to the window beneath -- but ONLY while
			// click-through. An interactive overlay defers to the base hit-test so it can receive the mouse.
			if (clickThrough && m.Msg == WindowsAPI.WM_NCHITTEST)
			{
				m.Result = new nint(WindowsAPI.HTTRANSPARENT);
				return;
			}

			base.WndProc(ref m);
		}

		private bool UpdateLayered(Bitmap image, int x, int y, int width, int height)
		{
			nint screenDc = 0, memoryDc = 0, hBitmap = 0, oldBitmap = 0;
			var updated = false;

			try
			{
				screenDc = WindowsAPI.GetDC(0);

				if (screenDc == 0 || (memoryDc = WindowsAPI.CreateCompatibleDC(screenDc)) == 0)
					return false;

				hBitmap = image.GetHbitmap(Color.FromArgb(0));

				if (hBitmap == 0)
					return false;

				oldBitmap = WindowsAPI.SelectObject(memoryDc, hBitmap);
				var topPos = new POINT(x, y);
				var size = new SIZE(width, height);
				var source = new POINT(0, 0);
				var blend = new BLENDFUNCTION
				{
					BlendOp = WindowsAPI.AC_SRC_OVER,
					BlendFlags = 0,
					SourceConstantAlpha = 255,
					AlphaFormat = WindowsAPI.AC_SRC_ALPHA
				};
				updated = WindowsAPI.UpdateLayeredWindow(Handle, screenDc, ref topPos, ref size, memoryDc,
					ref source, 0, ref blend, WindowsAPI.ULW_ALPHA);
			}
			finally
			{
				if (oldBitmap != 0)
					_ = WindowsAPI.SelectObject(memoryDc, oldBitmap);

				if (hBitmap != 0)
					_ = WindowsAPI.DeleteObject(hBitmap);

				if (memoryDc != 0)
					_ = WindowsAPI.DeleteDC(memoryDc);

				if (screenDc != 0)
					_ = WindowsAPI.ReleaseDC(0, screenDc);
			}

			return updated;
		}
	}
}
#endif
