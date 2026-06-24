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
