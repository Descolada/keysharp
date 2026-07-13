namespace Keysharp.Builtins
{
	public partial class Ks
	{
		/// <summary>
		/// A screen overlay backed by a raster canvas — click-through and always-on-top by default. Draw onto it with
		/// the same shape/text primitives as <see cref="KeysharpImage"/> (<c>DrawRect</c>, <c>FillRect</c>, <c>DrawLine</c>,
		/// <c>DrawEllipse</c>, <c>FillEllipse</c>, <c>DrawText</c>, <c>Clear</c>) or stamp an existing image with
		/// <see cref="DrawImage"/> / <see cref="SetImage"/>, then <see cref="Show"/> it on screen. Drawing while the
		/// overlay is visible updates it live. The canvas is owned by the overlay; <see cref="Destroy"/> (or dropping
		/// all references) frees it. This is the single cross-platform overlay primitive that <c>Highlight</c> and,
		/// on Linux/macOS, <c>ToolTip</c> build on.
		/// <para>By default each draw op auto-repaints (one upload per op). Wrap a burst of primitives in
		/// <see cref="BeginDraw"/>/<see cref="EndDraw"/> — or <see cref="Redraw"/> — to composite a whole HUD frame and
		/// upload it exactly once. Set <see cref="ClickThrough"/> to <c>false</c> to make the overlay receive mouse
		/// input (an interactive HUD) instead of passing clicks through to the windows beneath it.</para>
		/// </summary>
		[UserDeclaredName("Overlay")]
		public class KeysharpOverlay : KeysharpObject
		{
			private const uint OverlayIdPrefix = 0x1000_0000u;
			private const uint IdMask = 0x0FFF_FFFFu;
			private static int nextOverlayId;

			// The drawable canvas (reuses Image's shape/text primitives so no drawing logic is duplicated here).
			private KeysharpImage canvas;
			private uint overlayId;
			private int x;
			private int y;
			private int w;   // explicit width/height; 0 means "use the canvas size"
			private int h;
			private double scale = 1.0;   // content/DPI scale: physical size = logical (w,h) * scale; drawing scales to match
			private long opacity = 255;   // whole-overlay alpha multiplier applied at upload time
			private bool visible;
			private bool shown;
			private bool clickThrough = true;   // default: transparent to mouse input (Highlight/ToolTip depend on this)
			private int suspendCount;           // > 0 while a BeginDraw/EndDraw (or Redraw) batch is deferring uploads

			private uint OverlayId => overlayId != 0
				? overlayId
				: overlayId = OverlayIdPrefix | ((uint)Interlocked.Increment(ref nextOverlayId) & IdMask);

			// PHYSICAL canvas resolution (logical width/height * scale). The canvas is always a physical-resolution
			// bitmap drawn through a matching transform, so a DPI-scaled overlay stays crisp. A 0 authored
			// dimension means "use the canvas's own size" (SetImage).
			private int CanvasW => w > 0 ? Math.Max(1, (int)Math.Round(w * scale)) : (int)(canvas?.Width ?? 0);
			private int CanvasH => h > 0 ? Math.Max(1, (int)Math.Round(h * scale)) : (int)(canvas?.Height ?? 0);

			// On-screen size handed to the platform backing, in that platform's window coordinate units. Windows
			// and Linux place overlays in PHYSICAL pixels, so the window matches the canvas resolution. macOS
			// (Eto/Cocoa) works in LOGICAL points and renders HiDPI into the backing store itself, so the window
			// is sized in points (canvas / scale) while the canvas stays a hi-res bitmap — otherwise a scale-2
			// overlay is drawn as a doubled, upscaled panel instead of a crisp one at the intended size.
#if OSX
			private int EffectiveW => Math.Max(1, (int)Math.Round(CanvasW / scale));
			private int EffectiveH => Math.Max(1, (int)Math.Round(CanvasH / scale));
#else
			private int EffectiveW => CanvasW;
			private int EffectiveH => CanvasH;
#endif

			public KeysharpOverlay(params object[] args) : base(args) { }

			/// <summary>Overlay(x?, y?, w?, h?, scale?) — stores the geometry; the canvas is created on the first
			/// draw (or SetImage), and nothing is shown until Show. x/y and the on-screen size are in the OS's screen
			/// coordinates (physical pixels on Windows/Linux, logical points on macOS, where Cocoa handles HiDPI); the
			/// canvas is always a physical-resolution bitmap of w*scale x h*scale so it stays crisp. Pass
			/// A_ScreenScale (from #import KS) to size an overlay like a DPI-scaled GUI. scale defaults to 1.</summary>
			public override object __New(params object[] args)
			{
				if (args != null)
				{
					if (args.Length > 0 && args[0] != null) x = args[0].Ai();
					if (args.Length > 1 && args[1] != null) y = args[1].Ai();
					if (args.Length > 2 && args[2] != null) w = args[2].Ai();
					if (args.Length > 3 && args[3] != null) h = args[3].Ai();
					if (args.Length > 4 && args[4] != null) scale = Math.Max(0.01, args[4].Ad(1.0));
				}

				return DefaultObject;
			}

			#region Properties

			public object X { get => (long)x; set { x = value.Ai(); MoveLive(); } }
			public object Y { get => (long)y; set { y = value.Ai(); MoveLive(); } }

			/// <summary>Overlay width in LOGICAL draw units (physical size = W * Scale). Changing it resizes the live
			/// surface; the existing canvas is KEPT and the backing STRETCHES it to the new size (a display-time scale,
			/// not a bitmap rebuild), so a solid-fill or tile overlay can grow every frame cheaply without discarding
			/// its content. Draw ops keep targeting the canvas at its authored resolution — to draw crisply at a larger
			/// size, set <see cref="Scale"/> or recreate the overlay.</summary>
			public object W
			{
				get
				{
					if (w > 0)
						return (long)w;

					return canvas != null ? (long)Math.Round(canvas.Width / scale) : 0L;
				}
				set { w = value.Ai(); MoveLive(); }
			}

			/// <summary>Overlay height in LOGICAL draw units (physical size = H * Scale). Changing it stretches the live
			/// surface to the new size (the canvas is kept, not rebuilt) — see <see cref="W"/>.</summary>
			public object H
			{
				get
				{
					if (h > 0)
						return (long)h;

					return canvas != null ? (long)Math.Round(canvas.Height / scale) : 0L;
				}
				set { h = value.Ai(); MoveLive(); }
			}

			/// <summary>Content/DPI scale. The on-screen size is the logical width/height times this factor, and
			/// drawing is scaled to match so it stays crisp; 1 = draw in physical pixels. Set it before drawing —
			/// changing it discards the current canvas (a scale change redefines the canvas resolution). For an
			/// authored-size overlay the canvas is rebuilt blank at the new resolution and repainted immediately; a
			/// SetImage-based overlay (no authored size) has no size to rebuild from, so its stale surface is taken
			/// down and a new SetImage is required to redisplay it.</summary>
			public object Scale
			{
				get => scale;
				set
				{
					var s = Math.Max(0.01, value.Ad(1.0));

					if (s == scale)
						return;

					scale = s;
					canvas?.Dispose();
					canvas = null;

					// The canvas that backed the on-screen surface is gone, so we are no longer truthfully shown,
					// regardless of whether a subsequent withdraw is confirmed.
					shown = false;

					if (!visible)
						return;

					if (w > 0 && h > 0 && EnsureCanvas(out _))
						Refresh();   // authored-size: rebuild a blank canvas at the new resolution and repaint at once
					else
						_ = Platform.Overlay.TryHideImageOverlay(OverlayId);   // SetImage-based / create-failed: drop the stale surface
				}
			}

			/// <summary>Whole-overlay opacity, 0 (invisible) to 255 (opaque, default). Multiplies the
			/// per-pixel alpha at upload time; setting it on a visible overlay re-uploads with the new
			/// alpha, so an OSD can be faded in/out without redrawing its content.</summary>
			public object Opacity
			{
				get => opacity;
				set
				{
					var v = Math.Clamp(value.Al(), 0L, 255L);

					if (v == opacity)
						return;

					opacity = v;
					MaybeRefresh();
				}
			}

			/// <summary>Whether the overlay is transparent to mouse input (default true). Leave it true for a passive
			/// HUD/highlight so clicks reach the windows beneath; set it false to make the overlay RECEIVE mouse input
			/// (an interactive HUD). Changing it on a visible overlay re-applies the input mode immediately.</summary>
			public object ClickThrough
			{
				get => clickThrough;
				set
				{
					var v = value.Ab();

					if (v == clickThrough)
						return;

					clickThrough = v;
					MaybeRefresh();   // re-push so the backing toggles the live surface's input mode
				}
			}

			public object Visible => shown;

			// Return 0 without allocating an overlay id when nothing has been shown yet: a backing only exists once
			// an id has been allocated (on the first Show), so overlayId == 0 means there is no window/handle. Reading
			// the OverlayId property here instead would burn an id (Interlocked.Increment) for a handle that is 0.
			public object Hwnd => overlayId == 0 ? 0L : Platform.Overlay.GetImageOverlayHandle(overlayId).ToInt64();

			#endregion

			#region Draw batching

			/// <summary>Begins a draw batch: subsequent draw ops and property changes update the canvas but DEFER the
			/// on-screen upload until the matching <see cref="EndDraw"/> (or the end of a <see cref="Redraw"/>). The
			/// default is auto-repaint-per-op (one upload per primitive); batching composites a whole HUD frame and
			/// uploads it exactly once. Calls nest — each BeginDraw needs an EndDraw, and the upload happens when the
			/// outermost EndDraw runs. Returns this for chaining.</summary>
			public object BeginDraw()
			{
				suspendCount++;
				return this;
			}

			/// <summary>Ends a draw batch started with <see cref="BeginDraw"/>. When the outermost batch closes, the
			/// accumulated frame is uploaded once (if the overlay is visible). Returns this for chaining.</summary>
			public object EndDraw()
			{
				if (suspendCount > 0)
					suspendCount--;

				if (suspendCount == 0 && visible)
					Refresh();

				return this;
			}

			/// <summary>Runs <paramref name="callback"/> inside a BeginDraw/EndDraw batch, passing this overlay as the
			/// single argument so the callback can draw on it, and uploads the whole result once when it returns.
			/// Equivalent to BeginDraw(); callback(this); EndDraw() — the batch is closed even if the callback throws.
			/// Returns this for chaining.</summary>
			public object Redraw(object callback)
			{
				_ = BeginDraw();

				try
				{
					if (callback is IFuncObj f)
						_ = f.Call(this);
				}
				finally
				{
					_ = EndDraw();
				}

				return this;
			}

			#endregion

			#region Drawing (delegates to the Image canvas, then repaints if shown)

			/// <summary>Fills the whole canvas. Omit <paramref name="color"/> or pass "" for transparent.</summary>
			public object Clear(object color = null) => Draw(() => canvas.Clear(color));

			public object DrawLine(object x1, object y1, object x2, object y2, object color = null, object thickness = null)
				=> Draw(() => canvas.DrawLine(x1, y1, x2, y2, color, thickness));

			public object DrawRect(object rx, object ry, object rw, object rh, object color = null, object thickness = null)
				=> Draw(() => canvas.DrawRect(rx, ry, rw, rh, color, thickness));

			public object FillRect(object rx, object ry, object rw, object rh, object color = null)
				=> Draw(() => canvas.FillRect(rx, ry, rw, rh, color));

			public object DrawEllipse(object rx, object ry, object rw, object rh, object color = null, object thickness = null)
				=> Draw(() => canvas.DrawEllipse(rx, ry, rw, rh, color, thickness));

			public object FillEllipse(object rx, object ry, object rw, object rh, object color = null)
				=> Draw(() => canvas.FillEllipse(rx, ry, rw, rh, color));

			public object DrawRoundRect(object rx, object ry, object rw, object rh, object radius, object color = null, object thickness = null)
				=> Draw(() => canvas.DrawRoundRect(rx, ry, rw, rh, radius, color, thickness));

			public object FillRoundRect(object rx, object ry, object rw, object rh, object radius, object color = null)
				=> Draw(() => canvas.FillRoundRect(rx, ry, rw, rh, radius, color));

			public object DrawText(object text, object tx, object ty, object color = null, object font = null)
				=> Draw(() => canvas.DrawText(text, tx, ty, color, font));

			/// <summary>Measures the size <paramref name="text"/> would occupy in <paramref name="font"/>, in the
			/// overlay's LOGICAL draw units (so it composes with the coordinates passed to DrawText/DrawRect),
			/// writing the width and height into the output variables. Use it to centre or align text.</summary>
			public object MeasureText(object text, object font = null, [ByRef] object width = null, [ByRef] object height = null)
			{
				var (mw, mh) = KeysharpImage.MeasureTextCore(text.As(), font.As());

				if (width != null) Script.SetPropertyValue(width, "__Value", mw);
				if (height != null) Script.SetPropertyValue(height, "__Value", mh);

				return DefaultObject;
			}

			/// <summary>Stamps another image (an Image, a file path, or a bitmap handle) onto the canvas.</summary>
			public object DrawImage(object image, object ix = null, object iy = null, object iw = null, object ih = null)
				=> Draw(() => canvas.DrawImage(image, ix, iy, iw, ih));

			/// <summary>Replaces the whole canvas with a copy of <paramref name="source"/> (an Image, a file path,
			/// or a bitmap handle). Later changes to that source do not affect this overlay.</summary>
			public object SetImage(object source)
			{
				var loaded = source as KeysharpImage;
				var ownsLoaded = false;

				if (loaded == null)
				{
					var result = KeysharpImage.FromBitmap(null, source);

					if (result is not KeysharpImage li)
					{
						// Load failed. Raise the error (throws in the normal throwing mode); otherwise keep the old
						// canvas and return this so a fluent chain stays intact rather than handing back an error object.
						_ = Errors.ValueErrorOccurred("Overlay.SetImage could not load the source image.");
						return this;
					}

					loaded = li;
					ownsLoaded = true;
				}

				var copy = loaded.Copy() as KeysharpImage;

				if (ownsLoaded)
					_ = loaded.Dispose();

				if (copy == null)
				{
					_ = Errors.ValueErrorOccurred("Overlay.SetImage requires a valid Image.");
					return this;
				}

				copy.drawScale = scale;   // Copy() doesn't propagate it; draws after SetImage must keep scaling
				copy.mutable = true;      // subsequent draws on this canvas mutate it in place
				canvas?.Dispose();
				canvas = copy;

				MaybeRefresh();
				return this;
			}

			#endregion

			#region Show / Move / Hide / Destroy

			public object Show(object newX = null, object newY = null, object newW = null, object newH = null)
			{
				if (newX != null) x = newX.Ai();
				if (newY != null) y = newY.Ai();
				if (newW != null) w = newW.Ai();
				if (newH != null) h = newH.Ai();

				if (!EnsureCanvas(out _))
					return this;   // sizeless overlay: EnsureCanvas raised the error (throws in throw-mode); keep chaining otherwise

				// A resize just changes the displayed size; the backing STRETCHES the existing canvas to the new W/H
				// (see the W property), so growing a tile/fill overlay keeps its content instead of blanking it.
				visible = true;
				MaybeRefresh();
				return this;
			}

			public object Move(object newX = null, object newY = null, object newW = null, object newH = null)
			{
				if (newX != null) x = newX.Ai();
				if (newY != null) y = newY.Ai();
				if (newW != null) w = newW.Ai();
				if (newH != null) h = newH.Ai();

				// The backing STRETCHES the existing canvas to the new W/H (see the W property) — a resize is a display
				// scale, not a bitmap rebuild — so a tile/fill overlay resized every frame keeps its content.
				MoveLive();
				return this;
			}

			public object Hide()
			{
				visible = false;

				// Nothing was ever shown (no id/backing was allocated), so there is nothing to withdraw — and reading
				// the OverlayId property here would needlessly burn an id.
				if (overlayId == 0)
				{
					shown = false;
					return this;
				}

				// Only mark ourselves hidden once the platform CONFIRMS the surface is gone. If the withdraw
				// couldn't be confirmed (e.g. a dropped compositor hide), keep shown == true so Visible stays
				// truthful and a later Hide re-attempts, instead of leaving a painted-but-"hidden" orphan.
				if (Platform.Overlay.TryHideImageOverlay(overlayId))
					shown = false;

				return this;
			}

			public object Destroy()
			{
				if (overlayId != 0)
				{
					// Try a graceful, confirm-gated withdraw first...
					_ = Hide();
					// ...then FORCE-reap the backing unconditionally. If Hide couldn't confirm the withdraw (a dropped
					// Wayland hide), the backing would otherwise stay mapped in OverlayService with no owner left to
					// retry — this disposes and removes it for good, distinct from the retryable Hide.
					Platform.Overlay.DisposeImageOverlay(overlayId);
				}

				// The surface and its canvas are being torn down regardless of any backing confirmation above, so
				// Visible must read false.
				visible = false;
				shown = false;
				canvas?.Dispose();
				canvas = null;
				return DefaultObject;
			}

			public override object __Delete() => Destroy();

			#endregion

			// Runs one canvas draw op, bakes it in (so the op chain never grows across repaints), repaints if visible
			// and not mid-batch, and returns this overlay for chaining. On a real failure the canvas op raises the
			// error (throws in the normal throwing mode); we return this either way so a fluent chain never receives
			// an error object to dereference.
			private object Draw(Func<object> op)
			{
				if (!EnsureCanvas(out _))
					return this;

				_ = op();
				canvas.Bake();
				MaybeRefresh();
				return this;
			}

			private bool EnsureCanvas(out object error)
			{
				error = null;

				if (canvas != null)
					return true;

				if (w <= 0 || h <= 0)
				{
					error = Errors.ValueErrorOccurred("Overlay has no size: construct it as Overlay(x, y, w, h) or call SetImage/DrawImage first.");
					return false;
				}

				// Create the canvas at PHYSICAL resolution (logical size * scale) and tell it to scale drawing to
				// match, so DPI-scaled overlays render crisp rather than upscaling a small bitmap.
				if (KeysharpImage.Create(null, (long)CanvasW, (long)CanvasH) is KeysharpImage created)
				{
					created.drawScale = scale;
					created.mutable = true;   // a live draw surface: shapes mutate it in place, no per-op working copy
					canvas = created;
					return true;
				}

				error = Errors.ValueErrorOccurred("Could not create the overlay canvas.");
				return false;
			}

			// Repaints the live surface after a mutation, but ONLY when actually visible and not inside a
			// BeginDraw/EndDraw batch — the batch coalesces many mutations into the single upload EndDraw performs.
			private void MaybeRefresh()
			{
				if (visible && suspendCount == 0)
					Refresh();
			}

			private void MoveLive()
			{
				if (!shown || suspendCount > 0)
					return;

				if (!Platform.Overlay.TryMoveImageOverlay(OverlayId, x, y, EffectiveW, EffectiveH))
					Refresh();
			}

			private void Refresh()
			{
				if (!visible || canvas == null)
					return;

				// Hand the canvas's own bitmap to the backing WITHOUT copying it — the backing borrows it and
				// makes the single display copy itself (it never keeps or disposes what it is handed). Only an
				// opacity pass needs a temporary, which we own and dispose here.
				var bmp = canvas.PeekBitmap();

				if (bmp == null)
					return;

				// ApplyOpacity mutates in place, so to preserve the live canvas we fade a throwaway clone; at full
				// opacity we borrow the canvas bitmap directly (zero-copy). toShow is disposed below iff it's the clone.
				var toShow = opacity != 255 ? ImageHelper.ApplyOpacity(new Bitmap(bmp), (byte)opacity) : bmp;

				try
				{
					// Only ever PROMOTE shown to true on a successful show. A false return while we are already shown
					// is a TRANSIENT refresh failure — OverlayBase deliberately keeps the last good frame up and
					// returns false in that case — so leave shown == true rather than flipping Visible to false under
					// a still-live surface. shown is cleared only by a confirmed Hide, a Scale change, or Destroy.
					if (Platform.Overlay.TryShowImageOverlay(OverlayId, x, y, EffectiveW, EffectiveH, toShow, clickThrough))
						shown = true;
				}
				finally
				{
					if (!ReferenceEquals(toShow, bmp))
						toShow.Dispose();   // dispose only the opacity temp, never the canvas's own bitmap
				}
			}
		}
	}
}
