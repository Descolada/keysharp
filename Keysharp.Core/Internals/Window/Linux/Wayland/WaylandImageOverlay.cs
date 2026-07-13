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
		// A small pool of SHM buffers (double-buffering). We must never overwrite or dispose a buffer the
		// compositor may still be scanning out (wl_buffer.release not yet received, i.e. Released == false):
		// doing so tears/garbles the surface. Each Show picks a Released buffer (or allocates a fresh one) and
		// leaves the just-attached in-flight buffer alone until the compositor releases it. Wrong-size buffers
		// are reaped once released; all are freed on teardown (the compositor releases them implicitly then).
		private readonly List<WaylandShmBuffer> bufferPool = new();
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

		// Returns true iff the overlay is now shown. False on a genuine layer-shell failure (surface could not be
		// created, or never configured within the timeout) so the caller can fall back to a visible Eto window
		// instead of recording a phantom "shown" overlay with nothing on screen.
		internal bool Show(Bitmap image, int x, int y, int w, int h)
		{
			if (disposed)
				return false;

			if (image == null)
			{
				Hide();
				return false;
			}

			if (w <= 0) w = image.Width;
			if (h <= 0) h = image.Height;

			if (w < 1 || h < 1)
			{
				Hide();
				return false;
			}

			EnsureSurface();

			if (surface == null)
				return false;

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
					// The compositor never acked the initial configure — a real layer-shell failure. Tear the
					// surface down and report failure so the backing falls back to Eto rather than silently showing
					// nothing.
					Hide();
					return false;
				}
			}
			else if (sizeChanged || positionChanged)
			{
				surface.Commit();
				_ = surface.WaitForConfigure(ConfigureTimeoutMs);
			}

			// Double-buffer: acquire a buffer the compositor is NOT still scanning out (or allocate a fresh one),
			// never overwriting/disposing the in-flight one. This is what avoids tearing on a same-size update.
			var target = AcquireBuffer(width, height);
			CopyImageToBuffer(image, target, width, height);
			surface.AttachBuffer(target);   // marks `target` in-flight until the compositor releases it
			surface.Commit();
			return true;
		}

		// Picks a buffer of the requested size that the compositor has released (safe to overwrite), allocating a
		// fresh one when none is free. Reaps released buffers of a stale size; leaves still-in-flight buffers alone
		// (they are reaped on a later Show once released, or at teardown). Wayland proxy create/destroy must run
		// under the shared lock — and reading Released under it is consistent, since the release callback that sets
		// it also runs under that lock on the dispatcher thread.
		private WaylandShmBuffer AcquireBuffer(int w, int h)
		{
			lock (WaylandLayerShellClient.Sync)
			{
				WaylandShmBuffer chosen = null;

				for (var i = bufferPool.Count - 1; i >= 0; i--)
				{
					var b = bufferPool[i];

					if (b.Width == w && b.Height == h)
					{
						if (chosen == null && b.Released)
							chosen = b;   // reuse the first released same-size buffer
					}
					else if (b.Released)
					{
						// Wrong size AND no longer in use by the compositor: safe to free.
						b.Dispose();
						bufferPool.RemoveAt(i);
					}
					// else: wrong size but still in-flight — leave it; a later Show reaps it once released.
				}

				if (chosen == null)
				{
					chosen = WaylandShmBuffer.Create(client.Shm, w, h);
					bufferPool.Add(chosen);
				}

				return chosen;
			}
		}

		// Repositions the already-shown surface without re-uploading pixels: only the layer margin changes, so a
		// same-size move (e.g. a mouse-following highlight) costs a commit, not another SHM blit.
		internal void Reposition(int x, int y)
		{
			if (disposed || surface == null || !surface.IsConfigured)
				return;

			if (x == marginLeft && y == marginTop)
				return;

			marginLeft = x;
			marginTop = y;
			surface.SetMargin(y, 0, 0, x);
			surface.Commit();
			_ = surface.WaitForConfigure(ConfigureTimeoutMs);
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

					// Resolve the backend's fixed channel order ONCE instead of a virtual TranslateDataToArgb call
					// per pixel (~2M/frame at 1080p). Probe the transform with a deliberately TRANSLUCENT distinct-
					// byte marker: GTK — the only Linux Eto backend — stores non-premultiplied RGBA, so a little-
					// endian 4-byte read is 0xAABBGGRR and needs R<->B swapped to reach straight 0xAARRGGBB (rbSwap);
					// a layout already storing straight 0xAARRGGBB is the identity case. The translucent marker means
					// any premultiplication in the stored layout perturbs the probe and drops us to the safe per-
					// pixel fallback (so we never mis-colour translucent pixels). The chosen mode is loop-invariant,
					// so the branch predicts perfectly and the hot path is pure arithmetic + Premultiply, no dispatch.
					const int marker = unchecked((int)0x80102030);          // A=80 R=10 G=20 B=30 as 0xAARRGGBB
					var translated = (uint)data.TranslateDataToArgb(marker);
					var identity = translated == 0x80102030u;               // stored layout already straight 0xAARRGGBB
					var rbSwap = translated == 0x80302010u;                 // GTK: non-premultiplied RGBA, swap R<->B

					for (var y = 0; y < height; y++)
					{
						var srcRow = srcBase + (long)y * srcStride;
						var dstRow = dstBase + y * dstStride;

						for (var x = 0; x < width; x++)
						{
							var raw = (uint)*(int*)(srcRow + x * srcBpp);
							uint argb;

							if (rbSwap)
								argb = (raw & 0xFF00FF00u) | ((raw >> 16) & 0xFFu) | ((raw & 0xFFu) << 16);
							else if (identity)
								argb = raw;
							else
								argb = (uint)data.TranslateDataToArgb((int)raw);   // unexpected layout: exact fallback

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
				// The surface is being destroyed, so the compositor releases every attached buffer implicitly —
				// it is safe to free the whole pool here, in-flight buffers included.
				foreach (var b in bufferPool)
					b.Dispose();

				bufferPool.Clear();

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
