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
			private bool visible;
			private bool shown;

			private uint OverlayId => overlayId != 0
				? overlayId
				: overlayId = OverlayIdPrefix | ((uint)Interlocked.Increment(ref nextOverlayId) & IdMask);

			private int EffectiveW => w > 0 ? w : (int)(canvas?.Width ?? 0);
			private int EffectiveH => h > 0 ? h : (int)(canvas?.Height ?? 0);

			public KeysharpOverlay(params object[] args) : base(args) { }

			/// <summary>Overlay(x?, y?, w?, h?) — stores the geometry; the canvas is created on the first draw
			/// (or SetImage), and nothing is shown until Show.</summary>
			public override object __New(params object[] args)
			{
				if (args != null)
				{
					if (args.Length > 0 && args[0] != null) x = args[0].Ai();
					if (args.Length > 1 && args[1] != null) y = args[1].Ai();
					if (args.Length > 2 && args[2] != null) w = args[2].Ai();
					if (args.Length > 3 && args[3] != null) h = args[3].Ai();
				}

				return DefaultObject;
			}

			#region Properties

			public object X { get => (long)x; set { x = value.Ai(); MoveLive(); } }
			public object Y { get => (long)y; set { y = value.Ai(); MoveLive(); } }
			public object W { get => (long)EffectiveW; set { w = value.Ai(); MoveLive(); } }
			public object H { get => (long)EffectiveH; set { h = value.Ai(); MoveLive(); } }
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

			public object DrawText(object text, object tx, object ty, object color = null, object font = null)
				=> Draw(() => canvas.DrawText(text, tx, ty, color, font));

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

				if (KeysharpImage.Create(null, (long)w, (long)h) is KeysharpImage created)
				{
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

				using var bmp = canvas.SnapshotBitmap();   // the service copies it, so this snapshot is transient

				if (bmp == null)
					return;

				shown = Platform.Overlay.TryShowImageOverlay(OverlayId, x, y, EffectiveW, EffectiveH, bmp);
			}
		}
	}
}
