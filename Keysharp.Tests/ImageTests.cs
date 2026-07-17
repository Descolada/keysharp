using Keysharp.Internals.Images;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Keysharp.Tests
{
	public partial class ImageTests : TestRunner
	{
		// Paints a deterministic pattern (pixel (x,y) -> R=x, G=y, B=fixed) into a new PNG file and
		// returns its path. Uses the same bitmap pipeline (NewArgbCanvas + SaveBitmap) the Image class
		// relies on, so a region painted here is byte-identical to a needle painted the same way.
		private static string MakeImageFile(int w, int h, int blue = 0x40)
		{
			var bmp = ImageHelper.NewArgbCanvas(w, h);

			for (var y = 0; y < h; y++)
				for (var x = 0; x < w; x++)
					bmp.SetPixel(x, y, ImageHelper.ArgbToColor(unchecked((int)(0xFF000000u | (uint)((x & 0xFF) << 16) | (uint)((y & 0xFF) << 8) | (uint)blue))));

			var path = Path.Combine(Path.GetTempPath(), $"ks_img_{Guid.NewGuid():N}.png");
			ImageHelper.SaveBitmap(bmp, path);
			bmp.Dispose();
			return path;
		}

		private static long RgbaByte(KeysharpImage img, int x, int y, int channel)
		{
			var rgba = img.GetPixelData(4L) as Keysharp.Builtins.Buffer;
			var offset = ((y * (int)img.Width + x) * 4) + channel + 1;
			return (long)rgba[offset];
		}

		// A &match ByRef backed by a captured local; the current written value is read back via .__Value.
		private static Keysharp.Builtins.VarRef MakeRef()
		{
			object v = null;
			return new Keysharp.Builtins.VarRef(() => v, x => v = x);
		}

		// Reads a named own-property (e.g. "x"/"y"/"color") off a search-match object as a long.
		private static long MProp(object matchObj, string name) => (long)Script.GetPropertyValueOrNull(matchObj, name);

		// A NON-Buffer object exposing Ptr + Size own-properties backed by a real Buffer's memory, to prove the
		// FromBuffer/SetPixelData duck-typing (any object with Ptr+Size works, not only a Buffer).
		private static Keysharp.Builtins.KeysharpObject PtrSizeView(Keysharp.Builtins.Buffer buf)
		{
			var o = new Keysharp.Builtins.KeysharpObject();
			o.DefinePropInternal("Ptr", new Keysharp.Builtins.OwnPropsDesc(o, buf.Ptr));
			o.DefinePropInternal("Size", new Keysharp.Builtins.OwnPropsDesc(o, buf.Size));
			return o;
		}

		[Test, Category("Image")]
		public void ImageFromFileDimensions()
		{
			if (Script.IsHeadless)
				Assert.Ignore("Image tests need an initialized graphics backend.");

			var path = MakeImageFile(20, 10);

			try
			{
				var img = KeysharpImage.FromFile(null,path) as KeysharpImage;
				Assert.IsNotNull(img);
				Assert.AreEqual(20L, img.Width);
				Assert.AreEqual(10L, img.Height);
			}
			finally { File.Delete(path); }
		}

		[Test, Category("Image")]
		public void ImageCreateTransparentAndColoredCanvas()
		{
			if (Script.IsHeadless)
				Assert.Ignore("Image tests need an initialized graphics backend.");

			var transparent = KeysharpImage.Create(null, 8, 6) as KeysharpImage;
			Assert.IsNotNull(transparent);
			Assert.AreEqual(8L, transparent.Width);
			Assert.AreEqual(6L, transparent.Height);
			Assert.AreEqual(0L, RgbaByte(transparent, 0, 0, 3));

			var colored = KeysharpImage.Create(null, 4, 3, "0xFF445566") as KeysharpImage;
			Assert.AreEqual(0x44L, RgbaByte(colored, 0, 0, 0));
			Assert.AreEqual(0x55L, RgbaByte(colored, 0, 0, 1));
			Assert.AreEqual(0x66L, RgbaByte(colored, 0, 0, 2));

			var alpha = KeysharpImage.Create(null, 4, 3, "0x80445566") as KeysharpImage;
			Assert.AreEqual(0x80L, RgbaByte(alpha, 0, 0, 3));
		}

		[Test, Category("Image")]
		public void ImageDrawingIsLazyAndCopyIndependent()
		{
			if (Script.IsHeadless)
				Assert.Ignore("Image tests need an initialized graphics backend.");

			var img = KeysharpImage.Create(null, 8, 6) as KeysharpImage;
			var copy = img.Copy() as KeysharpImage;
			Assert.AreSame(img, img.FillRect(1, 1, 3, 2, "Red"));
			Assert.AreEqual(0xFFFF0000L, (long)img.GetPixel(1, 1));
			Assert.AreEqual(255L, RgbaByte(img, 1, 1, 3));
			Assert.AreEqual(0L, RgbaByte(copy, 1, 1, 3));
		}

		[Test, Category("Image")]
		public void ImageDrawingOrderAndShapes()
		{
			if (Script.IsHeadless)
				Assert.Ignore("Image tests need an initialized graphics backend.");

			var img = KeysharpImage.Create(null, 12, 12) as KeysharpImage;
			_ = img.FillRect(1, 1, 8, 8, "Red");
			_ = img.FillEllipse(4, 4, 6, 6, "Blue");
			_ = img.DrawRect(0, 0, 12, 12, "Lime", 1);
			Assert.AreEqual(0xFF0000FFL, (long)img.GetPixel(6, 6));
			Assert.AreEqual(0xFF00FF00L, (long)img.GetPixel(0, 0));
		}

		[Test, Category("Image")]
		public void ImageDrawTextRendersAlpha()
		{
			if (Script.IsHeadless)
				Assert.Ignore("Image tests need an initialized graphics backend.");

			var img = KeysharpImage.Create(null, 100, 40) as KeysharpImage;
			_ = img.DrawText("Hi", 2, 2, "Black", "Sans 14");
			var rgba = img.GetPixelData(4L) as Keysharp.Builtins.Buffer;
			var anyAlpha = false;

			for (long i = 4; i <= (long)rgba.Size; i += 4)
			{
				if ((long)rgba[i] != 0)
				{
					anyAlpha = true;
					break;
				}
			}

			Assert.IsTrue(anyAlpha, "DrawText should produce non-transparent pixels.");
		}

		[Test, Category("Image")]
		public void ImageDrawImageComposes()
		{
			if (Script.IsHeadless)
				Assert.Ignore("Image tests need an initialized graphics backend.");

			var src = KeysharpImage.Create(null, 3, 3) as KeysharpImage;
			_ = src.FillRect(0, 0, 3, 3, "0xFF112233");
			var dst = KeysharpImage.Create(null, 8, 8) as KeysharpImage;
			_ = dst.DrawImage(src, 2, 1);
			Assert.AreEqual(0xFF112233L, (long)dst.GetPixel(3, 2));
			Assert.AreEqual(0xFFL, RgbaByte(dst, 3, 2, 3));
		}

		[Test, Category("Image")]
		public void ImageScaleIsLazyAndChains()
		{
			if (Script.IsHeadless)
				Assert.Ignore("Image tests need an initialized graphics backend.");

			var path = MakeImageFile(20, 10);

			try
			{
				var img = KeysharpImage.FromFile(null,path) as KeysharpImage;
				// Scale returns the same instance (fluent) and applies multiplicatively.
				Assert.AreSame(img, img.Scale(2));
				Assert.AreEqual(40L, img.Width);
				Assert.AreEqual(20L, img.Height);
				// A second scale stacks on the first.
				_ = img.Scale(0.5, 1);
				Assert.AreEqual(20L, img.Width);
				Assert.AreEqual(20L, img.Height);
			}
			finally { File.Delete(path); }
		}

		[Test, Category("Image")]
		public void ImageCropDimensions()
		{
			if (Script.IsHeadless)
				Assert.Ignore("Image tests need an initialized graphics backend.");

			var path = MakeImageFile(20, 10);

			try
			{
				var img = KeysharpImage.FromFile(null,path) as KeysharpImage;
				_ = img.Crop(5, 2, 8, 4);
				Assert.AreEqual(8L, img.Width);
				Assert.AreEqual(4L, img.Height);
			}
			finally { File.Delete(path); }
		}

		[Test, Category("Image")]
		public void ImageScaleFoldsIntoScaleX()
		{
			if (Script.IsHeadless)
				Assert.Ignore("Image tests need an initialized graphics backend.");

			var path = MakeImageFile(20, 10);

			try
			{
				// Scaling multiplies the pixels-per-logical-unit density so consumers (e.g. OCR) can divide
				// image coordinates by ScaleX/ScaleY to recover logical units after an upscale.
				var img = KeysharpImage.FromFile(null,path) as KeysharpImage;
				Assert.AreEqual(1.0, img.ScaleX);
				Assert.AreEqual(1.0, img.ScaleY);
				_ = img.Scale(2);
				Assert.AreEqual(2.0, img.ScaleX);
				Assert.AreEqual(2.0, img.ScaleY);
				// A second, anisotropic scale stacks multiplicatively on each axis.
				_ = img.Scale(0.5, 1);
				Assert.AreEqual(1.0, img.ScaleX);
				Assert.AreEqual(2.0, img.ScaleY);
			}
			finally { File.Delete(path); }
		}

		[Test, Category("Image")]
		public void ImageCropShiftsOrigin()
		{
			if (Script.IsHeadless)
				Assert.Ignore("Image tests need an initialized graphics backend.");

			var path = MakeImageFile(20, 10);

			try
			{
				// Cropping moves the image's top-left, so its screen origin shifts by the crop offset (in
				// logical units). At ScaleX/Y == 1 the shift equals the crop offset directly.
				var img = KeysharpImage.FromFile(null,path) as KeysharpImage;
				Assert.AreEqual(0L, img.X);
				Assert.AreEqual(0L, img.Y);
				_ = img.Crop(5, 2, 8, 4);
				Assert.AreEqual(5L, img.X);
				Assert.AreEqual(2L, img.Y);

				// Cropping AFTER a scale must divide the crop offset (in image pixels) by the now-2x
				// ScaleX/ScaleY to get logical units — the path a ScaleX==1 crop never exercises.
				var scaled = KeysharpImage.FromFile(null,path) as KeysharpImage;
				_ = scaled.Scale(2);
				_ = scaled.Crop(4, 2, 6, 4);
				Assert.AreEqual(2L, scaled.X);   // 4 image px / ScaleX 2 = 2 logical
				Assert.AreEqual(1L, scaled.Y);   // 2 image px / ScaleY 2 = 1 logical
			}
			finally { File.Delete(path); }
		}

		[Test, Category("Image")]
		public void ImageCopyIsIndependent()
		{
			if (Script.IsHeadless)
				Assert.Ignore("Image tests need an initialized graphics backend.");

			var path = MakeImageFile(20, 10);

			try
			{
				// Copy() carries over pixels + scale/origin metadata, but later transforms on the copy must
				// not touch the original (the contract OCR relies on when it transforms a caller's image).
				var img = KeysharpImage.FromFile(null,path) as KeysharpImage;
				var copy = img.Copy() as KeysharpImage;
				Assert.IsNotNull(copy);
				Assert.AreEqual(20L, copy.Width);
				Assert.AreEqual(1.0, copy.ScaleX);
				// Same crop-then-scale order OCR.__ApplyTransforms uses: crop shifts the origin by 2 (at
				// ScaleX 1), then the scale folds into ScaleX and grows the width.
				_ = copy.Crop(2, 1, 10, 5);
				_ = copy.Scale(2);
				// Copy reflects the transforms...
				Assert.AreEqual(2.0, copy.ScaleX);
				Assert.AreEqual(2L, copy.X);
				Assert.AreEqual(20L, copy.Width);
				// ...the original is untouched.
				Assert.AreEqual(20L, img.Width);
				Assert.AreEqual(10L, img.Height);
				Assert.AreEqual(1.0, img.ScaleX);
				Assert.AreEqual(0L, img.X);
			}
			finally { File.Delete(path); }
		}

		[Test, Category("Image")]
		public void ImageGetPixelDataLayout()
		{
			if (Script.IsHeadless)
				Assert.Ignore("Image tests need an initialized graphics backend.");

			// MakeImageFile paints pixel (x,y) -> R=x, G=y, B=blue. Use a tiny known canvas and verify the
			// two GetPixelData layouts byte-for-byte: 4 = R,G,B,A in that order; 1 = integer luminance
			// (r*77 + g*150 + b*29) >> 8. This is the substrate OCR feeds on (GetPixelData(1)) and is exactly
			// the channel-order/luminance code that silently breaks across image backends. Buffer's indexer
			// is 1-based, so byte n of pixel `pix` (0-based) in a `bpp`-byte layout is buf[pix*bpp + n + 1].
			const int blue = 0x40;
			var path = MakeImageFile(4, 3, blue);

			try
			{
				var img = KeysharpImage.FromFile(null, path) as KeysharpImage;

				// 4bpp: R,G,B,A. Check the first pixel (0,0) and the last (3,2).
				var rgba = img.GetPixelData(4L) as Keysharp.Builtins.Buffer;
				Assert.IsNotNull(rgba);
				Assert.AreEqual(4L * 3 * 4, rgba.Size);
				// pixel (0,0) -> R=0, G=0, B=blue, A=255
				Assert.AreEqual(0L, rgba[1]);
				Assert.AreEqual(0L, rgba[2]);
				Assert.AreEqual((long)blue, rgba[3]);
				Assert.AreEqual(255L, rgba[4]);
				// pixel (3,2): pix index = 2*4 + 3 = 11, byte base = 11*4 = 44 -> R=3, G=2, B=blue, A=255
				Assert.AreEqual(3L, rgba[45]);
				Assert.AreEqual(2L, rgba[46]);
				Assert.AreEqual((long)blue, rgba[47]);
				Assert.AreEqual(255L, rgba[48]);

				// 1bpp grayscale luminance.
				var gray = img.GetPixelData(1L) as Keysharp.Builtins.Buffer;
				Assert.IsNotNull(gray);
				Assert.AreEqual(4L * 3, gray.Size);
				// (0,0): (0*77 + 0*150 + 64*29) >> 8 = 1856 >> 8 = 7
				Assert.AreEqual(7L, gray[1]);
				// (3,2): (3*77 + 2*150 + 64*29) >> 8 = 2387 >> 8 = 9 (pix index 11 -> buf[12])
				Assert.AreEqual(9L, gray[12]);

				// Only 1 and 4 are valid layouts; anything else is a ValueError.
				Assert.Throws<Keysharp.Builtins.KeysharpException>(() => img.GetPixelData(2L));
				Assert.Throws<Keysharp.Builtins.KeysharpException>(() => img.GetPixelData(3L));
			}
			finally { File.Delete(path); }
		}

		[Test, Category("Image")]
		public void ImageRotate90SwapsDimensions()
		{
			if (Script.IsHeadless)
				Assert.Ignore("Image tests need an initialized graphics backend.");

			var path = MakeImageFile(20, 10);

			try
			{
				var img = KeysharpImage.FromFile(null,path) as KeysharpImage;
				_ = img.Rotate(90);
				Assert.AreEqual(10L, img.Width);
				Assert.AreEqual(20L, img.Height);
			}
			finally { File.Delete(path); }
		}

		[Test, Category("Image")]
		public void ImageFlipMirrorsPixels()
		{
			if (Script.IsHeadless)
				Assert.Ignore("Image tests need an initialized graphics backend.");

			var path = MakeImageFile(20, 10);

			try
			{
				var img = KeysharpImage.FromFile(null,path) as KeysharpImage;
				var leftBefore = (long)img.GetPixel(0, 0);
				var rightBefore = (long)img.GetPixel(19, 0);
				_ = img.Flip();   // horizontal by default
				Assert.AreEqual(20L, img.Width);
				Assert.AreEqual(10L, img.Height);
				// After a horizontal flip the former rightmost pixel is now leftmost.
				Assert.AreEqual(rightBefore, (long)img.GetPixel(0, 0));
				Assert.AreEqual(leftBefore, (long)img.GetPixel(19, 0));
			}
			finally { File.Delete(path); }
		}

		[Test, Category("Image")]
		public void ImageGetSetPixelRoundTrips()
		{
			if (Script.IsHeadless)
				Assert.Ignore("Image tests need an initialized graphics backend.");

			var path = MakeImageFile(8, 8);

			try
			{
				var img = KeysharpImage.FromFile(null,path) as KeysharpImage;
				_ = img.SetPixel(3, 4, 0x123456L);
				Assert.AreEqual(0xFF123456L, (long)img.GetPixel(3, 4));
			}
			finally { File.Delete(path); }
		}

		[Test, Category("Image")]
		public void ImageSaveRoundTripsDimensions()
		{
			if (Script.IsHeadless)
				Assert.Ignore("Image tests need an initialized graphics backend.");

			var src = MakeImageFile(20, 10);
			var dst = Path.Combine(Path.GetTempPath(), $"ks_img_{Guid.NewGuid():N}.png");

			try
			{
				// Instance methods return object (dynamic chaining works in scripts, but C# must call
				// each on the typed instance), so apply the transform then save in two steps.
				var img = KeysharpImage.FromFile(null,src) as KeysharpImage;
				_ = img.Scale(2);
				_ = img.Save(dst);
				Assert.IsTrue(File.Exists(dst));
				var reloaded = KeysharpImage.FromFile(null,dst) as KeysharpImage;
				Assert.AreEqual(40L, reloaded.Width);
				Assert.AreEqual(20L, reloaded.Height);
			}
			finally { File.Delete(src); File.Delete(dst); }
		}

		[Test, Category("Image")]
		public void ImageSearchFindsSubImage()
		{
			if (Script.IsHeadless)
				Assert.Ignore("Image tests need an initialized graphics backend.");

			// Haystack carries the MakeImageFile pattern; the needle is the byte-identical 6x6 region
			// at (10, 3), so an exact (variation 0) search must locate it there.
			var haystack = MakeImageFile(30, 20);
			var needle = Path.Combine(Path.GetTempPath(), $"ks_img_{Guid.NewGuid():N}.png");

			try
			{
				var needleBmp = ImageHelper.NewArgbCanvas(6, 6);

				for (var y = 0; y < 6; y++)
					for (var x = 0; x < 6; x++)
						needleBmp.SetPixel(x, y, ImageHelper.ArgbToColor(unchecked((int)(0xFF000000u | (uint)(((10 + x) & 0xFF) << 16) | (uint)(((3 + y) & 0xFF) << 8) | 0x40u))));

				ImageHelper.SaveBitmap(needleBmp, needle);
				needleBmp.Dispose();

				var img = KeysharpImage.FromFile(null,haystack) as KeysharpImage;
				var match = MakeRef();
				var found = img.Search(match, needle);
				Assert.AreEqual(1L, found, "Search should return true on a hit.");
				Assert.AreEqual(10L, MProp(match.__Value, "x"));
				Assert.AreEqual(3L, MProp(match.__Value, "y"));

				// A miss returns false and leaves &match falsy ("").
				var noMatch = KeysharpImage.Create(null, 6, 6, "Magenta") as KeysharpImage;
				var miss = MakeRef();
				Assert.AreEqual(0L, img.Search(miss, noMatch));
				Assert.AreEqual("", miss.__Value);
			}
			finally { File.Delete(haystack); File.Delete(needle); }
		}

		[Test, Category("Image")]
		public void ImageSearchHonorsTransAndDirection()
		{
			if (Script.IsHeadless)
				Assert.Ignore("Image tests need an initialized graphics backend.");

			// Two identical solid-red 3x3 blocks at (2,2) and (10,6) in a black haystack. The default
			// scan (Dir1, row-major) must find the top-left one; Dir4 (right-to-left, bottom-to-top)
			// must find the bottom-right one. With trans=Red every needle pixel is a wildcard, so the
			// match lands at the scan origin (0,0).
			var img = KeysharpImage.Create(null, 20, 12, "Black") as KeysharpImage;
			_ = img.FillRect(2, 2, 3, 3, "Red");
			_ = img.FillRect(10, 6, 3, 3, "Red");
			var needle = KeysharpImage.Create(null, 3, 3, "Red") as KeysharpImage;

			var first = MakeRef();
			Assert.AreEqual(1L, img.Search(first, needle));
			Assert.AreEqual(2L, MProp(first.__Value, "x"));
			Assert.AreEqual(2L, MProp(first.__Value, "y"));

			var last = MakeRef();
			Assert.AreEqual(1L, img.Search(last, needle, 0, null, 4));
			Assert.AreEqual(10L, MProp(last.__Value, "x"));
			Assert.AreEqual(6L, MProp(last.__Value, "y"));

			var wild = MakeRef();
			Assert.AreEqual(1L, img.Search(wild, needle, 0, "Red"));
			Assert.AreEqual(0L, MProp(wild.__Value, "x"));
			Assert.AreEqual(0L, MProp(wild.__Value, "y"));
		}

		[Test, Category("Image")]
		public void ImageSearchPixelFindsColor()
		{
			if (Script.IsHeadless)
				Assert.Ignore("Image tests need an initialized graphics backend.");

			var img = KeysharpImage.Create(null, 16, 10, "Black") as KeysharpImage;
			_ = img.SetPixel(7, 4, 0x30A060L);

			var hit = MakeRef();
			Assert.AreEqual(1L, img.SearchPixel(hit, 0x30A060L));
			Assert.AreEqual(7L, MProp(hit.__Value, "x"));
			Assert.AreEqual(4L, MProp(hit.__Value, "y"));
			// match.color is the ACTUAL matched pixel's full ARGB (opaque here, so 0xFF-prefixed).
			Assert.AreEqual(0xFF30A060L, MProp(hit.__Value, "color"));

			// Within variation: a nearby color still matches; far off does not (returns false + falsy match).
			var near = MakeRef();
			Assert.AreEqual(1L, img.SearchPixel(near, 0x32A262L, 4));
			Assert.AreEqual(7L, MProp(near.__Value, "x"));

			var none = MakeRef();
			Assert.AreEqual(0L, img.SearchPixel(none, 0xFFFFFFL));
			Assert.AreEqual("", none.__Value);

			// 3 or 4 args (neither whole-image nor region) is a ValueError.
			Assert.Throws<Keysharp.Builtins.KeysharpException>(() => img.SearchPixel(MakeRef(), 1L, 2L, 3L));
			Assert.Throws<Keysharp.Builtins.KeysharpException>(() => img.SearchPixel(MakeRef(), 1L, 2L, 3L, 4L));
		}

		[Test, Category("Image")]
		public void ImageRoundRectDrawsCorners()
		{
			if (Script.IsHeadless)
				Assert.Ignore("Image tests need an initialized graphics backend.");

			var img = KeysharpImage.Create(null, 24, 24) as KeysharpImage;
			_ = img.FillRoundRect(0, 0, 24, 24, 8, "Red");
			// Centre and edge midpoints are inside the pill; the extreme corner is outside (transparent).
			Assert.AreEqual(0xFFFF0000L, (long)img.GetPixel(12, 12));
			Assert.AreEqual(0xFFFF0000L, (long)img.GetPixel(12, 0));
			Assert.AreEqual(0L, RgbaByte(img, 0, 0, 3));

			// Radius 0 degrades to a plain rectangle: the corner IS filled.
			var square = KeysharpImage.Create(null, 10, 10) as KeysharpImage;
			_ = square.FillRoundRect(0, 0, 10, 10, 0, "Red");
			Assert.AreEqual(255L, RgbaByte(square, 0, 0, 3));
		}

		[Test, Category("Image")]
		public void ImageDrawTextParsesStyledFontSpec()
		{
			if (Script.IsHeadless)
				Assert.Ignore("Image tests need an initialized graphics backend.");

			// Styled specs must parse (family with spaces + size + style keywords) and render pixels.
			var img = KeysharpImage.Create(null, 120, 40) as KeysharpImage;
			_ = img.DrawText("Hi", 2, 2, "Black", "Sans 14 bold italic");
			var rgba = img.GetPixelData(4L) as Keysharp.Builtins.Buffer;
			var anyAlpha = false;

			for (long i = 4; i <= (long)rgba.Size; i += 4)
			{
				if ((long)rgba[i] != 0)
				{
					anyAlpha = true;
					break;
				}
			}

			Assert.IsTrue(anyAlpha, "Styled DrawText should produce non-transparent pixels.");
		}

		[Test, Category("Image")]
		public void ImageFromFileNegativeDimensionPreservesAspect()
		{
			if (Script.IsHeadless)
				Assert.Ignore("Image tests need an initialized graphics backend.");

			var path = MakeImageFile(20, 10);

			try
			{
				// w=-1 with h=30 must derive w from the 2:1 aspect ratio (60), not copy h.
				var img = KeysharpImage.FromFile(null, path, -1, 30) as KeysharpImage;
				Assert.AreEqual(60L, img.Width);
				Assert.AreEqual(30L, img.Height);

				var img2 = KeysharpImage.FromFile(null, path, 40, -1) as KeysharpImage;
				Assert.AreEqual(40L, img2.Width);
				Assert.AreEqual(20L, img2.Height);
			}
			finally { File.Delete(path); }
		}

		[Test, Category("Image")]
		public void ImageFromBitmapRoundTrips()
		{
			if (Script.IsHeadless)
				Assert.Ignore("Image tests need an initialized graphics backend.");

			var path = MakeImageFile(16, 12);

			try
			{
				var handle = (KeysharpImage.FromFile(null,path) as KeysharpImage).ToBitmap();
				var img = KeysharpImage.FromBitmap(null, handle) as KeysharpImage;
				Assert.IsNotNull(img);
				Assert.AreEqual(16L, img.Width);
				Assert.AreEqual(12L, img.Height);
			}
			finally { File.Delete(path); }
		}

		[Test, Category("Image")]
		public void ImageSetPixelSurvivesLaterTransform()
		{
			if (Script.IsHeadless)
				Assert.Ignore("Image tests need an initialized graphics backend.");

			var path = MakeImageFile(20, 10);

			try
			{
				var img = KeysharpImage.FromFile(null, path) as KeysharpImage;
				_ = img.SetPixel(2, 3, 0xAB12CDL);
				// Flip is an exact (nearest-neighbour) transform; x=2 maps to 19-2=17. The edit must
				// persist across the transform (regression test for the "Invalidate discards SetPixel" bug).
				_ = img.Flip();
				Assert.AreEqual(0xFFAB12CDL, (long)img.GetPixel(17, 3));
			}
			finally { File.Delete(path); }
		}

		[Test, Category("Image")]
		public void ImageScalePropertiesDefaultToOneForFiles()
		{
			if (Script.IsHeadless)
				Assert.Ignore("Image tests need an initialized graphics backend.");

			var path = MakeImageFile(8, 8);

			try
			{
				var img = KeysharpImage.FromFile(null, path) as KeysharpImage;
				Assert.AreEqual(1.0, img.ScaleX);
				Assert.AreEqual(1.0, img.ScaleY);
			}
			finally { File.Delete(path); }
		}

		[Test, Category("Image")]
		public void ImageGetPixelReturnsAlphaForSemiTransparentSetPixel()
		{
			if (Script.IsHeadless)
				Assert.Ignore("Image tests need an initialized graphics backend.");

			// GetPixel now returns the full 32-bit ARGB, so a semi-transparent SetPixel must round-trip its
			// alpha byte (not just the RGB) rather than reading back as opaque.
			var img = KeysharpImage.Create(null, 8, 6) as KeysharpImage;
			_ = img.SetPixel(2, 3, "0x80112233");
			Assert.AreEqual(0x80112233L, (long)img.GetPixel(2, 3));
		}

		[Test, Category("Image")]
		public void ImageResizeAbsoluteAndAspect()
		{
			if (Script.IsHeadless)
				Assert.Ignore("Image tests need an initialized graphics backend.");

			var path = MakeImageFile(20, 10);   // a 2:1 image

			try
			{
				// Absolute resize to an exact pixel size, chainable, and folding the density into ScaleX/ScaleY.
				var img = KeysharpImage.FromFile(null, path) as KeysharpImage;
				Assert.AreSame(img, img.Resize(40, 20));
				Assert.AreEqual(40L, img.Width);
				Assert.AreEqual(20L, img.Height);
				Assert.AreEqual(2.0, img.ScaleX);   // 40 / 20
				Assert.AreEqual(2.0, img.ScaleY);   // 20 / 10

				// A negative dimension keeps the aspect ratio: Resize(-1, 10) on a 2:1 image -> 20x10.
				var img2 = KeysharpImage.FromFile(null, path) as KeysharpImage;
				_ = img2.Resize(-1, 10);
				Assert.AreEqual(20L, img2.Width);
				Assert.AreEqual(10L, img2.Height);

				// Zero, or both dimensions negative, is a ValueError.
				var img3 = KeysharpImage.FromFile(null, path) as KeysharpImage;
				Assert.Throws<Keysharp.Builtins.KeysharpException>(() => img3.Resize(0, 10));
				Assert.Throws<Keysharp.Builtins.KeysharpException>(() => img3.Resize(-1, -1));
			}
			finally { File.Delete(path); }
		}

		[Test, Category("Image")]
		public void ImageFromBufferRoundTrips()
		{
			if (Script.IsHeadless)
				Assert.Ignore("Image tests need an initialized graphics backend.");

			// Pack a known image (with a semi-transparent pixel) via GetPixelData(4), rebuild it with
			// FromBuffer(4), and confirm dimensions + pixels (including alpha) survive the round-trip.
			var src = KeysharpImage.Create(null, 4, 3, "0xFF204060") as KeysharpImage;
			_ = src.SetPixel(1, 1, "0x80AABBCC");
			var buf = src.GetPixelData(4L) as Keysharp.Builtins.Buffer;

			var rebuilt = KeysharpImage.FromBuffer(null, buf, 4L, 3L, 4L) as KeysharpImage;
			Assert.IsNotNull(rebuilt);
			Assert.AreEqual(4L, rebuilt.Width);
			Assert.AreEqual(3L, rebuilt.Height);
			Assert.AreEqual((long)src.GetPixel(0, 0), (long)rebuilt.GetPixel(0, 0));
			Assert.AreEqual(0x80AABBCCL, (long)rebuilt.GetPixel(1, 1));

			// bpp=1: each gray byte becomes an opaque gray pixel (R==G==B, A=255).
			var gray = src.GetPixelData(1L) as Keysharp.Builtins.Buffer;
			var fromGray = KeysharpImage.FromBuffer(null, gray, 4L, 3L, 1L) as KeysharpImage;
			var gp = (long)fromGray.GetPixel(0, 0);
			Assert.AreEqual(0xFFL, (gp >> 24) & 0xFF);
			Assert.AreEqual((gp >> 16) & 0xFF, (gp >> 8) & 0xFF);
			Assert.AreEqual((gp >> 8) & 0xFF, gp & 0xFF);

			// A buffer too small for the requested dimensions is a ValueError.
			Assert.Throws<Keysharp.Builtins.KeysharpException>(() => KeysharpImage.FromBuffer(null, buf, 8L, 8L, 4L));
		}

		[Test, Category("Image")]
		public void ImageSetPixelDataRoundTrips()
		{
			if (Script.IsHeadless)
				Assert.Ignore("Image tests need an initialized graphics backend.");

			// GetPixelData(4) from a source, SetPixelData into a same-size blank target: pixels must match.
			var src = KeysharpImage.Create(null, 4, 3, "0xFF3366AA") as KeysharpImage;
			_ = src.SetPixel(2, 1, "0x80112233");
			var buf = src.GetPixelData(4L) as Keysharp.Builtins.Buffer;

			var target = KeysharpImage.Create(null, 4, 3) as KeysharpImage;   // fully transparent
			Assert.AreSame(target, target.SetPixelData(buf, 4L));
			Assert.AreEqual((long)src.GetPixel(0, 0), (long)target.GetPixel(0, 0));
			Assert.AreEqual(0x80112233L, (long)target.GetPixel(2, 1));

			// The buffer must be EXACTLY Width*Height*bpp bytes for the current dimensions.
			var wrongSize = KeysharpImage.Create(null, 8, 8) as KeysharpImage;
			Assert.Throws<Keysharp.Builtins.KeysharpException>(() => wrongSize.SetPixelData(buf, 4L));
		}

		[Test, Category("Image")]
		public void ImageBufferDuckTypingAcceptsPtrSizeObject()
		{
			if (Script.IsHeadless)
				Assert.Ignore("Image tests need an initialized graphics backend.");

			// FromBuffer / SetPixelData accept a Buffer OR any object exposing Ptr + Size (AHK duck-typing, like
			// StrGet). Build a NON-Buffer object whose Ptr/Size point at a real Buffer's memory and assert it
			// produces the SAME image as passing the Buffer directly.
			var src = KeysharpImage.Create(null, 4, 3, "0xFF204060") as KeysharpImage;
			_ = src.SetPixel(1, 1, "0x80AABBCC");
			var buf = src.GetPixelData(4L) as Keysharp.Builtins.Buffer;
			var view = PtrSizeView(buf);   // keeps buf referenced (alive) for the duration of the test

			// FromBuffer with the duck-typed object matches FromBuffer with the Buffer, pixel for pixel.
			var fromBuffer = KeysharpImage.FromBuffer(null, buf, 4L, 3L, 4L) as KeysharpImage;
			var fromView = KeysharpImage.FromBuffer(null, view, 4L, 3L, 4L) as KeysharpImage;
			Assert.IsNotNull(fromView);
			Assert.AreEqual(fromBuffer.Width, fromView.Width);
			Assert.AreEqual(fromBuffer.Height, fromView.Height);

			for (var y = 0; y < 3; y++)
				for (var x = 0; x < 4; x++)
					Assert.AreEqual((long)fromBuffer.GetPixel(x, y), (long)fromView.GetPixel(x, y), $"FromBuffer pixel ({x},{y})");

			// SetPixelData with the duck-typed object matches SetPixelData with the Buffer.
			var t1 = KeysharpImage.Create(null, 4, 3) as KeysharpImage;
			var t2 = KeysharpImage.Create(null, 4, 3) as KeysharpImage;
			_ = t1.SetPixelData(buf, 4L);
			_ = t2.SetPixelData(view, 4L);

			for (var y = 0; y < 3; y++)
				for (var x = 0; x < 4; x++)
					Assert.AreEqual((long)t1.GetPixel(x, y), (long)t2.GetPixel(x, y), $"SetPixelData pixel ({x},{y})");

			// An object with neither Ptr nor Size is rejected with a ValueError.
			var bogus = new Keysharp.Builtins.KeysharpObject();
			Assert.Throws<Keysharp.Builtins.KeysharpException>(() => KeysharpImage.FromBuffer(null, bogus, 4L, 3L, 4L));
			Assert.Throws<Keysharp.Builtins.KeysharpException>(() => t1.SetPixelData(bogus, 4L));

			GC.KeepAlive(buf);
		}

		[Test, Category("Image")]
		public void ImageGrayscaleEqualizesChannels()
		{
			if (Script.IsHeadless)
				Assert.Ignore("Image tests need an initialized graphics backend.");

			var img = KeysharpImage.Create(null, 4, 4) as KeysharpImage;
			_ = img.FillRect(0, 0, 4, 4, "0xFF3060A0");
			_ = img.Grayscale();
			var r = RgbaByte(img, 1, 1, 0);
			var g = RgbaByte(img, 1, 1, 1);
			var b = RgbaByte(img, 1, 1, 2);
			Assert.AreEqual(r, g);
			Assert.AreEqual(g, b);
			// gray = round(0.299*0x30 + 0.587*0x60 + 0.114*0xA0) = 89
			Assert.AreEqual(89L, r);
			Assert.AreEqual(255L, RgbaByte(img, 1, 1, 3));   // alpha preserved
		}

		[Test, Category("Image")]
		public void ImageOpacityHalvesAlpha()
		{
			if (Script.IsHeadless)
				Assert.Ignore("Image tests need an initialized graphics backend.");

			var img = KeysharpImage.Create(null, 4, 4, "0xFF112233") as KeysharpImage;
			_ = img.Opacity(0.5);
			Assert.AreEqual(128L, RgbaByte(img, 0, 0, 3));   // round(255 * 0.5) = 128
			// RGB is preserved.
			Assert.AreEqual(0x11L, RgbaByte(img, 0, 0, 0));
			Assert.AreEqual(0x22L, RgbaByte(img, 0, 0, 1));
			Assert.AreEqual(0x33L, RgbaByte(img, 0, 0, 2));
		}

		[Test, Category("Image")]
		public void ImageBrightnessSaturatesToWhite()
		{
			if (Script.IsHeadless)
				Assert.Ignore("Image tests need an initialized graphics backend.");

			var img = KeysharpImage.Create(null, 4, 4, "0xFF204060") as KeysharpImage;
			_ = img.Brightness(1);   // +255 to every RGB channel -> saturates to white
			Assert.AreEqual(255L, RgbaByte(img, 0, 0, 0));
			Assert.AreEqual(255L, RgbaByte(img, 0, 0, 1));
			Assert.AreEqual(255L, RgbaByte(img, 0, 0, 2));
			Assert.AreEqual(255L, RgbaByte(img, 0, 0, 3));   // alpha untouched
		}

		[Test, Category("Image")]
		public void ImageContrastFlattensTo128()
		{
			if (Script.IsHeadless)
				Assert.Ignore("Image tests need an initialized graphics backend.");

			var img = KeysharpImage.Create(null, 4, 4, "0xFF204060") as KeysharpImage;
			_ = img.Contrast(-1);   // factor (1 + amount) = 0 -> every channel collapses to 128
			Assert.AreEqual(128L, RgbaByte(img, 0, 0, 0));
			Assert.AreEqual(128L, RgbaByte(img, 0, 0, 1));
			Assert.AreEqual(128L, RgbaByte(img, 0, 0, 2));
		}

		[Test, Category("Image")]
		public void ImageSearchAllFindsEveryMatch()
		{
			if (Script.IsHeadless)
				Assert.Ignore("Image tests need an initialized graphics backend.");

			// Two identical solid-red 3x3 blocks at (2,2) and (10,6) in a black haystack; a 3x3 red needle must
			// find BOTH, ordered by the default (row-major, top-left) scan direction.
			var img = KeysharpImage.Create(null, 20, 12, "Black") as KeysharpImage;
			_ = img.FillRect(2, 2, 3, 3, "Red");
			_ = img.FillRect(10, 6, 3, 3, "Red");
			var needle = KeysharpImage.Create(null, 3, 3, "Red") as KeysharpImage;

			var matchesRef = MakeRef();
			Assert.AreEqual(1L, img.SearchAll(matchesRef, needle));
			var all = matchesRef.__Value as Keysharp.Builtins.Array;
			Assert.IsNotNull(all);
			Assert.AreEqual(2, all.Count);

			var m1 = all[1L];
			Assert.AreEqual(2L, MProp(m1, "x"));
			Assert.AreEqual(2L, MProp(m1, "y"));

			var m2 = all[2L];
			Assert.AreEqual(10L, MProp(m2, "x"));
			Assert.AreEqual(6L, MProp(m2, "y"));

			// No matches -> returns false and sets &matches to an EMPTY Array (not "").
			var none = KeysharpImage.Create(null, 6, 6, "Blue") as KeysharpImage;
			var emptyRef = MakeRef();
			Assert.AreEqual(0L, none.SearchAll(emptyRef, needle));
			Assert.IsInstanceOf<Keysharp.Builtins.Array>(emptyRef.__Value);
			Assert.AreEqual(0, ((Keysharp.Builtins.Array)emptyRef.__Value).Count);
		}

		[Test, Category("Image")]
		public void ImageSearchRegionFormRestrictsAndReportsAbsolute()
		{
			if (Script.IsHeadless)
				Assert.Ignore("Image tests need an initialized graphics backend.");

			// Two red 3x3 blocks at (2,2) and (12,7) in a black haystack. A region covering only the
			// bottom-right one must find that match and report ABSOLUTE coords (12,7), not region-relative.
			var img = KeysharpImage.Create(null, 24, 16, "Black") as KeysharpImage;
			_ = img.FillRect(2, 2, 3, 3, "Red");
			_ = img.FillRect(12, 7, 3, 3, "Red");
			var needle = KeysharpImage.Create(null, 3, 3, "Red") as KeysharpImage;

			// Region (10,5,10,8) contains only the (12,7) block.
			var m = MakeRef();
			Assert.AreEqual(1L, img.Search(m, 10, 5, 10, 8, needle));
			Assert.AreEqual(12L, MProp(m.__Value, "x"));
			Assert.AreEqual(7L, MProp(m.__Value, "y"));

			// A region around only the top-left block excludes the bottom-right one.
			var m2 = MakeRef();
			Assert.AreEqual(1L, img.Search(m2, 0, 0, 8, 8, needle));
			Assert.AreEqual(2L, MProp(m2.__Value, "x"));
			Assert.AreEqual(2L, MProp(m2.__Value, "y"));

			// SearchAll over the whole image finds both; a region finds only the one inside it.
			var allRef = MakeRef();
			Assert.AreEqual(1L, img.SearchAll(allRef, needle));
			Assert.AreEqual(2, ((Keysharp.Builtins.Array)allRef.__Value).Count);

			var regionRef = MakeRef();
			Assert.AreEqual(1L, img.SearchAll(regionRef, 10, 5, 10, 8, needle));
			var regionArr = (Keysharp.Builtins.Array)regionRef.__Value;
			Assert.AreEqual(1, regionArr.Count);
			Assert.AreEqual(12L, MProp(regionArr[1L], "x"));
			Assert.AreEqual(7L, MProp(regionArr[1L], "y"));

			// SearchPixel region form also reports absolute coordinates.
			var px = MakeRef();
			Assert.AreEqual(1L, img.SearchPixel(px, 10, 5, 10, 8, "Red"));
			Assert.AreEqual(12L, MProp(px.__Value, "x"));
			Assert.AreEqual(7L, MProp(px.__Value, "y"));
		}

		[Test, Category("Image")]
		public void ImageSearchEmptyRegionReturnsNoMatchWithoutThrowing()
		{
			if (Script.IsHeadless)
				Assert.Ignore("Image tests need an initialized graphics backend.");

			// A region that is empty after clamping (origin AT/PAST an edge, or a non-positive size) genuinely
			// contains zero pixels, so every search must return "not found" and must NOT throw. Regression: an
			// empty region used to crop to a degenerate 1x1 transparent canvas that the finder phantom-matched at
			// (0,0); SearchPixel then read GetPixel at the absolute origin (>= Width) and threw. Use a solid-black
			// canvas and black/high-variation searches so a surviving phantom (0,0) would be caught.
			var img = KeysharpImage.Create(null, 10, 8, "Black") as KeysharpImage;
			var needle = KeysharpImage.Create(null, 2, 2, "Black") as KeysharpImage;

			// Origin AT the right edge -> width clamps to 0. This is the exact case that used to throw.
			var p1 = MakeRef();
			long r1 = 1;
			Assert.DoesNotThrow(() => r1 = (long)img.SearchPixel(p1, 10, 0, 4, 4, "Black"));
			Assert.AreEqual(0L, r1);
			Assert.AreEqual("", p1.__Value);

			// Origin PAST the bottom edge, with high variation (a phantom transparent pixel would match).
			var p2 = MakeRef();
			Assert.AreEqual(0L, img.SearchPixel(p2, 0, 8, 4, 4, "Black", 255));
			Assert.AreEqual("", p2.__Value);

			// Non-positive width.
			var p3 = MakeRef();
			Assert.AreEqual(0L, img.SearchPixel(p3, 0, 0, 0, 4, "Black"));
			Assert.AreEqual("", p3.__Value);

			// Search over an empty region -> false + falsy match, no throw.
			var s = MakeRef();
			Assert.AreEqual(0L, img.Search(s, 10, 0, 4, 4, needle));
			Assert.AreEqual("", s.__Value);

			// SearchAll over an empty region -> false + empty array, no throw.
			var a = MakeRef();
			Assert.AreEqual(0L, img.SearchAll(a, 0, 8, 4, 4, needle));
			Assert.IsInstanceOf<Keysharp.Builtins.Array>(a.__Value);
			Assert.AreEqual(0, ((Keysharp.Builtins.Array)a.__Value).Count);
		}

		[Test, Category("Image")]
		public void ImageDisposedThrows()
		{
			if (Script.IsHeadless)
				Assert.Ignore("Image tests need an initialized graphics backend.");

			var img = KeysharpImage.Create(null, 8, 6) as KeysharpImage;
			_ = img.Dispose();
			// Every public member throws after Dispose (a property and a transform shown here).
			Assert.Throws<Keysharp.Builtins.KeysharpException>(() => { _ = img.Width; });
			Assert.Throws<Keysharp.Builtins.KeysharpException>(() => img.Scale(2));
		}

		[Test, Category("Image")]
		public void ImageMutableDrawImageDoesNotLeakSources()
		{
			if (Script.IsHeadless)
				Assert.Ignore("Image tests need an initialized graphics backend.");

			// A mutable (live Overlay) canvas applies draws EAGERLY, so a repeated DrawImage must dispose each
			// loaded source copy immediately rather than parking it in pendingResources (which Bake never drains
			// on a mutable image) — that was a full-size unmanaged bitmap leaked per DrawImage call.
			var canvas = KeysharpImage.Create(null, 20, 20) as KeysharpImage;
			canvas.mutable = true;
			var src = KeysharpImage.Create(null, 4, 4, "Red") as KeysharpImage;

			for (var i = 0; i < 8; i++)
				_ = canvas.DrawImage(src, 0, 0);

			Assert.AreEqual(0, canvas.PendingResourcesCount);

			// A shape op adds no external resource, so the mutable path also stays clean.
			_ = canvas.FillRect(0, 0, 4, 4, "Blue");
			Assert.AreEqual(0, canvas.PendingResourcesCount);
		}

		[Test, Category("Image")]
		public void ImageCopyCarriesDrawScale()
		{
			if (Script.IsHeadless)
				Assert.Ignore("Image tests need an initialized graphics backend.");

			// A scaled Create() canvas draws logical coordinates through its axis scales; Copy() must carry them over
			// so the copy renders at the same physical scale (previously dropped, which is why Overlay.SetImage
			// had to re-set it by hand).
			var img = KeysharpImage.Create(null, 10, 10, null, 2) as KeysharpImage;
			Assert.AreEqual(2.0, img.drawScaleX);
			Assert.AreEqual(2.0, img.drawScaleY);
			var copy = img.Copy() as KeysharpImage;
			Assert.AreEqual(2.0, copy.drawScaleX);
			Assert.AreEqual(2.0, copy.drawScaleY);
		}
	}
}
