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

			// Screen-absolute position of this image's top-left at capture time (0,0 for file/bitmap images
			// that have no on-screen origin). Lets a consumer such as OCR map coordinates measured inside the
			// image back to screen coordinates without the caller having to pass the capture rectangle again.
			private int originX, originY;

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
				return CaptureRect((int)left, (int)top, (int)width, (int)height, "Capturing the desktop failed.");
			}

			/// <summary>Captures a single monitor (the primary monitor if <paramref name="n"/> is omitted).</summary>
			[Static] public static object FromMonitor(object @this, object n = null)
			{
				var (left, top, width, height) = Monitor.GetMonitorBounds(n);
				return CaptureRect((int)left, (int)top, (int)width, (int)height, "Capturing the monitor failed.");
			}

			/// <summary>
			/// Captures a rectangle of the screen. Coordinates are always absolute screen coordinates;
			/// unlike the screen-pixel functions (<c>PixelGetColor</c>, <c>ImageSearch</c>), this factory
			/// deliberately ignores the Pixel CoordMode so it matches its sibling capture factories
			/// (<c>FromDesktop</c>, <c>FromMonitor</c>, <c>FromWindow</c>), which are all absolute.
			/// </summary>
			[Static] public static object FromRect(object @this, object x, object y, object width, object height)
				=> CaptureRect(x.Ai(), y.Ai(), width.Ai(), height.Ai(), "Capturing the screen rectangle failed.");

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
			/// The mode is ignored on macOS/Linux. The <c>decorations</c> property (default false) controls
			/// whether the title bar/borders are captured; it is honored only on KWin Wayland (false = client
			/// area only, true = full window) and ignored elsewhere, where each backend captures a fixed extent.</para>
			/// </summary>
			[Static] public static object FromWindow(object @this, object winTitle = null, object options = null, object winText = null, object excludeTitle = null, object excludeText = null)
			{
				var mode = ParseCaptureMode(options);

				if (mode == 5)
					return Errors.ValueErrorOccurred("Capture mode 5 (UWP/Direct3D) is not yet implemented; use mode 0-4.");

				var win = WindowSearch.SearchWindow(winTitle, winText, excludeTitle, excludeText, true);

				if (win is not WindowItemBase w)
					return Errors.TargetErrorOccurred(winTitle, winText, excludeTitle, excludeText);

				// Whether to capture the title bar/borders. The default (false) captures only the client area
				// where the backend supports it (KWin); excluding decorations avoids the shadow-padded buffer whose
				// margin can't be mapped back to screen reliably. Honored only on KWin; on Windows/macOS/GNOME/X11
				// each backend captures a fixed extent and the flag is ignored.
				var includeDeco = ParseIncludeDecoration(options);

				// The window's screen-absolute frame rectangle. Its top-left is the on-screen origin recorded on
				// the Image so OCR (and any other consumer) can map image coordinates back to the screen.
				var bounds = w.Bounds;
				var (bmp, scale) = GuiHelper.CaptureWindowContent(w.Handle, mode, includeDeco);

				if (bmp != null)
				{
					// Origin of the captured pixels in screen coordinates — where the image's top-left sits on screen.
					int ox = bounds.X, oy = bounds.Y;

					// How far the capture extends beyond the window frame per side, in physical pixels, assuming a
					// SYMMETRIC margin (KWin's include-decoration buffer and the GNOME window actor both pad the frame
					// symmetrically with the drop shadow). >0 means decorations/shadow were captured around the frame.
					double shadowXpx = (bmp.Width - bounds.Width * scale) / 2.0;
					double shadowYpx = (bmp.Height - bounds.Height * scale) / 2.0;
					bool capturedBeyondFrame = shadowXpx > 0.5 || shadowYpx > 0.5;

					// Decorations are in the captured pixels when we asked for them (and the backend honored it) or
					// when the backend includes them regardless of the flag — GNOME always images the whole actor, so
					// capturedBeyondFrame catches that even though includeDeco is false (otherwise the client branch
					// below would wrongly map the origin to the client top-left).
					if (includeDeco || capturedBeyondFrame)
					{
						// Frame- or buffer-aligned capture. Windows' PrintWindow and macOS' capture return exactly the
						// frame (shadow margin ~0), so the frame origin is right (default above). KWin include-decoration
						// and the GNOME actor return the frame padded by the symmetric shadow margin; offset the origin to
						// the buffer's top-left derived from that margin. The shadow is transparent and harmless to OCR.
						// The captured image IS the buffer, so its size vs the frame gives the margin directly.
						if (capturedBeyondFrame)
						{
							ox = bounds.X - (int)Math.Round(shadowXpx / scale);
							oy = bounds.Y - (int)Math.Round(shadowYpx / scale);
						}
					}
#if LINUX
					else
					{
						// Client-area capture (X11 XGetImage, or KWin Wayland without decorations): the captured pixels are
						// already the client area natively (no crop), beginning at the CLIENT top-left and smaller than the
						// frame. Prefer the compositor's reported client position (now derived from clientPos/clientSize
						// when KWin doesn't expose clientGeometry); if it's still unreliable (equals the frame), derive the
						// insets from the capture: the client image is smaller than the frame by the borders, so
						// side = (frameW - capW)/2 and titleTop = (frameH - capH) - side.
						var clientPt = w.ClientToScreen();
						double capW = scale != 0 ? bmp.Width / scale : bmp.Width;
						double capH = scale != 0 ? bmp.Height / scale : bmp.Height;
						bool clientUnreliable = clientPt.X == bounds.X && clientPt.Y == bounds.Y && capH < bounds.Height - 1;

						if (clientUnreliable)
						{
							int side = (int)Math.Max(0, Math.Round((bounds.Width - capW) / 2));
							int titleTop = (int)Math.Max(0, Math.Round(bounds.Height - capH - side));
							(ox, oy) = (bounds.X + side, bounds.Y + titleTop);
						}
						else if (clientPt.X >= bounds.X && clientPt.Y >= bounds.Y && (clientPt.X != 0 || clientPt.Y != 0))
						{
							(ox, oy) = (clientPt.X, clientPt.Y);
						}
					}
#endif
					return Wrap(bmp, scale, scale, originX: ox, originY: oy);
				}

				// No platform window capture: grab the window's on-screen rectangle. That grab is frame-aligned
				// (it includes the title bar), so the frame origin is correct for this path.
				bmp = GuiHelper.GetScreen(bounds.X, bounds.Y, bounds.Width, bounds.Height);

				if (bmp == null)
					return Errors.ErrorOccurred("Capturing the window failed.");

				double sx = bounds.Width > 0 ? (double)bmp.Width / bounds.Width : 1.0;
				double sy = bounds.Height > 0 ? (double)bmp.Height / bounds.Height : 1.0;
				return Wrap(bmp, sx, sy, originX: bounds.X, originY: bounds.Y);
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

			// Whether the capture should include the window's title bar/borders, read from an options object's
			// `decorations` property (e.g. Image.FromWindow("A", {decorations: true})). Defaults to false (client
			// area only): on KWin a decoration-inclusive capture returns the window's shadow-padded buffer, and the
			// shadow margin can't be mapped back to screen reliably. A bare integer or no options keeps the default.
			private static bool ParseIncludeDecoration(object options)
			{
				if (options == null || options is long or int or double)
					return false;

				var d = Script.GetPropertyValueOrNull(options, "decorations");
				return d != null && d.Ab();
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
				// Scaling multiplies the pixels-per-logical-unit density: after Scale(2) there are twice as
				// many image pixels per logical unit. Folding the factor into scaleX/scaleY keeps the
				// image->logical mapping (e.g. OCR dividing word coordinates by ScaleX) correct, so callers
				// can upscale for accuracy without their coordinates drifting.
				scaleX *= sx;
				scaleY *= sy;
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
				// Cropping moves the image's top-left to (cx, cy) in image pixels, so its on-screen origin
				// shifts by that offset converted to logical units. Keeping originX/originY in step lets a
				// consumer such as OCR map coordinates from the cropped image back to the original screen
				// position. (CropBitmap clamps a negative start to 0, so clamp here to match.)
				originX += (int)Math.Round(Math.Max(0, cx) / scaleX);
				originY += (int)Math.Round(Math.Max(0, cy) / scaleY);
				Invalidate();
				return this;
			}

			/// <summary>
			/// Returns an independent copy of this image with its queued transforms already applied, carrying
			/// over the scale and screen-origin metadata. Edits or further transforms on the copy do not affect
			/// the original — used when a consumer (e.g. OCR) needs to transform an image the caller still owns.
			/// </summary>
			public object Copy()
			{
				if (disposed || baseBitmap == null)
					return Errors.ValueErrorOccurred("There is no image to copy.");

				// Materialize() applies any queued transforms; with none it returns the base bitmap directly (no
				// clone), so Copy() makes exactly one independent copy of the result either way.
				var src = Materialize();

				if (src == null)
					return Errors.ValueErrorOccurred("There is no image to copy.");

				return new KeysharpImage { baseBitmap = new Bitmap(src), scaleX = scaleX, scaleY = scaleY, originX = originX, originY = originY };
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
			/// script can move, retitle, or close it. The picture is shown at the image's full pixel size
			/// (the size reported in the caption); the window opens that big, or screen-sized if the image
			/// is larger than the monitor (Windows will not make a window bigger than the screen). It is
			/// resizable between a small lower bound and the image size, never past it into dead space, and
			/// scrollbars appear whenever the window is smaller than the image so all of it stays reachable.
			///
			/// <para>When <paramref name="wait"/> is true the call blocks until the user closes the window
			/// (a Keysharp convenience beyond AHK's Image): handy because OCR results normally flash by, so you
			/// can eyeball the captured image first. The wait pumps the message loop, so timers, hotkeys and the
			/// window itself stay responsive; the preview window is destroyed once dismissed.</para></summary>
			public object Show(object title = null, object wait = null)
			{
				var bmp = Materialize();

				if (bmp == null)
					return Errors.ValueErrorOccurred("There is no image to display.");

				// Snapshot the dimensions from this one materialized bitmap so the caption and the Picture size are
				// provably the same source as the displayed pixels: ToBitmap() below re-materializes, but with no
				// Invalidate() in between it returns the same cached bitmap, so imgW/imgH match the shown handle.
				int imgW = bmp.Width, imgH = bmp.Height;

				if (ToBitmap() is not long handle || handle == 0)
					return Errors.ValueErrorOccurred("Could not prepare the image for display.");

				// Caption shows the image's true pixel size and capture scale as a percentage
				// (e.g. "Image  3840 x 2160 @ 200%").
				var baseTitle = title == null ? "Image" : title.As();
				var caption = $"{baseTitle}  {imgW} x {imgH} @ {FormatScalePercent(scaleX, scaleY)}";

				// Construct the Gui through the runtime's Class.Call (the path scripts use); calling the
				// C# constructor directly binds to Gui's internal form-wrapping ctor and crashes. "+Resize"
				// makes the window stretchable; "+AutoScroll" lets the whole image be reached by scrolling
				// when the window is smaller than it. Windows refuses to make a window larger than the
				// monitor (it clamps to the max tracking size), so a picture bigger than the screen cannot
				// be shown by a window that fits it — scrolling bridges that gap.
				if (Script.TheScript.Vars.Statics[typeof(Gui)] is not Class guiClass
						|| guiClass.Call("+Resize +AutoScroll", caption) is not Gui gui)
					return Errors.ErrorOccurred("Could not create a window to display the image.");

				// Size the picture to the image's full pixel dimensions (the caption size). A Picture from
				// an "HBITMAP:" source is set to the loaded bitmap's pixel size directly, bypassing the GUI's
				// DPI scaling, so passing the *pixel* width/height (not a DPI-logical size) is what makes the
				// window open at the size the caption advertises. The control is fixed at this size and
				// pinned top-left; scrolling reaches any part the window is too small to show.
				_ = gui.Add("Picture", $"w{imgW} h{imgH}", "HBITMAP:" + handle);

				// Cap the window at the size it first opens (the image, or the screen if the image is larger):
				// "+MaxSize" with no dimensions defers the limit until first show, where the Gui pins it to
				// that size, so the window can't be stretched past the image into dead space. "+MinSize"
				// gives a small, DPI-scaled lower bound so it can't be shrunk away to nothing.
				_ = gui.Opt("+MinSize120x90 +MaxSize");

				if (wait != null && wait.Ab())
				{
					// Block until the user dismisses the window. Hook the form's close directly rather than
					// WinWaitClose(gui.Hwnd): an AHK GUI HIDES rather than destroys on close, so WinWaitClose
					// would hinge on window-search-by-handle plus the script's DetectHiddenWindows state to
					// decide "gone" — both fragile here (and window-id matching is unreliable on Wayland). The
					// Closing event is a direct, per-window signal with none of those dependencies. A plain
					// captured bool is enough: the event and the pump both run on the UI thread, and
					// WaitWithMessagePump re-invokes the predicate each iteration so the read is never hoisted.
					// Subscribe before Show so a near-instant close can't be missed.
					var closing = false;
#if WINDOWS
					gui.form.FormClosing += (s, e) => closing = true;
#else
					gui.form.Closing += (s, e) => closing = true;
#endif
					_ = gui.Show();
					Keysharp.Internals.Flow.WaitWithMessagePump(() => !closing);
					_ = gui.Destroy();
					return gui;
				}

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

			/// <summary>Screen-absolute X coordinate of this image's top-left at capture time (0 for file/bitmap
			/// images, which have no on-screen origin). OCR uses it as the default x offset so highlights and
			/// clicks land on the screen position the words actually occupy.</summary>
			public long X => originX;

			/// <summary>Screen-absolute Y coordinate of this image's top-left at capture time. See <see cref="X"/>.</summary>
			public long Y => originY;

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
				// Persist the edit. With queued transforms `bmp` is the materialized copy (cached): bake it in as
				// the new base and drop the now-applied transforms so the pixel survives a later Invalidate(). With
				// no transforms `bmp` IS the base and was edited in place, so there is nothing to bake.
				if (!ReferenceEquals(bmp, baseBitmap))
				{
					baseBitmap?.Dispose();
					baseBitmap = bmp;   // == cached
					cached = null;
					pending.Clear();
				}

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

			/// <summary>
			/// Materializes the image and copies its pixels into a freshly allocated <see cref="Buffer"/>,
			/// tightly packed and top-down (row stride = <c>Width * bytesPerPixel</c>), ready to hand to a
			/// native imaging/OCR library through DllCall (e.g. Tesseract's <c>SetImage</c>).
			/// <paramref name="bytesPerPixel"/> selects the layout:
			/// <list type="bullet">
			///   <item><c>1</c> (default): 8-bit grayscale, one luminance byte per pixel
			///   (<c>0.30 R + 0.59 G + 0.11 B</c>). Unambiguous across byte orders — this is what
			///   Tesseract expects for <c>bytes_per_pixel = 1</c>, and what OCR engines threshold anyway.</item>
			///   <item><c>4</c>: 32-bit color in R, G, B, A byte order (the layout Leptonica/Tesseract use
			///   for <c>bytes_per_pixel = 4</c>), preserving color and alpha.</item>
			/// </list>
			/// The returned Buffer owns its memory; keep a reference to it for as long as the native side
			/// reads from <c>buf.Ptr</c>. The pixel dimensions are this image's <see cref="Width"/>/<see cref="Height"/>.
			/// </summary>
			public object GetPixelData(object bytesPerPixel = null)
			{
				var bpp = (int)(bytesPerPixel == null ? 1L : bytesPerPixel.Al());

				if (bpp != 1 && bpp != 4)
					return Errors.ValueErrorOccurred("GetPixelData supports only 1 (grayscale) or 4 (RGBA) bytes per pixel.");

				var bmp = Materialize();

				if (bmp == null)
					return Errors.ValueErrorOccurred("There is no image to read.");

				int w = bmp.Width, h = bmp.Height;
				var buf = new Buffer((long)w * h * bpp);

				unsafe
				{
					WritePixelData(bmp, (byte*)buf.Ptr, bpp);
				}

				return buf;
			}

			#endregion

			#region Internals

			// Reads a bitmap's pixels straight into dst in the requested layout (bpp 1 = 8-bit grayscale,
			// 4 = 32-bit RGBA), converting each pixel in a single pass with no intermediate ARGB array.
			// dst must point at w*h*bpp writable bytes. The cross-platform lock/translate scaffolding
			// mirrors ImageFinder so the two stay consistent across backends.
			private static unsafe void WritePixelData(Bitmap bmp, byte* dst, int bpp)
			{
				int w = bmp.Width, h = bmp.Height;
#if WINDOWS
				var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

				try
				{
					// Format32bppArgb stores BGRA in memory, so a little-endian uint read yields 0xAARRGGBB.
					var basePtr = (byte*)data.Scan0;

					for (var y = 0; y < h; y++)
					{
						var row = (uint*)(basePtr + (nint)y * data.Stride);
						var dstRow = y * w;

						for (var x = 0; x < w; x++)
							EmitPixel(dst, dstRow + x, row[x], bpp);
					}
				}
				finally
				{
					bmp.UnlockBits(data);
				}

#else
				// Force 32bpp first so the 4-byte reads are valid (Pixbuf is 3bpp for 24-bit images). A 3bpp
				// source becomes opaque RGBA; an already-4bpp bitmap (possibly premultiplied, with real alpha)
				// passes through unchanged, so the per-pixel translate below still handles its premultiplication.
				var bmp32 = ImageHelper.EnsureOpaque32Bpp(bmp);

				try
				{
					using var data = bmp32.Lock();
					var basePtr = (byte*)data.Data;
					var stride = data.ScanWidth;
					var srcBpp = data.BytesPerPixel;

					// TranslateDataToArgb is a per-pixel virtual call reordering the backend's channel layout
					// (Gtk RGBA vs Cocoa BGRA) AND un-premultiplying alpha. Skip it only when it is a genuine
					// no-op. Probe once with a partially-transparent marker whose four bytes are all distinct: any
					// channel swap changes it, and any un-premultiplication changes the RGB of a non-opaque pixel,
					// so an unchanged result proves the stored layout already IS straight 0xAARRGGBB and the
					// per-pixel call is redundant. An A=255 marker would hide premultiplication — and a 4bpp bitmap
					// with real alpha reaches here un-forced, since EnsureOpaque32Bpp only converts 3bpp storage.
					const int marker = unchecked((int)0x80112233);
					var identity = srcBpp == 4 && data.TranslateDataToArgb(marker) == marker;

					for (var y = 0; y < h; y++)
					{
						var row = basePtr + (long)y * stride;
						var dstRow = y * w;

						for (var x = 0; x < w; x++)
						{
							var raw = *(int*)(row + x * srcBpp);
							EmitPixel(dst, dstRow + x, (uint)(identity ? raw : data.TranslateDataToArgb(raw)), bpp);
						}
					}
				}
				finally
				{
					if (!ReferenceEquals(bmp32, bmp))
						bmp32.Dispose();
				}

#endif
			}

			// Writes one 0xAARRGGBB pixel into dst at pixel index idx in the bpp layout: grayscale uses the
			// integer luminance 0.30R+0.59G+0.11B; RGBA writes R,G,B,A in that byte order. Aggressively inlined
			// to remove the call overhead; the bpp test then runs inline per pixel but is perfectly predicted
			// (constant for the whole call), so it costs effectively nothing.
			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			private static unsafe void EmitPixel(byte* dst, int idx, uint p, int bpp)
			{
				if (bpp == 1)
				{
					uint r = (p >> 16) & 0xFF, g = (p >> 8) & 0xFF, b = p & 0xFF;
					dst[idx] = (byte)((r * 77 + g * 150 + b * 29) >> 8);
				}
				else
				{
					var o = idx * 4;
					dst[o]     = (byte)((p >> 16) & 0xFF); // R
					dst[o + 1] = (byte)((p >> 8) & 0xFF);  // G
					dst[o + 2] = (byte)(p & 0xFF);         // B
					dst[o + 3] = (byte)((p >> 24) & 0xFF); // A
				}
			}

			// Captures a screen rectangle and wraps it, recording the HiDPI capture scale. Coordinates
			// are always absolute screen coordinates; the Pixel CoordMode is deliberately not applied
			// (see FromRect) so every capture factory shares one coordinate convention.
			private static object CaptureRect(int x, int y, int w, int h, string failMsg)
			{
				var bmp = GuiHelper.GetScreen(x, y, w, h);

				if (bmp == null)
					return Errors.ErrorOccurred(failMsg);

				double sx = w > 0 ? (double)bmp.Width / w : 1.0;
				double sy = h > 0 ? (double)bmp.Height / h : 1.0;
				return Wrap(bmp, sx, sy, originX: x, originY: y);
			}

			private static object Wrap(Bitmap bmp, double sx = 1.0, double sy = 1.0, string failMsg = null, int originX = 0, int originY = 0)
			{
				if (bmp == null)
					return Errors.ValueErrorOccurred(failMsg ?? "Failed to create the image.");

				return new KeysharpImage { baseBitmap = bmp, scaleX = sx, scaleY = sy, originX = originX, originY = originY };
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

			// Applies base + queued transforms, caching the result until the next Invalidate(). With no queued
			// transforms the base bitmap IS the image, so it is handed back directly (no clone) — callers treat
			// the result as read-only (SetPixel mutates it in place by design). Otherwise the transforms run
			// starting from the base itself: each returns a NEW bitmap (or the same instance for a no-op) and
			// never mutates its input, so the base needs no protective up-front copy. `current` is only ever
			// disposed once it is a transform-produced bitmap, never while it is still the base.
			private Bitmap Materialize()
			{
				if (disposed || baseBitmap == null)
					return null;

				if (cached != null)
					return cached;

				if (pending.Count == 0)
					return baseBitmap;

				var current = baseBitmap;

				try
				{
					foreach (var op in pending)
					{
						var next = op(current);

						if (next == null || ReferenceEquals(next, current))
							continue;

						if (!ReferenceEquals(current, baseBitmap))
							current.Dispose();

						current = next;
					}
				}
				catch
				{
					// A transform threw partway through; don't leak the work-in-progress bitmap (but never the
					// base), and leave `cached` null so a later call can retry cleanly.
					if (!ReferenceEquals(current, baseBitmap))
						current?.Dispose();

					throw;
				}

				// Every queued transform was a no-op on this base: hand back a distinct, owned copy so `cached`
				// stays disposable independently of the base (Invalidate/SetPixel rely on that).
				if (ReferenceEquals(current, baseBitmap))
					current = new Bitmap(baseBitmap);

				cached = current;
				return cached;
			}

			private void Invalidate()
			{
				cached?.Dispose();
				cached = null;
			}

			// Formats the capture scale for the window title as a percentage: a single value when X and Y
			// match (the usual case), otherwise "sx%/sy%". 1.0 -> "100%", 2.0 -> "200%", 1.5 -> "150%".
			private static string FormatScalePercent(double sx, double sy)
			{
				static string P(double v) => $"{(long)Math.Round(v * 100)}%";
				return sx == sy ? P(sx) : $"{P(sx)}/{P(sy)}";
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
