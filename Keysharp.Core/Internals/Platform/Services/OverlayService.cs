#if LINUX
using Wl = Keysharp.Internals.Window.Linux.Wayland;
#endif

namespace Keysharp.Internals
{
	/// <summary>
	/// One click-through image overlay's platform backing. Ownership rule: <see cref="Show"/> BORROWS the bitmap
	/// it is handed — it copies only what it needs to display (and to satisfy same-size moves), synchronously,
	/// and must NOT store, retain a reference to, or dispose the passed bitmap; the caller keeps ownership.
	/// <see cref="Move"/> repositions the last shown content as cheaply as the backing can; if it cannot satisfy
	/// the request without new pixels (e.g. a resize), it returns false and the caller re-renders via <see cref="Show"/>.
	/// </summary>
	internal interface IImageOverlayBacking : IDisposable
	{
		/// <summary><paramref name="clickThrough"/> true (the default for a passive HUD/highlight) makes the surface
		/// transparent to mouse input; false makes it RECEIVE mouse input, where the backing supports an interactive
		/// mode (Windows layered form, Eto window). Backings that cannot be made interactive (compositor-drawn actors,
		/// wlr layer surfaces) accept the flag but remain click-through.</summary>
		bool Show(Bitmap image, int x, int y, int width, int height, bool clickThrough);
		bool Move(int x, int y, int width, int height);

		/// <summary>
		/// Withdraw the on-screen surface. Returns true iff it is CONFIRMED gone (so the caller may forget the id);
		/// false means the withdraw could not be confirmed — e.g. a dropped / timed-out compositor call — and the
		/// caller must keep the backing mapped so a later Hide can re-attempt rather than orphaning the surface.
		/// Must be idempotent: a call after a successful withdraw returns true without doing more work.
		/// </summary>
		bool TryHide();

		nint Handle { get; }
	}

	/// <summary>
	/// Platform-neutral overlay service: owns the id → backing map, its lock, and the show/move/hide/hide-all
	/// orchestration, all in terms of one abstract per-platform <see cref="IImageOverlayBacking"/>. Highlight,
	/// ToolTip (Linux/macOS) and the user-facing Overlay builtin all render through this single image primitive,
	/// so there is no separate highlight/tooltip surface here. The lock scopes only the map bookkeeping — never a
	/// backing's Show/Move/Dispose, which may marshal to the UI thread and would otherwise deadlock.
	/// </summary>
	internal abstract class OverlayBase : IOverlay
	{
		private readonly object sync = new ();
		private readonly Dictionary<uint, IImageOverlayBacking> overlays = new ();

		public abstract OverlayKind PreferredKind { get; }

		public abstract bool SupportsImageOverlay { get; }

		/// <summary>Create the backing for a new overlay id (called under the map lock; must not do UI/IO work).</summary>
		protected abstract IImageOverlayBacking CreateBacking(uint id);

		public bool TryShowImageOverlay(uint id, int x, int y, int width, int height, Bitmap image, bool clickThrough)
		{
			if (id == 0 || image == null)
				return false;

			if (width <= 0) width = image.Width;
			if (height <= 0) height = image.Height;

			if (width <= 0 || height <= 0)
				return TryHideImageOverlay(id);   // nothing to show; the caller still owns `image`

			IImageOverlayBacking backing;
			var created = false;

			lock (sync)
			{
				if (!overlays.TryGetValue(id, out backing))
				{
					overlays[id] = backing = CreateBacking(id);
					created = true;
				}
			}

			// Outside the lock (may hit the UI thread / D-Bus). The backing BORROWS `image` — it copies what it
			// needs, synchronously, and never stores or disposes it. The caller (the Overlay) keeps ownership of
			// its canvas bitmap, so there is nothing to clean up here on either path.
			var shown = backing.Show(image, x, y, width, height, clickThrough);

			if (shown)
				return true;

			// Only drop a backing we just created (it never showed anything). A transient failure while REFRESHING
			// an already-live overlay must keep the last good frame up rather than tearing the whole surface down.
			if (created)
				_ = TryHideImageOverlay(id);

			return false;
		}

		public bool TryMoveImageOverlay(uint id, int x, int y, int width, int height)
		{
			IImageOverlayBacking backing;

			lock (sync)
			{
				if (!overlays.TryGetValue(id, out backing))
					return false;
			}

			return backing.Move(x, y, width, height);
		}

		public bool TryHideImageOverlay(uint id)
		{
			IImageOverlayBacking backing;

			lock (sync)
			{
				if (!overlays.TryGetValue(id, out backing))
					return false;
			}

			// Verify the withdraw BEFORE forgetting the id. If the surface can't be confirmed gone (a dropped or
			// timed-out compositor hide), keep the backing mapped so a later Hide re-attempts — dropping the id here
			// while the surface is still painted is exactly what turned a transient hiccup into a permanent orphan.
			if (!backing.TryHide())
				return false;

			lock (sync)
			{
				// Only forget it if it is still the same backing (a concurrent Show could have replaced it).
				if (overlays.TryGetValue(id, out var current) && ReferenceEquals(current, backing))
					_ = overlays.Remove(id);
			}

			return true;
		}

		public void DisposeImageOverlay(uint id)
		{
			IImageOverlayBacking backing;

			// Unconditional force-reap (no confirm-gating): remove the backing from the map, then dispose it outside
			// the lock. This is Destroy's escape hatch for a backing whose confirm-gated TryHide never succeeded — it
			// must not be left mapped forever with no owner to retry the withdraw.
			lock (sync)
			{
				if (!overlays.TryGetValue(id, out backing))
					return;

				_ = overlays.Remove(id);
			}

			try { backing.Dispose(); } catch { }
		}

		public bool TryHideAllImageOverlays()
		{
			IImageOverlayBacking[] all;

			lock (sync)
			{
				if (overlays.Count == 0)
					return false;

				all = overlays.Values.ToArray();
				overlays.Clear();
			}

			foreach (var backing in all)
			{
				try { backing.Dispose(); } catch { }
			}

			return true;
		}

		public nint GetImageOverlayHandle(uint id)
		{
			lock (sync)
				return overlays.TryGetValue(id, out var backing) ? backing.Handle : 0;
		}
	}

#if LINUX
	/// <summary>Linux overlay backing. Image overlays prefer wlr-layer-shell, then a compositor extension
	/// (GNOME/Cinnamon), then the Eto click-through fallback — chosen per overlay on its first Show.</summary>
	internal sealed class LinuxOverlay : OverlayBase
	{
		public override OverlayKind PreferredKind
		{
			get
			{
				var client = Wl.WaylandLayerShellClient.Current;

				if (client != null && client.IsAvailable)
					return OverlayKind.LayerSurface;

				if (IsWaylandSession && Wl.WaylandBackend.Current?.SupportsImageOverlay == true)
					return OverlayKind.Compositor;

				return OverlayKind.Eto;
			}
		}

		public override bool SupportsImageOverlay => true;

		protected override IImageOverlayBacking CreateBacking(uint id) => new LinuxImageOverlayBacking(id);
	}

	internal enum ImageOverlayKind { Layer, Compositor, Eto }

	// Picks wlr-layer-shell / compositor-extension / Eto on the first Show (falling back to Eto if the preferred
	// backing fails), then reuses that concrete backing for every later Show/Move.
	internal sealed class LinuxImageOverlayBacking : IImageOverlayBacking
	{
		private readonly uint id;
		private IImageOverlayBacking inner;

		internal LinuxImageOverlayBacking(uint id) => this.id = id;

		public nint Handle => inner?.Handle ?? 0;

		public bool Show(Bitmap image, int x, int y, int width, int height, bool clickThrough)
		{
			if (inner != null)
				return inner.Show(image, x, y, width, height, clickThrough);

			var preferred = ChooseKind();

			// Every backing borrows `image` (copies what it needs, never disposes it), so we can hand the same
			// bitmap to the preferred backing and, if it genuinely can't render it, straight to the Eto fallback
			// — no clone needed. Crucially, the compositor backing does NOT report a transient D-Bus TIMEOUT as a
			// failure: the shell almost certainly still received the (cold, large) upload and created the actor,
			// so it commits and returns true, and the next Show/Move updates that same shell-owned actor in place.
			// Only a DEFINITIVE failure (the extension rejected the call / is absent) returns false here. That is
			// what prevents the duplicated-overlay bug — a naive fallback on timeout would leave the shell's actor
			// (correctly positioned) PLUS a second Eto window that native-Wayland GDK cannot position, so it piles
			// up in the screen centre and, being `inner`, steals every live update. See CompositorImageBacking.Show.
			if (preferred != ImageOverlayKind.Eto)
			{
				var backing = Create(preferred);

				if (backing.Show(image, x, y, width, height, clickThrough))
				{
					inner = backing;
					return true;
				}

				backing.Dispose();
			}

			var fallback = new EtoImageOverlay();

			if (fallback.Show(image, x, y, width, height, clickThrough))
			{
				inner = fallback;
				return true;
			}

			fallback.Dispose();
			return false;
		}

		public bool Move(int x, int y, int width, int height) => inner?.Move(x, y, width, height) ?? false;

		public bool TryHide()
		{
			var backing = inner;

			if (backing == null)
				return true;

			// Keep `inner` on an unconfirmed withdraw so a later retry re-attempts it; only forget it once it is gone.
			if (!backing.TryHide())
				return false;

			inner = null;
			return true;
		}

		public void Dispose()
		{
			inner?.Dispose();
			inner = null;
		}

		private IImageOverlayBacking Create(ImageOverlayKind kind) => kind switch
		{
			ImageOverlayKind.Layer => new LayerImageBacking(),
			ImageOverlayKind.Compositor => new CompositorImageBacking(id),
			_ => new EtoImageOverlay(),
		};

		private static ImageOverlayKind ChooseKind()
		{
			var client = Wl.WaylandLayerShellClient.Current;

			if (client != null && client.IsAvailable)
				return ImageOverlayKind.Layer;

			if (IsWaylandSession && Wl.WaylandBackend.Current?.SupportsImageOverlay == true)
				return ImageOverlayKind.Compositor;

			return ImageOverlayKind.Eto;
		}
	}

	// wlr-layer-shell backing (KWin/wlroots). WaylandImageOverlay copies the pixels into its own SHM buffer on
	// Show, so nothing of the borrowed `image` is retained; a same-size move just repositions that surface.
	internal sealed class LayerImageBacking : IImageOverlayBacking
	{
		private Wl.WaylandImageOverlay overlay;
		private int shownW, shownH;

		public nint Handle => overlay?.Handle ?? 0;

		// NOTE (Linux, unverified): the wlr-layer-shell surface is created without a pointer interactivity region, so
		// it is always click-through; interactive overlays (clickThrough == false) are not yet supported on this
		// backing. The flag is accepted for interface parity and to let a future layer-surface input region honor it.
		public bool Show(Bitmap image, int x, int y, int width, int height, bool clickThrough)
		{
			var client = Wl.WaylandLayerShellClient.Current;

			if (client == null || !client.IsAvailable)
				return false;   // borrow: never dispose `image`

			try
			{
				overlay ??= new Wl.WaylandImageOverlay(client);

				// Propagate a genuine layer-shell failure (e.g. the surface never configured within the timeout, so
				// the overlay tore itself down) instead of hard-coding success: returning false lets the
				// LinuxImageOverlayBacking wrapper fall back to a visible Eto window rather than recording this as
				// shown with nothing actually on screen.
				if (!overlay.Show(image, x, y, width, height))   // copies the pixels into its own SHM buffer
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

		public bool Move(int x, int y, int width, int height)
		{
			if (overlay == null)
				return false;

			// Same-size: reposition the retained SHM surface (no re-copy). Resize: re-render via Show.
			if (width == shownW && height == shownH)
			{
				try { overlay.Reposition(x, y); return true; }
				catch { return false; }
			}

			return false;
		}

		public bool TryHide()
		{
			// Tearing down our own Wayland SHM surface is local and synchronous, so it either succeeds or throws.
			try { overlay?.Dispose(); overlay = null; return true; }
			catch { return false; }
		}

		public void Dispose() => _ = TryHide();
	}

	// Compositor-extension backing (GNOME/Cinnamon): hands the pixels to the shell as a PNG. A move asks the shell
	// to reposition the already-uploaded actor (no re-encode); only when that fast path is unavailable - an older
	// extension, or the actor was dropped - does it fall back to re-encoding and re-sending the current image.
	internal sealed class CompositorImageBacking : IImageOverlayBacking
	{
		private readonly uint id;
		private bool shown;
		private bool hidden;

		internal CompositorImageBacking(uint id) => this.id = id;

		public nint Handle => 0;

		// NOTE (Linux, unverified): a compositor-drawn actor has no client-side window, so it is inherently
		// click-through; interactive overlays (clickThrough == false) are not supported on this backing. The flag is
		// accepted for interface parity.
		public bool Show(Bitmap image, int x, int y, int width, int height, bool clickThrough)
		{
			try
			{
				// ToPngBytes reads `image` into a PNG (Eto ToByteArray / GDI Save) which is what gets uploaded;
				// `image` itself is never retained or disposed (borrow contract). ToPngBytes / the D-Bus call
				// can throw — return false without touching `image` on every path.
				var bytes = ImageHelper.ToPngBytes(image);

				if (bytes.Length == 0)
					return false;

				var result = Wl.WaylandBackend.Current?.TryShowImageOverlay(id, x, y, width, height, bytes)
							 ?? Wl.OverlayShowResult.Failed;

				// A DEFINITIVE Failed means the extension is absent or rejected the call — report failure so the
				// LinuxImageOverlayBacking wrapper falls back to a (visible) Eto window. A TIMEOUT is NOT a failure:
				// the shell almost certainly still received this (cold, large) upload and created the actor, so we
				// commit to the compositor — mark it shown (the actor exists, or will momentarily, and the next
				// Show/Move updates it in place) rather than spawning a duplicate Eto window for the same overlay.
				if (result == Wl.OverlayShowResult.Failed)
					return false;

				shown = true;
				hidden = false;
				return true;
			}
			catch
			{
				return false;
			}
		}

		public bool Move(int x, int y, int width, int height)
		{
			// Byte-free reposition: the compositor keeps the pixels we already uploaded and just moves the actor.
			// If that fast path is unavailable, return false so the overlay re-renders via Show.
			return shown && Wl.WaylandBackend.Current?.TryMoveImageOverlay(id, x, y, width, height) == true;
		}

		public bool TryHide()
		{
			// Always ask the shell to drop the actor, even when our Show timed out (shown == false): the shell may
			// still have created it, and HideImageOverlay is a no-op for an unknown id, so an unconditional hide
			// reaps a possibly-orphaned actor without risk (the alternative left it on screen until process-death).
			if (hidden)
				return true;

			try
			{
				var backend = Wl.WaylandBackend.Current;

				// No backend, or the extension is gone (disabled/uninstalled mid-session): the shell has already
				// reaped its actors on disable, so there is nothing left to withdraw. Treat that as a CONFIRMED hide
				// and reclaim the backing — otherwise SendHideImageOverlay returns false (no owner to ack) and the
				// id is kept mapped and retried forever, leaking backings for the rest of the process lifetime.
				if (backend == null || !backend.SupportsImageOverlay)
				{
					hidden = true;
					return true;
				}

				// A definitive ack (actor removed, or unknown id) confirms the withdraw. A dropped / timed-out call
				// returns false so the caller keeps us mapped and retries — that is what stops a lost hide from
				// orphaning the actor for good.
				if (backend.TryHideImageOverlay(id))
				{
					hidden = true;
					return true;
				}

				return false;
			}
			catch { return false; }
		}

		public void Dispose() => _ = TryHide();
	}
#elif WINDOWS
	internal sealed class WindowsOverlay : OverlayBase
	{
		public override OverlayKind PreferredKind => OverlayKind.Eto;
		public override bool SupportsImageOverlay => true;
		protected override IImageOverlayBacking CreateBacking(uint id) => new WindowsImageOverlay();
	}

	// A per-pixel-alpha layered top-level window (UpdateLayeredWindow) that is click-through and never activates.
	internal sealed class WindowsImageOverlay : IImageOverlayBacking
	{
		private LayeredOverlayForm form;
		private int shownW, shownH;

		public nint Handle => form?.IsHandleCreated == true ? form.Handle : 0;

		public bool Show(Bitmap image, int x, int y, int width, int height, bool clickThrough)
		{
			try
			{
				width = Math.Max(1, width <= 0 ? image.Width : width);
				height = Math.Max(1, height <= 0 ? image.Height : height);

				// CreateDisplayBitmap runs on THIS (calling) thread — it copies `image` into the premultiplied
				// display bitmap, which is both the single unavoidable display copy AND the isolation boundary
				// from the caller's live canvas, so we never store or dispose `image` (borrow contract).
				using (var display = CreateDisplayBitmap(image, width, height))
				{
					var d = display;
					Script.InvokeOnUIThread(() =>
					{
						EnsureForm();
						// Apply the input mode before showing so the exstyle is right from the first CreateParams
						// evaluation (a live toggle later goes through SetWindowLong instead).
						form.SetClickThrough(clickThrough);
						form.ShowImage(d, x, y, width, height);
					});
				}

				shownW = width;
				shownH = height;
				return true;
			}
			catch
			{
				return false;
			}
		}

		public bool Move(int x, int y, int width, int height)
		{
			if (form == null)
				return false;

			// A layered window retains its last UpdateLayeredWindow content across a move, so a same-size move
			// is a pure reposition (no pixels needed; matters for mouse-following highlights). A resize needs
			// new pixels, so return false and let the overlay re-render via Show.
			if (width == shownW && height == shownH)
			{
				Script.InvokeOnUIThread(() =>
					_ = WindowsAPI.SetWindowPos(form.Handle, new nint(WindowsAPI.HWND_TOPMOST), x, y, 0, 0,
												WindowsAPI.SWP_NOACTIVATE | WindowsAPI.SWP_NOSIZE));
				return true;
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
				// A same-size frame — the common live-refresh case — needs only the ARGB->PArgb premultiplication,
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

		internal void ShowImage(Bitmap image, int x, int y, int width, int height)
		{
			if (image == null)
				return;

			if (!IsHandleCreated)
				_ = Handle;

			// One UpdateLayeredWindow moves, resizes AND repaints the layered surface atomically (it takes the new
			// position, size and pixels together). Moving/resizing the window FIRST — via Bounds or a sizing SetWindowPos
			// — briefly leaves it at the new top-left with the PREVIOUS (smaller) surface still showing; the compositor
			// runs on its own vsync and can catch that half-step, which reads as the overlay snapping toward the top-left
			// and back on nearly every frame of a live resize (a zoom-drag, a mouse-following highlight). So change
			// position and size ONLY through UpdateLayered — never with a separate window move that precedes the repaint.
			UpdateLayered(image, x, y, width, height);
			// Keep it topmost and visible WITHOUT moving or resizing (SWP_NOMOVE|SWP_NOSIZE), so z-order upkeep can't
			// reintroduce that half-step.
			_ = WindowsAPI.SetWindowPos(Handle, new nint(WindowsAPI.HWND_TOPMOST), 0, 0, 0, 0,
										WindowsAPI.SWP_NOACTIVATE | WindowsAPI.SWP_NOMOVE | WindowsAPI.SWP_NOSIZE);
			_ = WindowsAPI.ShowWindow(Handle, WindowsAPI.SW_SHOWNOACTIVATE);
		}

		protected override void WndProc(ref Message m)
		{
			// Report every point as transparent so the mouse falls through to the window beneath — but ONLY while
			// click-through. An interactive overlay defers to the base hit-test so it can receive the mouse.
			if (clickThrough && m.Msg == WindowsAPI.WM_NCHITTEST)
			{
				m.Result = new nint(WindowsAPI.HTTRANSPARENT);
				return;
			}

			base.WndProc(ref m);
		}

		private void UpdateLayered(Bitmap image, int x, int y, int width, int height)
		{
			var screenDc = WindowsAPI.GetDC(0);
			var memoryDc = WindowsAPI.CreateCompatibleDC(screenDc);
			var hBitmap = image.GetHbitmap(Color.FromArgb(0));
			var oldBitmap = WindowsAPI.SelectObject(memoryDc, hBitmap);

			try
			{
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
				_ = WindowsAPI.UpdateLayeredWindow(Handle, screenDc, ref topPos, ref size, memoryDc, ref source, 0, ref blend, WindowsAPI.ULW_ALPHA);
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
		}
	}
#elif OSX
	internal sealed class MacOverlay : OverlayBase
	{
		public override OverlayKind PreferredKind => OverlayKind.Eto;
		public override bool SupportsImageOverlay => true;
		protected override IImageOverlayBacking CreateBacking(uint id) => new EtoImageOverlay();
	}
#endif

#if LINUX || OSX
	// Shared Eto (GTK/Cocoa) click-through overlay window — the toolkit fallback on Linux and the only backing on
	// macOS. It borrows `image`: Show snapshots it on the calling thread (isolation + the one display copy) and
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
#else
		private ImageView imageView;
#endif
		private Bitmap displayed;
		private int shownW, shownH;

		public nint Handle => form?.Handle ?? 0;

		public bool Show(Bitmap image, int x, int y, int width, int height, bool clickThrough)
		{
			Bitmap snapshot = null;
			var adopted = false;

			try
			{
				// Copy `image` on THIS (calling) thread — the borrowed canvas may be redrawn on this thread, so
				// snapshotting here (not inside the UI-thread callback) isolates the pixels the UI will show.
				snapshot = new Bitmap(image);
				var snap = snapshot;

				Script.InvokeOnUIThread(() =>
				{
					EnsureForm();
					form.CanFocus = !clickThrough;
					// From here PaintOwned owns `snap` (it keeps it as `displayed`, or disposes it after resizing), so
					// mark ownership transferred BEFORE the call: a throw past this point must not ALSO dispose it on
					// the catch path (that would double-free the bitmap the form now holds).
					adopted = true;
					PaintOwned(snap, x, y, width, height);
					form.Location = new Point(x, y);

					if (!form.Visible)
						form.Show();

					form.SetClickThrough(clickThrough);
				});

				return true;   // borrow: `image` is neither retained nor disposed
			}
			catch
			{
				// The UI-thread invoke threw before PaintOwned took ownership of the snapshot — dispose it here so it
				// does not leak. If ownership had transferred, `displayed` owns it now and TryHide will free it.
				if (!adopted)
					snapshot?.Dispose();

				return false;
			}
		}

		public bool Move(int x, int y, int width, int height)
		{
			// Same-size: reposition (the ImageView keeps its bitmap). Resize: re-render via Show.
			if (form == null || width != shownW || height != shownH)
				return false;

			Script.InvokeOnUIThread(() =>
			{
				if (form != null)
					form.Location = new Point(x, y);
			});

			return true;
		}

		// Adopts `snapshot` (an owned, private copy) as the displayed bitmap, resizing it if needed. UI thread.
		// x/y are the overlay's on-screen position and width/height its on-screen size, in the toolkit's window
		// coordinate units (physical px on GTK, logical points on Cocoa). x/y are used only on macOS to pick the
		// screen the overlay actually sits on (for the right backing scale); GTK ignores them here.
		private void PaintOwned(Bitmap snapshot, int x, int y, int width, int height)
		{
			var size = new Size(Math.Max(1, width), Math.Max(1, height));
			var old = displayed;

#if OSX
			// Cocoa sizes windows in LOGICAL points but renders into a HiDPI backing store, so the visible surface
			// is `size * backingScale` device pixels. Match the bitmap to that device resolution: a hi-res card
			// bitmap (already at device size) is kept as-is for a crisp 1:1 result, while a small "stretch tile"
			// (SelFill / guide / border bars) is scaled up to fill the view exactly. Leaving a bitmap whose aspect
			// differs from the view would let NSImageView's ProportionallyUpOrDown scaling shrink-to-fit and CENTRE
			// it — which is what made a drag selection look smaller than the real area with its corner offset.
			// macOS-unverified: use the scale of the screen the overlay is actually PLACED on (derived from x/y) rather
			// than always the primary — a secondary monitor with a different backingScaleFactor would otherwise render
			// at the wrong resolution. Screen.FromRectangle is the Eto API for this; fall back to the primary screen.
			var screen = Forms.Screen.FromRectangle(new RectangleF(x, y, size.Width, size.Height)) ?? Forms.Screen.PrimaryScreen;
			var backing = screen?.LogicalPixelSize ?? 1f;
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
			// Take ownership of the snapshot BEFORE the throwable resize: if ResizeBitmap fails (OOM/GDI), `displayed`
			// still owns a live bitmap that TryHide frees, rather than the snapshot leaking (the caller already set
			// adopted=true, so Show's catch will NOT dispose it).
			displayed = snapshot;

			if (snapshot.Width != size.Width || snapshot.Height != size.Height)
			{
				var resized = ImageHelper.ResizeBitmap(snapshot, size.Width, size.Height, exactPixels: true);
				displayed = resized;
				snapshot.Dispose();
			}
#endif

			form.Size = size;
#if LINUX
			// The Drawable holds no image of its own — it reads `displayed` in its Paint handler. Resize it to the
			// window, then Invalidate. This immediate Invalidate is also what repaints a SAME-SIZE content change
			// (a Highlight recolour, an opacity re-push, a same-size tooltip text swap) — cases where SizeChanged in
			// EnsureForm never fires — so it is NOT redundant with that handler; the two cover different cases.
			imageSurface.Size = size;
			imageSurface.Invalidate();
#else
			imageView.Image = displayed;
			imageView.Size = size;
#endif
			// Free the previous frame only now that the view/Drawable references the NEW bitmap: on macOS imageView
			// still points at `old` until the assignment above, so disposing earlier briefly freed the live image.
			old?.Dispose();
			shownW = width;
			shownH = height;
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
				// was a no-op and the previous (larger) window stretched the new smaller bitmap — an overlay that
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
					e.Graphics.DrawImage(d, 0, 0);
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
