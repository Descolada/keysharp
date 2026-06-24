namespace Keysharp.Builtins
{
	public partial class Ks
	{
		/// <summary>
		/// A cross-platform image: capture it from the screen/a window/a file, queue transforms
		/// (scale, rotate, flip, crop) that are applied lazily, then output it (save to a file or hand
		/// out a native bitmap handle), search it for a sub-image, or read/write individual pixels.
		///
		/// Construct one through a <c>From*</c> factory, e.g. <c>img := Image.FromFile("logo.png")</c>
		/// or <c>Image.FromDesktop().Scale(0.5).Save("thumb.png")</c>. Transform methods accumulate and
		/// return the same instance, so they chain.
		///
		/// The C# type is named <c>KeysharpImage</c> to avoid colliding with the backend drawing
		/// <c>Image</c> type (System.Drawing on Windows, Eto.Drawing elsewhere); scripts see it as
		/// <c>Image</c> via <see cref="UserDeclaredNameAttribute"/>.
		/// </summary>
		[UserDeclaredName("Image")]
		public class KeysharpImage : KeysharpObject, IDisposable
		{
			// The original captured/loaded pixels. Owned by this instance and never mutated by
			// transforms; Materialize() always starts from a fresh copy of it.
			private Bitmap baseBitmap;

			// Queued transforms, applied in order on the next Materialize(). Each takes the running
			// bitmap and returns a new one (or the same instance when it is a no-op).
			private readonly List<Func<Bitmap, Bitmap>> pending = new ();

			// The result of base + pending, built on demand and reused until a new transform invalidates
			// it. Owned by this instance.
			private Bitmap cached;

			// Physical pixels per logical unit at capture time (1.0 for files and non-HiDPI captures).
			// All Image coordinates (Width/Height, Get/SetPixel, Search) are in the image's own pixels,
			// which on a Retina/HiDPI screen or window capture are physical pixels. Scripts that need to
			// map back to logical screen coordinates do so explicitly via the ScaleX/ScaleY properties;
			// the class never silently rescales.
			private double scaleX = 1.0, scaleY = 1.0;

			private bool disposed;

			public KeysharpImage(params object[] args) : base(args) { }

			/// <summary>
			/// <c>Image(source)</c> builds an image from a file path, another Image, or a native bitmap
			/// handle. <c>Image()</c> with no argument creates an empty image (used internally by the
			/// <c>From*</c> factories).
			/// </summary>
			public override object __New(params object[] args)
			{
				if (args != null && args.Length > 0 && args[0] != null)
				{
					var (bmp, sx, sy) = LoadFromSource(args[0]);

					if (bmp == null)
						return Errors.ValueErrorOccurred($"Could not create an image from {args[0]}.");

					baseBitmap = bmp;
					scaleX = sx;
					scaleY = sy;
				}

				return DefaultObject;
			}

			#region Capture / load factories

			/// <summary>Captures the whole virtual desktop (the union of all monitors).</summary>
			[Static] public static object FromDesktop(object @this)
			{
				var (left, top, width, height) = Monitor.GetVirtualScreenBounds();
				return CaptureRect((int)left, (int)top, (int)width, (int)height, mapCoords: false, "Capturing the desktop failed.");
			}

			/// <summary>Captures a single monitor (the primary monitor if <paramref name="n"/> is omitted).</summary>
			[Static] public static object FromMonitor(object @this, object n = null)
			{
				var (left, top, width, height) = Monitor.GetMonitorBounds(n);
				return CaptureRect((int)left, (int)top, (int)width, (int)height, mapCoords: false, "Capturing the monitor failed.");
			}

			/// <summary>
			/// Captures a rectangle of the screen. Coordinates honor the current Pixel CoordMode, just
			/// like the legacy <c>ImageCapture</c> function this replaces.
			/// </summary>
			[Static] public static object FromRect(object @this, object x, object y, object width, object height)
				=> CaptureRect(x.Ai(), y.Ai(), width.Ai(), height.Ai(), mapCoords: true, "Capturing the screen rectangle failed.");

			/// <summary>
			/// Captures the whole window (title bar and borders included) matched by the usual WinTitle
			/// criteria. Uses a true window-server capture where supported (Windows, macOS) so it works
			/// even when the window is occluded. On Linux there is no foreign-window
			/// capture, so it falls back to grabbing the window's on-screen rectangle: the window must be
			/// unobscured and on-screen for the result to be correct, and on Wayland the grab may fail
			/// outright (the call then reports an error).
			///
			/// <para><paramref name="options"/> selects the Windows capture technique (matching OCR.ahk),
			/// either as a bare mode number or an object with a <c>mode</c> property. Modes: 0 = GetDC +
			/// BitBlt; 1 = same, with window transparency turned off first; 2 = PrintWindow; 3 = same,
			/// transparency off first; 4 (default) = PrintWindow with the undocumented PW_RENDERFULLCONTENT
			/// flag (captures hardware-accelerated windows); 5 = UWP Direct3D capture (not yet implemented).
			/// The mode is ignored on macOS/Linux.</para>
			/// </summary>
			[Static] public static object FromWindow(object @this, object winTitle = null, object options = null, object winText = null, object excludeTitle = null, object excludeText = null)
			{
				var mode = ParseCaptureMode(options);

				if (mode == 5)
					return Errors.ValueErrorOccurred("Capture mode 5 (UWP/Direct3D) is not yet implemented; use mode 0-4.");

				var win = WindowSearch.SearchWindow(winTitle, winText, excludeTitle, excludeText, true);

				if (win is not WindowItemBase w)
					return Errors.TargetErrorOccurred(winTitle, winText, excludeTitle, excludeText);

				var (bmp, scale) = GuiHelper.CaptureWindowContent(w.Handle, mode);

				if (bmp != null)
					return Wrap(bmp, scale, scale);

				// No platform window capture (Linux, or the capture failed): grab the window's rectangle.
				var bounds = w.Bounds;
				bmp = GuiHelper.GetScreen(bounds.X, bounds.Y, bounds.Width, bounds.Height);

				if (bmp == null)
					return Errors.ErrorOccurred("Capturing the window failed.");

				double sx = bounds.Width > 0 ? (double)bmp.Width / bounds.Width : 1.0;
				double sy = bounds.Height > 0 ? (double)bmp.Height / bounds.Height : 1.0;
				return Wrap(bmp, sx, sy);
			}

			// Parses the window-capture mode (0-5) from an options argument: a bare integer, or an object
			// with a `mode` property (OCR.ahk style). Defaults to 4 (PrintWindow + PW_RENDERFULLCONTENT).
			private static int ParseCaptureMode(object options)
			{
				if (options == null)
					return 4;

				if (options is long || options is int || options is double)
					return (int)Math.Clamp(options.Al(4), 0, 5);

				var m = Script.GetPropertyValueOrNull(options, "mode");
				return m == null ? 4 : (int)Math.Clamp(m.Al(4), 0, 5);
			}

			/// <summary>
			/// Loads an image from a file. <paramref name="w"/>/<paramref name="h"/> optionally scale it
			/// on load (a negative value keeps the aspect ratio); <paramref name="iconNumber"/> selects an
			/// icon group from multi-icon resources (EXE/DLL/ICO).
			/// </summary>
			[Static] public static object FromFile(object @this, object filename, object w = null, object h = null, object iconNumber = null)
			{
				var f = filename.As();

				if (f.Length == 0)
					return Errors.ValueErrorOccurred("A file name is required.");

				Bitmap bmp;

				try
				{
					bmp = ImageHelper.LoadImage(f, w.Ai(0), h.Ai(0), iconNumber == null ? 0L : ImageHelper.PrepareIconNumber(iconNumber), exactPixels: true).Item1;
				}
				catch (Exception ex)
				{
					return Errors.ValueErrorOccurred(ex.Message);
				}

				return Wrap(bmp, failMsg: $"Loading the image from {f} failed.");
			}

			/// <summary>
			/// Wraps an existing image: another <c>Image</c>, or a native bitmap handle (HBITMAP on
			/// Windows, or a handle previously returned by <see cref="ToBitmap"/>).
			/// </summary>
			[Static] public static object FromBitmap(object @this, object source)
			{
				if (source == null)
					return Errors.ValueErrorOccurred("A bitmap source is required.");

				var (bmp, sx, sy) = LoadFromSource(source);
				return Wrap(bmp, sx, sy, "Could not create an image from the given bitmap.");
			}

			#endregion

			#region Transforms (lazy, chainable)

			/// <summary>Queues a multiplicative resize. <c>Scale(2)</c> doubles; <c>Scale(2, 1)</c> stretches X only.</summary>
			public object Scale(object factor, object factorY = null)
			{
				var sx = factor.Ad();
				var sy = factorY == null ? sx : factorY.Ad();

				if (sx <= 0 || sy <= 0)
					return Errors.ValueErrorOccurred("Scale factors must be positive.");

				pending.Add(b =>
				{
					var nw = Math.Max(1, (int)Math.Round(b.Width * sx));
					var nh = Math.Max(1, (int)Math.Round(b.Height * sy));
					return ImageHelper.ResizeBitmap(b, nw, nh, exactPixels: true);
				});
				Invalidate();
				return this;
			}

			/// <summary>Queues a clockwise rotation by <paramref name="angle"/> degrees. The canvas grows to fit;
			/// <paramref name="background"/> fills the exposed corners. Omit it or pass "" for a transparent
			/// fill; pass a 0xRRGGBB color (including numeric 0 for opaque black) for a solid fill.</summary>
			public object Rotate(object angle, object background = null)
			{
				var deg = angle.Ad();
				var bg = ParseColorArg(background);
				pending.Add(b => ImageHelper.RotateBitmap(b, deg, bg));
				Invalidate();
				return this;
			}

			/// <summary>Queues a mirror: horizontal (left-right) by default, vertical when <paramref name="horizontal"/> is false.</summary>
			public object Flip(object horizontal = null)
			{
				var h = horizontal == null || horizontal.Ab();
				pending.Add(b => ImageHelper.FlipBitmap(b, h));
				Invalidate();
				return this;
			}

			/// <summary>Queues a crop to the (x, y, w, h) sub-region. The region is clamped to the image
			/// bounds at apply time; <paramref name="width"/> and <paramref name="height"/> must be positive.</summary>
			public object Crop(object x, object y, object width, object height)
			{
				int cx = x.Ai(), cy = y.Ai(), cw = width.Ai(), ch = height.Ai();

				if (cw <= 0 || ch <= 0)
					return Errors.ValueErrorOccurred("Crop width and height must be positive.");

				pending.Add(b => ImageHelper.CropBitmap(b, cx, cy, cw, ch));
				Invalidate();
				return this;
			}

			#endregion

			#region Output

			/// <summary>Applies any queued transforms and saves the result to <paramref name="filename"/>.
			/// The encoder is chosen from the extension (.png/.jpg/.bmp/.gif/.tif), defaulting to PNG.
			/// Note that which formats can actually be written depends on the platform's image backend
			/// (e.g. GIF output is unavailable on some Linux gdk-pixbuf builds); a failure is reported as
			/// an error. Returns this image so calls can chain.</summary>
			public object Save(object filename)
			{
				var bmp = Materialize();

				if (bmp == null)
					return Errors.ValueErrorOccurred("There is no image to save.");

				var f = filename.As();

				try
				{
					ImageHelper.SaveBitmap(bmp, f);
				}
				catch (Exception ex)
				{
					return Errors.ValueErrorOccurred($"Saving the image to {f} failed: {ex.Message}");
				}

				return this;
			}

			/// <summary>Applies any queued transforms and returns a native bitmap handle (HBITMAP on
			/// Windows, a Pixbuf/NSImage handle elsewhere) that the "HBITMAP:" consumers (ImageSearch,
			/// Gui Picture, LoadPicture) accept, as the legacy <c>ImageCapture</c> did. The handle is
			/// managed independently of this image. Note: on Windows the handle is a GDI HBITMAP, which
			/// has no alpha channel, so any transparency is lost (fine for opaque screen captures).</summary>
			public object ToBitmap()
			{
				var bmp = Materialize();

				if (bmp == null)
					return 0L;

				// Hand a *copy* to the manager (not `bmp` itself) for two reasons: this image keeps
				// ownership of its own cached bitmap, and the copy ctor forces a surface-backed bitmap
				// (after a transform) to materialize its real backing store (e.g. a Gdk.Pixbuf on Linux)
				// so the handle the manager extracts is valid. Do not "optimize" the copy away.
				if (ImageHandleManager.TryAddBitmap(new Bitmap(bmp), ImageHandleKind.Bitmap, out var handle))
					return handle.ToInt64();

				return 0L;
			}

			/// <summary>Creates a simple GUI window containing this image (after applying any queued
			/// transforms) and shows it (named to match <c>Gui.Show</c>). Returns the <c>Gui</c> so the
			/// script can move, retitle, or close it. On a HiDPI capture the picture is shown at its
			/// logical size, so it appears at its real on-screen size and stays crisp rather than
			/// rendering double-size.</summary>
			public object Show(object title = null)
			{
				var bmp = Materialize();

				if (bmp == null)
					return Errors.ValueErrorOccurred("There is no image to display.");

				if (ToBitmap() is not long handle || handle == 0)
					return Errors.ValueErrorOccurred("Could not prepare the image for display.");

				// Show at logical size (physical pixels divided by the capture scale).
				var dw = (long)Math.Round(bmp.Width / (scaleX > 0 ? scaleX : 1.0));
				var dh = (long)Math.Round(bmp.Height / (scaleY > 0 ? scaleY : 1.0));

				// Construct the Gui through the runtime's Class.Call (the path scripts use); calling the
				// C# constructor directly binds to Gui's internal form-wrapping ctor and crashes.
				if (Script.TheScript.Vars.Statics[typeof(Gui)] is not Class guiClass
						|| guiClass.Call("", title == null ? "Image" : title.As()) is not Gui gui)
					return Errors.ErrorOccurred("Could not create a window to display the image.");

				_ = gui.Add("Picture", $"w{dw} h{dh}", "HBITMAP:" + handle);
				_ = gui.Show();
				return gui;
			}

			#endregion

			#region Pixels / search

			/// <summary>The current width in pixels (after queued transforms).</summary>
			public long Width => Materialize()?.Width ?? 0L;

			/// <summary>The current height in pixels (after queued transforms).</summary>
			public long Height => Materialize()?.Height ?? 0L;

			/// <summary>Physical pixels per logical unit along X at capture time (1.0 for files and
			/// non-HiDPI captures; ~2.0 for a Retina screen/window capture). Multiply a logical screen
			/// width by this to get image pixels, or divide an image X coordinate by it to get logical.</summary>
			public double ScaleX => scaleX;

			/// <summary>Physical pixels per logical unit along Y. See <see cref="ScaleX"/>.</summary>
			public double ScaleY => scaleY;

			/// <summary>Returns the color of the pixel at (x, y) as a 0xRRGGBB integer.</summary>
			public object GetPixel(object x, object y)
			{
				var bmp = Materialize();

				if (bmp == null)
					return Errors.ValueErrorOccurred("There is no image to read.");

				int px = x.Ai(), py = y.Ai();

				if (px < 0 || py < 0 || px >= bmp.Width || py >= bmp.Height)
					return Errors.ValueErrorOccurred($"Pixel ({px}, {py}) is out of range.");

				return (long)(bmp.GetPixel(px, py).ToArgb() & 0xFFFFFF);
			}

			/// <summary>Sets the pixel at (x, y) to <paramref name="color"/> (a 0xRRGGBB integer). Returns this image.</summary>
			public object SetPixel(object x, object y, object color)
			{
				var bmp = Materialize();

				if (bmp == null)
					return Errors.ValueErrorOccurred("There is no image to write.");

				int px = x.Ai(), py = y.Ai();

				if (px < 0 || py < 0 || px >= bmp.Width || py >= bmp.Height)
					return Errors.ValueErrorOccurred($"Pixel ({px}, {py}) is out of range.");

				bmp.SetPixel(px, py, ImageHelper.ArgbToColor(ParseColorArg(color)));
				// Bake the edited render in as the new base (clearing the now-applied transforms) so the
				// pixel survives any later transform, which would otherwise Invalidate() and rebuild from
				// the original base. `cached` is always a distinct copy of `baseBitmap` after Materialize.
				if (!ReferenceEquals(cached, baseBitmap))
					baseBitmap?.Dispose();

				baseBitmap = cached;
				cached = null;
				pending.Clear();
				return this;
			}

			/// <summary>
			/// Searches this image for <paramref name="needle"/> (an Image, a file path, or a bitmap
			/// handle). Returns a two-element array of the first match's top-left position as 0-based
			/// pixel offsets within this image (result[1] = x, result[2] = y), or "" when not found.
			/// These are image pixels, not logical screen coordinates (see <see cref="ScaleX"/>). The
			/// needle and this image must share pixel density to match. <paramref name="variation"/>
			/// (0-255) allows per-channel color tolerance.
			/// </summary>
			public object Search(object needle, object variation = null)
			{
				var haystack = Materialize();

				if (haystack == null)
					return Errors.ValueErrorOccurred("There is no image to search.");

				var (needleBmp, own) = ResolveNeedle(needle);

				if (needleBmp == null)
					return Errors.ValueErrorOccurred("Could not load the search image.");

				try
				{
					var v = variation == null ? 0L : variation.Al();
					var finder = new ImageFinder(haystack) { Variation = (byte)Math.Clamp(v, 0, 255) };
					var loc = finder.Find(needleBmp);

					if (loc.HasValue)
						return new Array((long)loc.Value.X, (long)loc.Value.Y);

					return "";
				}
				finally
				{
					if (own)
						needleBmp.Dispose();
				}
			}

			#endregion

			#region Internals

			// Captures a screen rectangle and wraps it, recording the HiDPI capture scale. When
			// mapCoords is true the rectangle is first translated through the active Pixel CoordMode
			// (matching the legacy ImageCapture); desktop/monitor captures pass absolute coordinates.
			private static object CaptureRect(int x, int y, int w, int h, bool mapCoords, string failMsg)
			{
				if (mapCoords)
					CoordToScreen(ref x, ref y, CoordMode.Pixel);

				var bmp = GuiHelper.GetScreen(x, y, w, h);

				if (bmp == null)
					return Errors.ErrorOccurred(failMsg);

				double sx = w > 0 ? (double)bmp.Width / w : 1.0;
				double sy = h > 0 ? (double)bmp.Height / h : 1.0;
				return Wrap(bmp, sx, sy);
			}

			private static object Wrap(Bitmap bmp, double sx = 1.0, double sy = 1.0, string failMsg = null)
			{
				if (bmp == null)
					return Errors.ValueErrorOccurred(failMsg ?? "Failed to create the image.");

				return new KeysharpImage { baseBitmap = bmp, scaleX = sx, scaleY = sy };
			}

			// Resolves an arbitrary script source to a freshly-owned bitmap plus its capture scale.
			// Accepts another Image, a file path, or a native bitmap handle.
			private static (Bitmap bmp, double sx, double sy) LoadFromSource(object source)
			{
				if (source is KeysharpImage img)
				{
					var b = img.Materialize();
					return (b == null ? null : new Bitmap(b), img.scaleX, img.scaleY);
				}

				if (source is string s)
				{
					try { return (ImageHelper.LoadImage(s, 0, 0, 0L, exactPixels: true).Item1, 1.0, 1.0); }
					catch { return (null, 1.0, 1.0); }
				}

				// Treat anything else as a native bitmap handle.
				var handle = (nint)source.Al();

				if (handle != 0)
				{
					if (ImageHandleManager.TryGetImage(handle, out var image) && image is Bitmap hb)
						return (new Bitmap(hb), 1.0, 1.0);

					try { return (ImageHelper.GetBitmapFromHBitmap(handle), 1.0, 1.0); }
					catch { return (null, 1.0, 1.0); }
				}

				return (null, 1.0, 1.0);
			}

			private static (Bitmap bmp, bool own) ResolveNeedle(object needle)
			{
				if (needle is KeysharpImage img)
				{
					var b = img.Materialize();
					return (b == null ? null : new Bitmap(b), true);
				}

				if (needle is string s && s.Length > 0)
				{
					try { return (ImageHelper.LoadImage(s, 0, 0, 0L, exactPixels: true).Item1, true); }
					catch { return (null, false); }
				}

				var (bmp, _, _) = LoadFromSource(needle);
				return (bmp, true);
			}

			// Applies base + queued transforms, caching the result until the next Invalidate().
			private Bitmap Materialize()
			{
				if (disposed || baseBitmap == null)
					return null;

				if (cached != null)
					return cached;

				var current = new Bitmap(baseBitmap);

				try
				{
					foreach (var op in pending)
					{
						var next = op(current);

						if (next == null || ReferenceEquals(next, current))
							continue;

						current.Dispose();
						current = next;
					}
				}
				catch
				{
					// A transform threw partway through; don't leak the work-in-progress bitmap, and
					// leave `cached` null so a later call can retry cleanly.
					current?.Dispose();
					throw;
				}

				cached = current;
				return cached;
			}

			private void Invalidate()
			{
				cached?.Dispose();
				cached = null;
			}

			// Parses a color argument into packed 0xAARRGGBB. "" / null is fully transparent (alpha 0);
			// any other value is treated as 0xRRGGBB and made fully opaque.
			private static int ParseColorArg(object o)
			{
				if (o == null)
					return 0;

				if (o is long or int or double)
					return unchecked((int)(0xFF000000u | ((uint)o.Al() & 0xFFFFFFu)));

				var s = o.As();

				if (s.Length == 0)
					return 0;

				var v = s.ParseLong();
				return v.HasValue ? unchecked((int)(0xFF000000u | ((uint)v.Value & 0xFFFFFFu))) : 0;
			}

			public object Dispose()
			{
				((IDisposable)this).Dispose();
				return DefaultObject;
			}

			void IDisposable.Dispose()
			{
				if (disposed)
					return;

				disposed = true;
				cached?.Dispose();
				cached = null;
				baseBitmap?.Dispose();
				baseBitmap = null;
				pending.Clear();
			}

			#endregion
		}
	}
}
