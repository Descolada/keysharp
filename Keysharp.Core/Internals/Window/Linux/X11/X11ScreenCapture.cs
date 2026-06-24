#if LINUX
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using Eto.Drawing;

namespace Keysharp.Internals.Window.Linux.X11
{
	/// <summary>
	/// Direct X11 window/screen capture via <c>XGetImage</c>, with optional XComposite redirection so a
	/// window can be captured even when it is occluded by other windows or partially off-screen.
	///
	/// <para>Plain <c>XGetImage</c> on a window only returns valid pixels for the regions that are
	/// currently visible (X11 keeps no backing store by default), so for the occluded case we obtain the
	/// window's off-screen backing pixmap through <c>XCompositeNameWindowPixmap</c> and image that
	/// instead. When a compositing manager is already running (the usual case on Cinnamon, KDE-on-X11,
	/// GNOME-on-X11, Mutter/Muffin, picom, ...) every window is already redirected, so naming the pixmap
	/// is enough; otherwise we redirect the single target window ourselves (Automatic mode keeps the
	/// window on screen) for the duration of the capture and unredirect it afterwards.</para>
	///
	/// <para>This path serves both bare X11 and Cinnamon (Cinnamon runs on X11/Muffin). It cannot
	/// capture a <em>minimized</em> window — the server has dropped its pixmap — in which case the
	/// caller falls back to a screen-rectangle grab.</para>
	/// </summary>
	internal static class X11ScreenCapture
	{
		// XImage field byte offsets on a 64-bit (LP64) build. Only the leading scalar fields and the
		// data pointer are read; the trailing function table is ignored.
		private const int OffWidth = 0;
		private const int OffHeight = 4;
		private const int OffData = 16;
		private const int OffByteOrder = 24;     // 0 = LSBFirst, 1 = MSBFirst
		private const int OffBytesPerLine = 44;
		private const int OffBitsPerPixel = 48;
		private const int OffRedMask = 56;
		private const int OffGreenMask = 64;
		private const int OffBlueMask = 72;

		/// <summary>
		/// Captures the full contents of the X11 window <paramref name="xid"/> (its client area), trying
		/// the occlusion-independent XComposite pixmap first and falling back to a direct window grab.
		/// Returns null when nothing could be captured (e.g. the window is unmapped/minimized or X is
		/// unavailable), so the caller can fall back to a screen-rectangle grab.
		/// </summary>
		internal static Bitmap TryCaptureWindow(long xid)
		{
			if (xid == 0 || !Keysharp.Internals.Platform.Unix.PlatformManager.IsX11Available)
				return null;

			var display = XDisplay.Default.Handle;

			if (display == 0)
				return null;

			lock (WindowManager.xLibLock)
			{
				var ok = true;
				var oldHandler = Xlib.XSetErrorHandler((nint _, ref XErrorEvent __) => { ok = false; return 0; });
				long pixmap = 0;
				var redirectedByUs = false;

				try
				{
					var attr = new XWindowAttributes();

					if (Xlib.XGetWindowAttributes(display, xid, ref attr) == 0 || !ok)
						return null;

					int w = attr.width, h = attr.height;

					if (w <= 0 || h <= 0)
						return null;

					// Obtain an occlusion-independent copy via the window's backing pixmap when XComposite
					// is available. If no compositor is running the window isn't redirected, so redirect it
					// ourselves (Automatic keeps it on screen) for the duration of the grab. The whole block
					// is guarded so a missing libXcomposite.so.1 (DllNotFoundException) simply degrades to the
					// plain visible-window grab below instead of failing the capture outright.
					try
					{
						if (Xlib.XCompositeQueryExtension(display, out _, out _) != 0 && ok)
						{
							if (!CompositorRunning(display))
							{
								ok = true;
								Xlib.XCompositeRedirectWindow(display, xid, Xlib.CompositeRedirectAutomatic);
								_ = Xlib.XSync(display, false);
								redirectedByUs = ok;   // false if the redirect raised (e.g. another manual redirector)
							}

							ok = true;
							pixmap = Xlib.XCompositeNameWindowPixmap(display, xid);
							_ = Xlib.XSync(display, false);

							if (!ok || pixmap == 0)
								pixmap = 0;
						}
					}
					catch
					{
						// libXcomposite unavailable — fall back to a plain (visible-only) window grab.
						pixmap = 0;
					}

					ok = true;
					var drawable = pixmap != 0 ? pixmap : xid;
					var ximage = Xlib.XGetImage(display, drawable, 0, 0, (uint)w, (uint)h, Xlib.AllPlanes, Xlib.ZPixmap);
					_ = Xlib.XSync(display, false);

					// Pixmap grab failed for some reason — retry directly on the window (visible regions only).
					if ((ximage == 0 || !ok) && pixmap != 0)
					{
						if (ximage != 0)
						{
							_ = Xlib.XDestroyImage(ximage);
							ximage = 0;
						}

						ok = true;
						ximage = Xlib.XGetImage(display, xid, 0, 0, (uint)w, (uint)h, Xlib.AllPlanes, Xlib.ZPixmap);
						_ = Xlib.XSync(display, false);
					}

					if (ximage == 0 || !ok)
						return null;

					try
					{
						return BuildBitmapFromXImage(ximage);
					}
					finally
					{
						_ = Xlib.XDestroyImage(ximage);
					}
				}
				catch
				{
					return null;
				}
				finally
				{
					if (pixmap != 0)
						_ = Xlib.XFreePixmap(display, pixmap);

					if (redirectedByUs)
						Xlib.XCompositeUnredirectWindow(display, xid, Xlib.CompositeRedirectAutomatic);

					_ = Xlib.XSync(display, false);
					_ = Xlib.XSetErrorHandler(oldHandler);
				}
			}
		}

		// A compositing manager owns the _NET_WM_CM_Sn selection for screen n while it is running.
		private static bool CompositorRunning(nint display)
		{
			var screen = Xlib.XDefaultScreen(display);
			var atom = Xlib.XInternAtom(display, $"_NET_WM_CM_S{screen}", false);
			return atom != 0 && Xlib.XGetSelectionOwner(display, atom) != 0;
		}

		// Converts an XImage (ZPixmap, TrueColor 24/32 bpp) into a Gtk-backed Eto bitmap. Gtk's
		// Format32bppRgb is laid out R,G,B,A in memory (i.e. the int A_B_G_R on little-endian), matching
		// WaylandScreenCapture.BuildBitmap; channels are extracted from the XImage's RGB masks so the
		// source byte order (BGRX/RGBX) is handled automatically.
		private static unsafe Bitmap BuildBitmapFromXImage(nint ximage)
		{
			int width = Marshal.ReadInt32(ximage, OffWidth);
			int height = Marshal.ReadInt32(ximage, OffHeight);
			var data = Marshal.ReadIntPtr(ximage, OffData);
			int byteOrder = Marshal.ReadInt32(ximage, OffByteOrder);
			int bytesPerLine = Marshal.ReadInt32(ximage, OffBytesPerLine);
			int bitsPerPixel = Marshal.ReadInt32(ximage, OffBitsPerPixel);
			ulong redMask = (ulong)Marshal.ReadInt64(ximage, OffRedMask);
			ulong greenMask = (ulong)Marshal.ReadInt64(ximage, OffGreenMask);
			ulong blueMask = (ulong)Marshal.ReadInt64(ximage, OffBlueMask);

			if (data == 0 || width <= 0 || height <= 0 || bytesPerLine <= 0)
				return null;

			if (bitsPerPixel != 32 && bitsPerPixel != 24)
				return null;   // unsupported depth; caller falls back to a screen grab

			// Default to the standard X TrueColor masks if the server reported none.
			if (redMask == 0 && greenMask == 0 && blueMask == 0)
			{
				redMask = 0xFF0000;
				greenMask = 0x00FF00;
				blueMask = 0x0000FF;
			}

			int rShift = BitOperations.TrailingZeroCount(redMask);
			int gShift = BitOperations.TrailingZeroCount(greenMask);
			int bShift = BitOperations.TrailingZeroCount(blueMask);

			var bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb);

			try
			{
				using var bd = bitmap.Lock();
				var srcBase = (byte*)data;

				for (var row = 0; row < height; row++)
				{
					var srcRow = srcBase + ((long)row * bytesPerLine);
					var dstRow = (uint*)((byte*)bd.Data + ((long)row * bd.ScanWidth));

					if (bitsPerPixel == 32)
					{
						var src32 = (uint*)srcRow;

						for (var col = 0; col < width; col++)
						{
							var v = src32[col];

							// XImage pixels are in the server's byte order; a local same-endian server
							// matches the CPU, so only MSBFirst needs a swap before mask extraction.
							if (byteOrder != 0)
								v = BinaryPrimitives.ReverseEndianness(v);

							uint r = (v >> rShift) & 0xFFu;
							uint g = (v >> gShift) & 0xFFu;
							uint b = (v >> bShift) & 0xFFu;
							dstRow[col] = 0xFF000000u | (b << 16) | (g << 8) | r;
						}
					}
					else // 24 bpp packed (rare; most servers report 32)
					{
						for (var col = 0; col < width; col++)
						{
							var p = srcRow + (col * 3);
							uint b0 = p[0], b1 = p[1], b2 = p[2];
							// LSBFirst packs B,G,R; MSBFirst packs R,G,B.
							uint r = byteOrder == 0 ? b2 : b0;
							uint g = b1;
							uint b = byteOrder == 0 ? b0 : b2;
							dstRow[col] = 0xFF000000u | (b << 16) | (g << 8) | r;
						}
					}
				}

				return bitmap;
			}
			catch
			{
				bitmap.Dispose();
				throw;
			}
		}
	}
}
#endif
