namespace Keysharp.Builtins
{
	public partial class Ks
	{
		/// <summary>
		/// A screen overlay backed by a raster canvas — click-through and always-on-top by default. Draw onto it with
		/// the same shape/text primitives as <see cref="KeysharpImage"/> (<c>DrawRect</c>, <c>FillRect</c>, <c>DrawLine</c>,
		/// <c>DrawEllipse</c>, <c>FillEllipse</c>, <c>DrawText</c>, <c>Clear</c>) or stamp an existing image with
		/// <see cref="DrawImage"/> / <see cref="SetImage"/>, then <see cref="Show"/> it on screen. Use <see cref="Update"/>
		/// to replace a live overlay's image, position, logical size, and raster scale in one backing operation. Drawing
		/// while the overlay is visible updates it live. The canvas is owned by the overlay; <see cref="Destroy"/> (or dropping
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
			// Physical bitmap pixels per logical display/drawing unit. This is DPI/raster density, never application zoom;
			// zoom belongs in the logical W/H so the visible size and authored coordinate space remain explicit.
			private double rasterScale = 1.0;
			private long opacity = 255;   // whole-overlay alpha multiplier applied at upload time
			private bool visible;
			private bool shown;
			private bool clickThrough = true;   // default: transparent to mouse input (Highlight/ToolTip depend on this)
			private int suspendCount;           // > 0 while a BeginDraw/EndDraw (or Redraw) batch is deferring uploads

			private uint OverlayId => overlayId != 0
				? overlayId
				: overlayId = OverlayIdPrefix | ((uint)Interlocked.Increment(ref nextOverlayId) & IdMask);

			private (int CanvasW, int CanvasH, int EffectiveW, int EffectiveH) CurrentGeometry
				=> ResolveGeometry(w, h, rasterScale, (int)(canvas?.Width ?? 0), (int)(canvas?.Height ?? 0));

			// Resolves authored logical dimensions into both the physical canvas size and the size expected by the
			// platform backing. A 0 authored dimension uses the image's native canvas size (SetImage). Windows and
			// Linux backings use physical pixels; Cocoa uses logical points and supplies its own HiDPI backing store.
			// Keeping this pure lets Update resolve prospective geometry without first mutating the live overlay.
			private static (int CanvasW, int CanvasH, int EffectiveW, int EffectiveH) ResolveGeometry(
				int authoredW, int authoredH, double rasterScale, int imageW, int imageH)
			{
				var canvasW = authoredW > 0 ? Math.Max(1, (int)Math.Round(authoredW * rasterScale)) : imageW;
				var canvasH = authoredH > 0 ? Math.Max(1, (int)Math.Round(authoredH * rasterScale)) : imageH;
#if OSX
				var effectiveW = Math.Max(1, (int)Math.Round(canvasW / rasterScale));
				var effectiveH = Math.Max(1, (int)Math.Round(canvasH / rasterScale));
#else
				var effectiveW = canvasW;
				var effectiveH = canvasH;
#endif
				return (canvasW, canvasH, effectiveW, effectiveH);
			}

			public KeysharpOverlay(params object[] args) : base(args) { }

			/// <summary>Overlay(x?, y?, w?, h?, scale?) — stores the geometry; the canvas is created on the first
			/// draw (or SetImage), and nothing is shown until Show. W/H are the logical display and drawing size; encode
			/// application zoom in them. Scale is strictly raster/DPI density, producing a physical-resolution canvas of
			/// round(w*scale) x round(h*scale) pixels. Windows/Linux display that physical backing size, while Cocoa keeps
			/// W/H as logical points and supplies its HiDPI backing. Pass A_ScreenScale (from #import KS) for a DPI-scaled
			/// canvas. Scale defaults to 1.</summary>
			public override object __New(params object[] args)
			{
				if (args != null)
				{
					if (args.Length > 0 && args[0] != null) x = args[0].Ai();
					if (args.Length > 1 && args[1] != null) y = args[1].Ai();
					if (args.Length > 2 && args[2] != null) w = args[2].Ai();
					if (args.Length > 3 && args[3] != null) h = args[3].Ai();
					if (args.Length > 4 && args[4] != null) rasterScale = Math.Max(0.01, args[4].Ad(1.0));
				}

				return DefaultObject;
			}

			#region Properties

			public object X { get => (long)x; set { x = value.Ai(); MoveLive(); } }
			public object Y { get => (long)y; set { y = value.Ai(); MoveLive(); } }

			/// <summary>Overlay width in LOGICAL display/draw units. Application zoom belongs here, not in Scale. Changing
			/// it resizes the live
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

					return canvas != null ? (long)Math.Round(canvas.Width / rasterScale) : 0L;
				}
				set { w = value.Ai(); MoveLive(); }
			}

			/// <summary>Overlay height in LOGICAL display/draw units. Application zoom belongs here, not in Scale. Changing
			/// it stretches the live
			/// surface to the new size (the canvas is kept, not rebuilt) — see <see cref="W"/>.</summary>
			public object H
			{
				get
				{
					if (h > 0)
						return (long)h;

					return canvas != null ? (long)Math.Round(canvas.Height / rasterScale) : 0L;
				}
				set { h = value.Ai(); MoveLive(); }
			}

			/// <summary>Raster/DPI density in physical bitmap pixels per logical W/H unit; 1 = draw in physical pixels.
			/// It is not application zoom: change W/H to resize or zoom the content. Drawing is scaled to this density so
			/// it stays crisp. Set it before drawing — changing it discards the current canvas because it redefines the
			/// backing resolution. For an
			/// authored-size overlay the canvas is rebuilt blank at the new resolution and repainted immediately; a
			/// SetImage-based overlay (no authored size) has no size to rebuild from, so its stale surface is taken
			/// down and a new SetImage is required to redisplay it.</summary>
			public object Scale
			{
				get => rasterScale;
				set
				{
					var s = Math.Max(0.01, value.Ad(1.0));

					if (s == rasterScale)
						return;

					rasterScale = s;
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
				if (!TryCopyImage(source, rasterScale, nameof(SetImage), out var copy))
					return this;

				canvas?.Dispose();
				canvas = copy;

				MaybeRefresh();
				return this;
			}

			/// <summary>Atomically replaces the image and any supplied geometry. The complete replacement is prepared
			/// off-screen and, when visible, handed to the platform in one upload; no blank canvas, intermediate move, or
			/// intermediate resize is published. Omitted geometry keeps its current value. A failed upload preserves both
			/// the previous on-screen frame and this overlay's previous state. Like <see cref="SetImage"/>, the source is
			/// copied and remains owned by the caller. NewScale is raster/DPI density only; encode application zoom in
			/// NewW/NewH. Update does not change visibility: call <see cref="Show"/> separately when staging an image into
			/// a hidden overlay.</summary>
			public object Update(object source, object newX = null, object newY = null, object newW = null,
							 object newH = null, object newScale = null)
			{
				var nextX = newX != null ? newX.Ai() : x;
				var nextY = newY != null ? newY.Ai() : y;
				var nextW = newW != null ? newW.Ai() : w;
				var nextH = newH != null ? newH.Ai() : h;
				var nextRasterScale = newScale != null ? Math.Max(0.01, newScale.Ad(1.0)) : rasterScale;

				// Do every fallible image operation before touching the live model. The old canvas remains owned by
				// this overlay and displayed by the backing until the final upload succeeds.
				if (!TryCopyImage(source, nextRasterScale, nameof(Update), out var replacement))
					return this;

				var nextGeometry = ResolveGeometry(
					nextW, nextH, nextRasterScale, (int)replacement.Width, (int)replacement.Height);

				var uploadNow = visible && suspendCount == 0;

				if (uploadNow && !TryUpload(
						replacement, nextX, nextY, nextGeometry.EffectiveW, nextGeometry.EffectiveH))
				{
					replacement.Dispose();
					return this;
				}

				var previous = canvas;
				canvas = replacement;
				x = nextX;
				y = nextY;
				w = nextW;
				h = nextH;
				rasterScale = nextRasterScale;
				previous?.Dispose();

				if (uploadNow)
					shown = true;

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
				var geometry = CurrentGeometry;

				if (KeysharpImage.Create(null, (long)geometry.CanvasW, (long)geometry.CanvasH) is KeysharpImage created)
				{
					created.drawScale = rasterScale;
					created.mutable = true;   // a live draw surface: shapes mutate it in place, no per-op working copy
					canvas = created;
					return true;
				}

				error = Errors.ValueErrorOccurred("Could not create the overlay canvas.");
				return false;
			}

			// Loads (where needed) and copies a caller-owned image without changing live overlay state. Stamp the
			// overlay's raster density onto the replacement so later draw operations use its logical W/H coordinate space.
			private bool TryCopyImage(object source, double rasterScale, string operation, out KeysharpImage copy)
			{
				copy = null;
				var loaded = source as KeysharpImage;
				var ownsLoaded = false;

				if (loaded == null)
				{
					var result = KeysharpImage.FromBitmap(null, source);

					if (result is not KeysharpImage li)
					{
						_ = Errors.ValueErrorOccurred($"Overlay.{operation} could not load the source image.");
						return false;
					}

					loaded = li;
					ownsLoaded = true;
				}

				copy = loaded.Copy() as KeysharpImage;

				if (ownsLoaded)
					_ = loaded.Dispose();

				if (copy == null)
				{
					_ = Errors.ValueErrorOccurred($"Overlay.{operation} requires a valid Image.");
					return false;
				}

				copy.drawScale = rasterScale;
				copy.mutable = true;
				return true;
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

				var geometry = CurrentGeometry;

				if (!Platform.Overlay.TryMoveImageOverlay(OverlayId, x, y, geometry.EffectiveW, geometry.EffectiveH))
					Refresh();
			}

			private void Refresh()
			{
				if (!visible || canvas == null)
					return;

				var geometry = CurrentGeometry;

				if (TryUpload(canvas, x, y, geometry.EffectiveW, geometry.EffectiveH))
					shown = true;
			}

			// Uploads one already-prepared canvas at one final geometry. This is the only platform call made by
			// Update; the backing copies synchronously and never retains or disposes the canvas bitmap.
			private bool TryUpload(KeysharpImage source, int targetX, int targetY, int targetW, int targetH)
			{
				// Hand the canvas's own bitmap to the backing WITHOUT copying it — the backing borrows it and
				// makes the single display copy itself (it never keeps or disposes what it is handed). Only an
				// opacity pass needs a temporary, which we own and dispose here.
				var bmp = source.PeekBitmap();

				if (bmp == null)
					return false;

				// ApplyOpacity mutates in place, so to preserve the live canvas we fade a throwaway clone; at full
				// opacity we borrow the canvas bitmap directly (zero-copy). toShow is disposed below iff it's the clone.
				var toShow = opacity != 255 ? ImageHelper.ApplyOpacity(new Bitmap(bmp), (byte)opacity) : bmp;

				try
				{
					return Platform.Overlay.TryShowImageOverlay(OverlayId, targetX, targetY, targetW, targetH,
															toShow, clickThrough);
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
