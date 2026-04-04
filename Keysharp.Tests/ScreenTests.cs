using static Keysharp.Builtins.Misc;
using static Keysharp.Builtins.Mouse;
using static Keysharp.Builtins.Screen;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Keysharp.Tests
{
	public partial class Screentests : TestRunner
	{
		[Test, Category("Screen")]
		public void ImageSearch()
		{
			if (Script.IsHeadless)
				Assert.Ignore("ImageSearch requires a non-headless GUI session.");

			_ = CoordMode("Pixel", "Screen");
			var screenWidth = A_ScreenWidth.Ai();
			var screenHeight = A_ScreenHeight.Ai();
			_ = ImageCapture(10, 10, 500, 500, "./imagesearch.bmp");
			VarRef x = new(null), y = new(null);
			_ = Builtins.Screen.ImageSearch(x, y, 0, 0, screenWidth, screenHeight, "./imagesearch.bmp");

			if (x.__Value is long lx && lx == 10 && y.__Value is long ly && ly == 10)
				Assert.IsTrue(true);
			else
				Assert.IsTrue(false);

			Assert.IsTrue(TestScript("screen-imagesearch", false));
		}

		[Test, Category("Screen")]
		public void PixelGetColor()
		{
			if (Script.IsHeadless)
				Assert.Ignore("PixelGetColor requires a non-headless GUI session.");

			int last = 0, white = 0xffffff, black = 0x000000;
			_ = CoordMode("Pixel", "Screen");
			var screenWidth = A_ScreenWidth.Ai();
			var screenHeight = A_ScreenHeight.Ai();

			for (var i = 0; i < screenHeight; i++)
			{
				for (var j = 0; j < screenWidth; j++)
				{
					var pix = Builtins.Screen.PixelGetColor(j, i);
					Assert.IsTrue(int.TryParse(pix.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var ii));

					if (ii != last && ii != white && ii != black)
						goto pass;

					last = ii;
				}
			}

			Assert.IsTrue(false);
			pass:
			Assert.IsTrue(true);
			Assert.IsTrue(TestScript("screen-pixelgetcolor", true));
		}

		[Test, Category("Screen")]
		public void PixelSearch()
		{
			if (Script.IsHeadless)
				Assert.Ignore("PixelSearch requires a non-headless GUI session.");

			int last = 0, white = 0xffffff, black = 0x000000;
			_ = CoordMode("Pixel", "Screen");
			var screenWidth = A_ScreenWidth.Ai();
			var screenHeight = A_ScreenHeight.Ai();

			for (var i = 0; i < screenHeight; i++)
			{
				for (var j = 0; j < screenWidth; j++)
				{
					var pix = Builtins.Screen.PixelGetColor(j, i);
					Assert.IsTrue(int.TryParse(pix.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var ii));

					if (ii != last && ii != white && ii != black)
					{
						VarRef outX = new(null);
						VarRef outY = new(null);
						var ret = Builtins.Screen.PixelSearch(outX, outY, j, i, j + 1, i + 1, pix);

						if (ret == 1L && (long)outX.__Value == j && (long)outY.__Value == i)
							goto pass;
						else
							goto fail;
					}

					last = ii;
				}
			}

			pass:
			Assert.IsTrue(true);
			Assert.IsTrue(TestScript("screen-pixelsearch", true));
			return;
			fail:
			Assert.IsTrue(false);
		}
	}
}
