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
				Assert.AreEqual(0x123456L, (long)img.GetPixel(3, 4));
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
				var result = img.Search(needle);
				Assert.IsInstanceOf<Keysharp.Builtins.Array>(result, "Search should return a match array.");
				var arr = (Keysharp.Builtins.Array)result;
				Assert.AreEqual(10L, arr[1L]);
				Assert.AreEqual(3L, arr[2L]);
			}
			finally { File.Delete(haystack); File.Delete(needle); }
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
				Assert.AreEqual(0xAB12CDL, (long)img.GetPixel(17, 3));
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
	}
}
