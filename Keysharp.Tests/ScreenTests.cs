﻿using static Keysharp.Core.Mouse;
using static Keysharp.Core.Screen;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Keysharp.Tests
{
	public partial class Screentests : TestRunner
	{
		[Test, Category("Screen")]
		public void ImageSearch()
		{
			CoordMode("Mouse", "Screen");
			var screen = System.Windows.Forms.Screen.PrimaryScreen;
			_ = GetScreenClip(10, 10, 500, 500, "./imagesearch.bmp");
			object x = null, y = null;
			Keysharp.Core.Screen.ImageSearch(ref x, ref y, 0, 0, screen.Bounds.Width, screen.Bounds.Height, "./imagesearch.bmp");//This implies this pattern won't be present before 1000,1000.

			if (x is long lx && lx == 10 && y is long ly && ly == 10)
				Assert.IsTrue(true);
			else
				Assert.IsTrue(false);

			Assert.IsTrue(TestScript("screen-imagesearch", false));
		}

		[Test, Category("Screen")]
		public void PixelGetColor()
		{
			int last = 0, white = 0xffffff, black = 0x000000;
			CoordMode("Mouse", "Screen");

			//There isn't really a sure way to tell whether this is working.
			//So a pseudo-test is to just get pixels until the value is not white or black.
			for (var i = 0; i < System.Windows.Forms.Screen.PrimaryScreen.Bounds.Height; i++)
			{
				for (var j = 0; j < System.Windows.Forms.Screen.PrimaryScreen.Bounds.Width; j++)
				{
					var pix = Keysharp.Core.Screen.PixelGetColor(j, i);
					Assert.IsTrue(int.TryParse(pix.AsSpan(2), NumberStyles.HexNumber, Parser.culture, out var ii));

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
			int last = 0, white = 0xffffff, black = 0x000000;
			CoordMode("Mouse", "Screen");

			//There isn't really a sure way to tell whether this is working.
			//So a pseudo-test is to just get pixels until the value is not white or black.
			for (var i = 0; i < System.Windows.Forms.Screen.PrimaryScreen.Bounds.Height; i++)
			{
				for (var j = 0; j < System.Windows.Forms.Screen.PrimaryScreen.Bounds.Width; j++)
				{
					var pix = Keysharp.Core.Screen.PixelGetColor(j, i);
					Assert.IsTrue(int.TryParse(pix.AsSpan(2), NumberStyles.HexNumber, Parser.culture, out var ii));

					if (ii != last && ii != white && ii != black)
					{
						object outX = null;
						object outY = null;
						var ret = Keysharp.Core.Screen.PixelSearch(ref outX, ref outY, j, i, j + 1, i + 1, pix);

						if (ret == 1L && (long)outX == j && (long)outY == i)
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