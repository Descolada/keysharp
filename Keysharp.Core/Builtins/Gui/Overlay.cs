namespace Keysharp.Builtins
{
	public partial class Ks
	{
		/// <summary>
		/// A click-through, always-on-top screen overlay backed by a raster canvas. Draw onto it with the same
		/// shape/text primitives as <see cref="KeysharpImage"/> (<c>DrawRect</c>, <c>FillRect</c>, <c>DrawLine</c>,
		/// <c>DrawEllipse</c>, <c>FillEllipse</c>, <c>DrawText</c>, <c>Clear</c>) or stamp an existing image with
		/// <see cref="DrawImage"/> / <see cref="SetImage"/>, then <see cref="Show"/> it on screen. Drawing while the
		/// overlay is visible updates it live. The canvas is owned by the overlay; <see cref="Destroy"/> (or dropping
		/// all references) frees it. This is the single cross-platform overlay primitive that <c>Highlight</c> and,
		/// on Linux/macOS, <c>ToolTip</c> build on.
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

			private uint OverlayId => overlayId != 0
				? overlayId
				: overlayId = OverlayIdPrefix | ((uint)Interlocked.Increment(ref nextOverlayId) & IdMask);

			// Physical on-screen size handed to the platform. The authored width/height are LOGICAL units; the
			// canvas is a physical-resolution bitmap (logical * scale) drawn through a matching transform, so a
			// DPI-scaled overlay stays crisp. A 0 authored dimension means "use the canvas's own size" (SetImage).
			private int EffectiveW => w > 0 ? Math.Max(1, (int)Math.Round(w * scale)) : (int)(canvas?.Width ?? 0);
			private int EffectiveH => h > 0 ? Math.Max(1, (int)Math.Round(h * scale)) : (int)(canvas?.Height ?? 0);

			public KeysharpOverlay(params object[] args) : base(args) { }

			/// <summary>Overlay(x?, y?, w?, h?, scale?) — stores the geometry; the canvas is created on the first
			/// draw (or SetImage), and nothing is shown until Show. x/y are physical screen pixels; w/h are logical
			/// units multiplied by <paramref name="scale"/> for the on-screen size (pass A_ScreenDPI/96 to size an
			/// overlay like a DPI-scaled GUI). scale defaults to 1 (draw in physical pixels).</summary>
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
			public object W { get => (long)(w > 0 ? w : (int)(canvas?.Width ?? 0)); set { w = value.Ai(); MoveLive(); } }
			public object H { get => (long)(h > 0 ? h : (int)(canvas?.Height ?? 0)); set { h = value.Ai(); MoveLive(); } }

			/// <summary>Content/DPI scale. The on-screen size is the logical width/height times this factor, and
			/// drawing is scaled to match so it stays crisp; 1 = draw in physical pixels. Set it before drawing —
			/// changing it discards the current canvas (a scale change redefines the canvas resolution).</summary>
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

					if (visible)
						Refresh();
				}
			}

			public object Visible => shown;
			public object Hwnd => Platform.Overlay.GetImageOverlayHandle(OverlayId).ToInt64();

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
						return result;   // propagate the load error

					loaded = li;
					ownsLoaded = true;
				}

				var copy = loaded.Copy() as KeysharpImage;

				if (ownsLoaded)
					_ = loaded.Dispose();

				if (copy == null)
					return Errors.ValueErrorOccurred("Overlay.SetImage requires a valid Image.");

				copy.drawScale = scale;   // Copy() doesn't propagate it; draws after SetImage must keep scaling
				copy.mutable = true;      // subsequent draws on this canvas mutate it in place
				canvas?.Dispose();
				canvas = copy;

				if (visible)
					Refresh();

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

				if (!EnsureCanvas(out var error))
					return error;

				visible = true;
				Refresh();
				return this;
			}

			public object Move(object newX = null, object newY = null, object newW = null, object newH = null)
			{
				if (newX != null) x = newX.Ai();
				if (newY != null) y = newY.Ai();
				if (newW != null) w = newW.Ai();
				if (newH != null) h = newH.Ai();

				MoveLive();
				return this;
			}

			public object Hide()
			{
				visible = false;
				shown = false;
				_ = Platform.Overlay.TryHideImageOverlay(OverlayId);
				return this;
			}

			public object Destroy()
			{
				_ = Hide();
				canvas?.Dispose();
				canvas = null;
				return DefaultObject;
			}

			public override object __Delete() => Destroy();

			#endregion

			// Runs one canvas draw op, bakes it in (so the op chain never grows across repaints), repaints if
			// shown, and returns this overlay for chaining — or the canvas's error object if the op failed.
			private object Draw(Func<object> op)
			{
				if (!EnsureCanvas(out var error))
					return error;

				var result = op();
				canvas.Bake();

				if (visible)
					Refresh();

				return ReferenceEquals(result, canvas) ? this : result;
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
				if (KeysharpImage.Create(null, (long)EffectiveW, (long)EffectiveH) is KeysharpImage created)
				{
					created.drawScale = scale;
					created.mutable = true;   // a live draw surface: shapes mutate it in place, no per-op working copy
					canvas = created;
					return true;
				}

				error = Errors.ValueErrorOccurred("Could not create the overlay canvas.");
				return false;
			}

			private void MoveLive()
			{
				if (!shown)
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
					shown = Platform.Overlay.TryShowImageOverlay(OverlayId, x, y, EffectiveW, EffectiveH, toShow);
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
