using Keysharp.Internals;

namespace Keysharp.Builtins
{
	public partial class Ks
	{
		/// <summary>
		/// A screen overlay backed by a raster canvas — click-through and always-on-top by default. Draw onto it with
		/// the same shape/text primitives as <see cref="KeysharpImage"/> (<c>DrawRect</c>, <c>FillRect</c>, <c>DrawLine</c>,
		/// <c>DrawEllipse</c>, <c>FillEllipse</c>, <c>DrawText</c>, <c>Clear</c>) or stamp an existing image with
		/// <see cref="DrawImage"/> / <see cref="SetImage"/>, then <see cref="Show"/> it on screen. Use <see cref="Update"/>
		/// to replace a live overlay's image, position, and native screen-space size in one backing operation. Drawing
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
			private int w;   // explicit native width/height; 0 means "use the image pixel size"
			private int h;
			private long opacity = 255;   // whole-overlay alpha multiplier applied at upload time
			private bool requestedVisible;
			private bool isMapped;
			private bool clickThrough = true;   // default: transparent to mouse input (Highlight/ToolTip depend on this)
			private int suspendCount;           // > 0 while a BeginDraw/EndDraw (or Redraw) batch is deferring uploads
			private bool redrawing;

			private uint OverlayId => overlayId != 0
				? overlayId
				: overlayId = OverlayIdPrefix | ((uint)Interlocked.Increment(ref nextOverlayId) & IdMask);

			private (int ScreenW, int ScreenH) CurrentGeometry
				=> ResolveGeometry(w, h, (int)(canvas?.Width ?? 0), (int)(canvas?.Height ?? 0));

			// W/H are always native screen units. A raster image with no explicit W/H uses one native unit per pixel;
			// generated canvases ask the platform renderer for their actual pixel dimensions.
			private static (int ScreenW, int ScreenH) ResolveGeometry(
				int authoredW, int authoredH, int imageW, int imageH)
				=> (authoredW > 0 ? authoredW : Math.Max(0, imageW),
					authoredH > 0 ? authoredH : Math.Max(0, imageH));

			public KeysharpOverlay(params object[] args) : base(args) { }

			/// <summary>Overlay(x?, y?, w?, h?) stores the geometry; the canvas is created on the first
			/// draw (or SetImage), and nothing is shown until Show. X/Y/W/H are native screen coordinates: PMv2/X11
			/// desktop pixels, Cocoa points, or Wayland logical units. The renderer chooses the pixel size of generated
			/// canvases; supplied images already carry their raster dimensions.</summary>
			public override object __New(params object[] args)
			{
				if (args != null)
				{
					if (args.Length > 0 && args[0] != null) x = args[0].Ai();
					if (args.Length > 1 && args[1] != null) y = args[1].Ai();
					if (args.Length > 2 && args[2] != null) w = args[2].Ai();
					if (args.Length > 3 && args[3] != null) h = args[3].Ai();
					if (args.Length > 4 && args[4] != null)
						return Errors.ValueErrorOccurred("Overlay accepts only x, y, w and h; backing resolution is automatic.");
				}

				return DefaultObject;
			}

			#region Properties

			public object X { get => (long)x; set { if (RejectRedrawMutation()) return; x = value.Ai(); MoveLive(); } }
			public object Y { get => (long)y; set { if (RejectRedrawMutation()) return; y = value.Ai(); MoveLive(); } }

			/// <summary>Overlay width in native screen/draw units. Changing
			/// it resizes the live
			/// surface; the existing canvas is KEPT and the backing STRETCHES it to the new size (a display-time scale,
			/// not a bitmap rebuild), so a solid-fill or tile overlay can grow every frame cheaply without discarding
			/// its content. Draw ops keep targeting the canvas at its authored resolution — to draw crisply at a larger
			/// size, redraw the content or recreate the overlay.</summary>
			public object W
			{
				get
				{
					if (w > 0)
						return (long)w;

					return canvas != null ? (long)CurrentGeometry.ScreenW : 0L;
				}
				set { if (RejectRedrawMutation()) return; w = value.Ai(); MoveLive(); }
			}

			/// <summary>Overlay height in native screen/draw units. Changing
			/// it stretches the live
			/// surface to the new size (the canvas is kept, not rebuilt) — see <see cref="W"/>.</summary>
			public object H
			{
				get
				{
					if (h > 0)
						return (long)h;

					return canvas != null ? (long)CurrentGeometry.ScreenH : 0L;
				}
				set { if (RejectRedrawMutation()) return; h = value.Ai(); MoveLive(); }
			}

			/// <summary>Whole-overlay opacity, 0 (invisible) to 255 (opaque, default). Multiplies the
			/// per-pixel alpha at upload time; setting it on a visible overlay re-uploads with the new
			/// alpha, so an OSD can be faded in/out without redrawing its content.</summary>
			public object Opacity
			{
				get => opacity;
				set
				{
					if (RejectRedrawMutation()) return;
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
					if (RejectRedrawMutation()) return;
					var v = value.Ab();

					if (v == clickThrough)
						return;

					clickThrough = v;
					MaybeRefresh();   // re-push so the backing toggles the live surface's input mode
				}
			}

			public object Visible => isMapped;

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
				if (RejectRedrawMutation()) return this;
				suspendCount++;
				return this;
			}

			/// <summary>Ends a draw batch started with <see cref="BeginDraw"/>. When the outermost batch closes, the
			/// accumulated frame is uploaded once (if the overlay is visible). Returns this for chaining.</summary>
			public object EndDraw()
			{
				if (RejectRedrawMutation()) return this;
				if (suspendCount > 0)
					suspendCount--;

				if (suspendCount == 0 && requestedVisible)
					Refresh();

				return this;
			}

			/// <summary>Builds a complete replacement canvas off-screen, passing this overlay to
			/// <paramref name="callback"/>, then commits its pixels and optional geometry in one platform update.
			/// Drawing uses local native screen units while backing-pixel density is selected automatically for the target.
			/// A drawing exception or failed upload preserves the previous frame and overlay state.</summary>
			public object Redraw(object callback, object newX = null, object newY = null, object newW = null, object newH = null)
			{
				if (RejectRedrawMutation()) return this;
				if (callback is not IFuncObj f)
					return Errors.ValueErrorOccurred("Overlay.Redraw requires a callable object.");

				var nextX = newX != null ? newX.Ai() : x;
				var nextY = newY != null ? newY.Ai() : y;
				var nextW = newW != null ? newW.Ai() : w;
				var nextH = newH != null ? newH.Ai() : h;
				var oldGeometry = CurrentGeometry;
				var screenW = nextW > 0 ? nextW : oldGeometry.ScreenW;
				var screenH = nextH > 0 ? nextH : oldGeometry.ScreenH;

				if (!TryCreateCanvas(new ScreenRect(nextX, nextY, screenW, screenH), out var replacement))
					return Errors.ValueErrorOccurred("Overlay.Redraw requires a positive final width and height.");

				var previousCanvas = canvas;
				var previousX = x;
				var previousY = y;
				var previousW = w;
				var previousH = h;
				var previousOpacity = opacity;
				var previousClickThrough = clickThrough;
				var previousSuspend = suspendCount;
				var committed = false;

				// Draw into a private target-sized canvas. The live backing and previous model are untouched until the
				// final upload succeeds, so a resize never publishes an empty/intermediate surface.
				canvas = replacement;
				x = nextX;
				y = nextY;
				w = screenW;
				h = screenH;
				suspendCount = previousSuspend + 1;
				redrawing = true;

				try
				{
					_ = f.Call(this);
					replacement.Bake();

					if (x != nextX || y != nextY || w != screenW || h != screenH)
						return Errors.ValueErrorOccurred("Overlay.Redraw geometry must be supplied as arguments, not changed inside the callback.");

					var finalBounds = new ScreenRect(x, y, screenW, screenH);

					if (requestedVisible && previousSuspend == 0 && !TryUpload(replacement, finalBounds))
						return this;

					committed = true;
					previousCanvas?.Dispose();

					if (requestedVisible && previousSuspend == 0)
						isMapped = true;
				}
				finally
				{
					redrawing = false;
					suspendCount = previousSuspend;

					if (!committed)
					{
						canvas = previousCanvas;
						x = previousX;
						y = previousY;
						w = previousW;
						h = previousH;
						opacity = previousOpacity;
						clickThrough = previousClickThrough;
						replacement.Dispose();
					}
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
			/// overlay's local draw units (so it composes with the coordinates passed to DrawText/DrawRect),
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
				if (RejectRedrawMutation()) return this;
				if (!TryCopyImage(source, nameof(SetImage), out var copy))
					return this;

				var geometry = ResolveGeometry(w, h, (int)copy.Width, (int)copy.Height);
				SetDrawScale(copy, (int)copy.Width, (int)copy.Height, geometry.ScreenW, geometry.ScreenH);

				canvas?.Dispose();
				canvas = copy;

				MaybeRefresh();
				return this;
			}

			/// <summary>Atomically replaces the image and any supplied geometry. The complete replacement is prepared
			/// off-screen and, when visible, handed to the platform in one upload; no blank canvas, intermediate move, or
			/// intermediate resize is published. Omitted geometry keeps its current value. A failed upload preserves both
			/// the previous on-screen frame and this overlay's previous state. Like <see cref="SetImage"/>, the source is
			/// copied and remains owned by the caller. The image dimensions are its backing pixels; W/H are its native
			/// on-screen size. Update does not change visibility: call <see cref="Show"/> when staging into a hidden overlay.</summary>
			public object Update(object source, object newX = null, object newY = null, object newW = null,
						 object newH = null)
			{
				if (RejectRedrawMutation()) return this;
				var nextX = newX != null ? newX.Ai() : x;
				var nextY = newY != null ? newY.Ai() : y;
				var nextW = newW != null ? newW.Ai() : w;
				var nextH = newH != null ? newH.Ai() : h;
				// Do every fallible image operation before touching the live model. The old canvas remains owned by
				// this overlay and displayed by the backing until the final upload succeeds.
				if (!TryCopyImage(source, nameof(Update), out var replacement))
					return this;

				var nextGeometry = ResolveGeometry(nextW, nextH, (int)replacement.Width, (int)replacement.Height);
				SetDrawScale(replacement, (int)replacement.Width, (int)replacement.Height,
					nextGeometry.ScreenW, nextGeometry.ScreenH);

				var uploadNow = requestedVisible && suspendCount == 0;

				if (uploadNow && !TryUpload(
						replacement, new ScreenRect(nextX, nextY, nextGeometry.ScreenW, nextGeometry.ScreenH)))
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
				previous?.Dispose();

				if (uploadNow)
					isMapped = true;

				return this;
			}

			#endregion

			#region Show / Move / Hide / Destroy

			public object Show(object newX = null, object newY = null, object newW = null, object newH = null)
			{
				if (RejectRedrawMutation()) return this;
				if (newX != null) x = newX.Ai();
				if (newY != null) y = newY.Ai();
				if (newW != null) w = newW.Ai();
				if (newH != null) h = newH.Ai();

				if (!EnsureCanvas())
					return this;   // sizeless overlay: EnsureCanvas raised the error (throws in throw-mode); keep chaining otherwise

				// A resize just changes the displayed size; the backing STRETCHES the existing canvas to the new W/H
				// (see the W property), so growing a tile/fill overlay keeps its content instead of blanking it.
				requestedVisible = true;
				MaybeRefresh();
				return this;
			}

			public object Move(object newX = null, object newY = null, object newW = null, object newH = null)
			{
				if (RejectRedrawMutation()) return this;
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
				if (RejectRedrawMutation()) return this;
				requestedVisible = false;

				// Nothing was ever shown (no id/backing was allocated), so there is nothing to withdraw — and reading
				// the OverlayId property here would needlessly burn an id.
				if (overlayId == 0)
				{
					isMapped = false;
					return this;
				}

				// Only mark ourselves hidden once the platform CONFIRMS the surface is gone. If the withdraw
				// couldn't be confirmed (e.g. a dropped compositor hide), keep isMapped true so Visible stays
				// truthful and a later Hide re-attempts, instead of leaving a painted-but-"hidden" orphan.
				if (Platform.Overlay.TryHideImageOverlay(overlayId))
					isMapped = false;

				return this;
			}

			public object Destroy()
			{
				if (RejectRedrawMutation()) return DefaultObject;
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
				requestedVisible = false;
				isMapped = false;
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
				if (!EnsureCanvas())
					return this;

				_ = op();
				canvas.Bake();
				MaybeRefresh();
				return this;
			}

			private bool EnsureCanvas()
			{
				if (canvas != null)
					return true;

				if (w <= 0 || h <= 0)
				{
					_ = Errors.ValueErrorOccurred("Overlay has no size: construct it as Overlay(x, y, w, h) or call SetImage/DrawImage first.");
					return false;
				}

				if (TryCreateCanvas(new ScreenRect(x, y, w, h), out var created))
				{
					canvas = created;
					return true;
				}

				_ = Errors.ValueErrorOccurred("Could not create the overlay canvas.");
				return false;
			}

			private static bool TryCreateCanvas(ScreenRect bounds, out KeysharpImage created)
			{
				created = null;

				if (!bounds.HasArea)
					return false;

				// Ask the renderer for the target's actual pixel canvas. Drawing coordinates remain native local units.
				var pixels = Platform.Overlay.GetCanvasSize(bounds);

				if (!pixels.HasArea || KeysharpImage.Create(null,
						(long)pixels.Width, (long)pixels.Height) is not KeysharpImage image)
					return false;

				SetDrawScale(image, pixels.Width, pixels.Height, bounds.Width, bounds.Height);
				image.mutable = true;
				created = image;
				return true;
			}

			// Loads (where needed) and copies a caller-owned image without changing live overlay state.
			private bool TryCopyImage(object source, string operation, out KeysharpImage copy)
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

				copy.mutable = true;
				return true;
			}

			private static void SetDrawScale(KeysharpImage image, int pixelW, int pixelH, int screenW, int screenH)
			{
				image.drawScaleX = ScaleFactor.Normalize(screenW > 0 ? (double)pixelW / screenW : 1.0);
				image.drawScaleY = ScaleFactor.Normalize(screenH > 0 ? (double)pixelH / screenH : 1.0);
			}

			private bool RejectRedrawMutation()
			{
				if (!redrawing)
					return false;

				_ = Errors.ValueErrorOccurred("Overlay.Redraw callbacks may draw only; overlay state and lifecycle cannot be changed inside the callback.");
				return true;
			}

			// Repaints the live surface after a mutation, but ONLY when actually visible and not inside a
			// BeginDraw/EndDraw batch — the batch coalesces many mutations into the single upload EndDraw performs.
			private void MaybeRefresh()
			{
				if (requestedVisible && suspendCount == 0)
					Refresh();
			}

			private void MoveLive()
			{
				if (!isMapped || suspendCount > 0)
					return;

				var geometry = CurrentGeometry;
				var bounds = new ScreenRect(x, y, geometry.ScreenW, geometry.ScreenH);

				if (!Platform.Overlay.TryMoveImageOverlay(OverlayId, bounds))
					Refresh();
			}

			private void Refresh()
			{
				if (!requestedVisible || canvas == null)
					return;

				var geometry = CurrentGeometry;

				if (TryUpload(canvas, new ScreenRect(x, y, geometry.ScreenW, geometry.ScreenH)))
					isMapped = true;
			}

			// Uploads one already-prepared canvas at one final geometry. This is the only platform call made by
			// Update; the backing copies synchronously and never retains or disposes the canvas bitmap.
			private bool TryUpload(KeysharpImage source, ScreenRect bounds)
			{
				// Hand the canvas's own bitmap to the backing WITHOUT copying it — the backing borrows it and
				// performs its platform-specific display conversion synchronously (it never keeps or disposes what it is
				// handed). Some backings require more than one native transfer. Only an
				// opacity pass needs a temporary, which we own and dispose here.
				var bmp = source.PeekBitmap();

				if (bmp == null)
					return false;

				// ApplyOpacity mutates in place, so to preserve the live canvas we fade a throwaway clone; at full
				// opacity we borrow the canvas bitmap directly (zero-copy). toShow is disposed below iff it's the clone.
				var toShow = opacity != 255 ? ImageHelper.ApplyOpacity(new Bitmap(bmp), (byte)opacity) : bmp;

				try
				{
					return Platform.Overlay.TryShowImageOverlay(OverlayId, bounds, toShow, clickThrough);
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
