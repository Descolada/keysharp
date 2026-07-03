#if LINUX
namespace Keysharp.Internals.Window.Linux.Wayland
{
	/// <summary>
	/// Generic click-through image overlay backed by zwlr_layer_shell + wl_shm.
	/// Pixels are copied into a premultiplied ARGB8888 SHM buffer and displayed on the overlay layer.
	/// </summary>
	internal sealed class WaylandImageOverlay : IDisposable
	{
		private const string LayerNamespace = "keysharp-image-overlay";
		private const int ConfigureTimeoutMs = 1000;

		private readonly WaylandLayerShellClient client;
		private WaylandLayerSurface surface;
		private WaylandShmBuffer buffer;
		private nint emptyRegion;
		private int width;
		private int height;
		private int marginLeft;
		private int marginTop;
		private bool disposed;

		internal nint Handle => surface?.Surface ?? 0;

		internal WaylandImageOverlay(WaylandLayerShellClient client)
		{
			this.client = client ?? throw new ArgumentNullException(nameof(client));
		}

		internal void Show(Bitmap image, int x, int y, int w, int h)
		{
			if (disposed)
				return;

			if (image == null)
			{
				Hide();
				return;
			}

			if (w <= 0) w = image.Width;
			if (h <= 0) h = image.Height;

			if (w < 1 || h < 1)
			{
				Hide();
				return;
			}

			EnsureSurface();

			if (surface == null)
				return;

			var sizeChanged = w != width || h != height;
			var positionChanged = x != marginLeft || y != marginTop;

			if (sizeChanged)
			{
				width = w;
				height = h;
				surface.SetSize((uint)width, (uint)height);
			}

			if (positionChanged || sizeChanged)
			{
				marginLeft = x;
				marginTop = y;
				surface.SetMargin(y, 0, 0, x);
			}

			if (!surface.IsConfigured)
			{
				surface.Commit();

				if (!surface.WaitForConfigure(ConfigureTimeoutMs))
				{
					Hide();
					return;
				}
			}
			else if (sizeChanged || positionChanged)
			{
				surface.Commit();
				_ = surface.WaitForConfigure(ConfigureTimeoutMs);
			}

			if (sizeChanged || buffer == null)
			{
				buffer?.Dispose();
				buffer = null;

				lock (WaylandLayerShellClient.Sync)
					buffer = WaylandShmBuffer.Create(client.Shm, width, height);
			}

			CopyImageToBuffer(image, buffer, width, height);
			surface.AttachBuffer(buffer);
			surface.Commit();
		}

		internal void Hide()
		{
			TeardownSurface();
			width = height = 0;
		}

		private void EnsureSurface()
		{
			if (surface != null || client == null || !client.IsAvailable)
				return;

			try
			{
				surface = new WaylandLayerSurface(client, WaylandNative.LayerOverlay, LayerNamespace);
				surface.SetAnchor(WaylandNative.AnchorTop | WaylandNative.AnchorLeft);
				surface.SetExclusiveZone(-1);
				surface.SetKeyboardInteractivity(WaylandNative.KeyboardInteractivityNone);

				lock (WaylandLayerShellClient.Sync)
				{
					emptyRegion = WaylandNative.CompositorCreateRegion(client.Compositor);

					if (emptyRegion != 0 && surface.Surface != 0)
						WaylandNative.SurfaceSetInputRegion(surface.Surface, emptyRegion);
				}
			}
			catch
			{
				TeardownSurface();
			}
		}

		private static unsafe void CopyImageToBuffer(Bitmap image, WaylandShmBuffer target, int width, int height)
		{
			if (image == null || target == null || target.Data == 0)
				return;

			// Read `image` in place (the backing owns it; Lock() is read-only) — only an actual
			// resize produces a new bitmap, so the common same-size blit copies nothing.
			var src = image;

			try
			{
				if (src.Width != width || src.Height != height)
				{
					var resized = ImageHelper.ResizeBitmap(src, width, height, exactPixels: true);

					if (!ReferenceEquals(resized, src))
						src = resized;
				}

				var src32 = ImageHelper.EnsureOpaque32Bpp(src);

				try
				{
					using var data = src32.Lock();
					var srcBase = (byte*)data.Data;
					var srcStride = data.ScanWidth;
					var srcBpp = data.BytesPerPixel;
					var dstBase = (uint*)target.Data;
					var dstStride = target.Stride / 4;

					for (var y = 0; y < height; y++)
					{
						var srcRow = srcBase + (long)y * srcStride;
						var dstRow = dstBase + y * dstStride;

						for (var x = 0; x < width; x++)
						{
							var raw = *(int*)(srcRow + x * srcBpp);
							var argb = (uint)data.TranslateDataToArgb(raw);
							dstRow[x] = Premultiply(argb);
						}
					}
				}
				finally
				{
					if (!ReferenceEquals(src32, src))
						src32.Dispose();
				}
			}
			finally
			{
				if (!ReferenceEquals(src, image))
					src.Dispose();
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static uint Premultiply(uint argb)
		{
			var a = (argb >> 24) & 0xFF;
			var r = (argb >> 16) & 0xFF;
			var g = (argb >> 8) & 0xFF;
			var b = argb & 0xFF;

			if (a == 0)
				return 0;

			if (a != 255)
			{
				r = (r * a + 127) / 255;
				g = (g * a + 127) / 255;
				b = (b * a + 127) / 255;
			}

			return (a << 24) | (r << 16) | (g << 8) | b;
		}

		private void TeardownSurface()
		{
			lock (WaylandLayerShellClient.Sync)
			{
				if (buffer != null)
				{
					buffer.Dispose();
					buffer = null;
				}

				if (emptyRegion != 0)
				{
					WaylandNative.RegionDestroy(emptyRegion);
					emptyRegion = 0;
				}
			}

			surface?.Dispose();
			surface = null;
		}

		public void Dispose()
		{
			if (disposed)
				return;

			disposed = true;
			TeardownSurface();
		}
	}
}
#endif
