#if LINUX
using System.Buffers;

namespace Keysharp.Internals.Window.Linux.Wayland
{
	/// <summary>
	/// Generic click-through image overlay backed by zwlr_layer_shell + wl_shm.
	/// Pixels are copied into a premultiplied ARGB8888 SHM buffer and displayed on the overlay layer.
	/// </summary>
	internal sealed class WaylandImageOverlay : IDisposable
	{
		// This string is both the layer-shell namespace and, on KWin, the surface's semantic scope.
		// KWin maps unknown scopes to a normal application window.  Using a private value here therefore
		// made our input-empty overlay look like an ordinary Keysharp window to windowAt/WinFromPoint,
		// causing own-PID guards to mistake it for an interactive popup.  "on-screen-display" is the
		// compositor-defined type which matches this passive, non-activating overlay surface.
		private const string LayerNamespace = "on-screen-display";
		private const int ConfigureTimeoutMs = 1000;

		private readonly WaylandLayerShellClient client;
		private readonly object stateSync = new();
		private WaylandLayerSurface surface;
		// Bounded triple buffering: in-flight buffers remain untouched until wl_buffer.release. If all three
		// are busy, the new frame is dropped and the last complete frame remains mapped.
		private readonly List<WaylandShmBuffer> bufferPool = new();
		private nint emptyRegion;
		private int marginLeft;
		private int marginTop;
		private WaylandShmBuffer preparedBuffer;
		private int preparedMarginLeft, preparedMarginTop;
		private uint outputName;
		private bool disposed;
		private bool connectionInvalidated;

		internal nint Handle => surface?.Surface ?? 0;
		internal bool IsAvailable => !disposed && !connectionInvalidated && client.IsAvailable;

		internal WaylandImageOverlay(WaylandLayerShellClient client)
		{
			this.client = client ?? throw new ArgumentNullException(nameof(client));

			if (!client.Register(this))
				throw new IOException("The Wayland layer-shell connection is unavailable.");
		}

		// Returns true iff the overlay is now shown. False on a genuine layer-shell failure (surface could not be
		// created, or never configured within the timeout) so the caller can fall back to a visible Eto window
		// instead of recording a phantom "shown" overlay with nothing on screen.
		internal bool Show(Bitmap image, Rectangle sourcePixels, ScreenRect bounds,
			WaylandLayerShellClient.OutputTarget output, bool clickThrough)
		{
			lock (stateSync)
				return PrepareCore(image, sourcePixels, bounds, output, clickThrough) && CommitPreparedCore();
		}

		internal bool Prepare(Bitmap image, Rectangle sourcePixels, ScreenRect bounds,
			WaylandLayerShellClient.OutputTarget output, bool clickThrough)
		{
			lock (stateSync)
				return PrepareCore(image, sourcePixels, bounds, output, clickThrough);
		}

		internal bool CommitPrepared()
		{
			lock (stateSync)
				return CommitPreparedCore();
		}

		private bool PrepareCore(Bitmap image, Rectangle sourcePixels, ScreenRect bounds,
			WaylandLayerShellClient.OutputTarget output, bool clickThrough)
		{
			if (disposed || connectionInvalidated || !client.IsAvailable)
				return false;

			if (surface?.IsClosed == true)
				TeardownSurface();

			if (image == null)
			{
				HideCore();
				return false;
			}

			if (bounds.Width <= 0) bounds = bounds with { Width = image.Width };
			if (bounds.Height <= 0) bounds = bounds with { Height = image.Height };

			if (!bounds.HasArea || !client.IsOutputCurrent(output))
			{
				HideCore();
				return false;
			}

			// A layer surface is permanently assigned to the wl_output passed at construction. Crossing a monitor
			// therefore requires a fresh surface; keeping global margins on the old output is what previously made an
			// overlay disappear or jump on mixed monitor layouts.
			if (surface != null && output.RegistryName != outputName)
				TeardownSurface();

			EnsureSurface(output);

			if (surface == null)
				return false;

			var localX = bounds.X - output.Bounds.X;
			var localY = bounds.Y - output.Bounds.Y;
			var w = bounds.Width;
			var h = bounds.Height;

			// Prepare the complete replacement raster before touching pending surface geometry. Allocation or copy
			// failure therefore leaves the previous live frame and rectangle unchanged.
			var useViewport = client.Viewporter != 0;
			var bufferScale = Math.Max(1, output.IntegerScale);
			if (!TryResolvePixelLength(w, useViewport ? output.BufferScale : bufferScale, out var pixelWidth)
				|| !TryResolvePixelLength(h, useViewport ? output.BufferScale : bufferScale, out var pixelHeight))
				return false;

			var target = AcquireBuffer(pixelWidth, pixelHeight);

			if (target == null)
				return false;

			CopyImageToBuffer(image, sourcePixels, target, pixelWidth, pixelHeight);
			var initiallyConfigured = surface.IsConfigured;
			surface.SetSize((uint)w, (uint)h);
			surface.SetMargin(localY, 0, 0, localX);
			surface.SetInputRegion(ResolveInputRegion(clickThrough, emptyRegion));
			_ = surface.ConfigureBufferMapping(w, h, bufferScale);

			if (!initiallyConfigured)
			{
				// Layer-shell requires one initial null-buffer commit and configure acknowledgement. Every later
				// resize combines geometry, viewport and replacement pixels in the single final commit below.
				if (!surface.Commit())
				{
					HideCore();
					return false;
				}

				if (!surface.WaitForConfigure(ConfigureTimeoutMs))
				{
					// The compositor never acked the initial configure — a real layer-shell failure. Tear the
					// surface down and report failure so the backing falls back to Eto rather than silently showing
					// nothing.
					HideCore();
					return false;
				}
			}

			preparedBuffer = target;
			preparedMarginLeft = localX;
			preparedMarginTop = localY;
			return true;
		}

		private bool CommitPreparedCore()
		{
			var target = preparedBuffer;

			if (target == null || surface == null || !client.IsAvailable)
				return false;

			preparedBuffer = null;
			if (!surface.AttachBuffer(target) || !surface.Commit())
				return false;

			marginLeft = preparedMarginLeft;
			marginTop = preparedMarginTop;
			return true;
		}

		private static bool TryResolvePixelLength(int logicalLength, double scale, out int pixels)
		{
			var value = Math.Round(logicalLength * scale);
			if (!double.IsFinite(value) || value < 1 || value > int.MaxValue)
			{
				pixels = 0;
				return false;
			}

			pixels = (int)value;
			return true;
		}

		// Reuse released buffers, reap stale sizes, and drop a frame once the bounded pool is full.
		private WaylandShmBuffer AcquireBuffer(int w, int h)
		{
			lock (WaylandLayerShellClient.Sync)
			{
				for (var i = bufferPool.Count - 1; i >= 0; i--)
				{
					var b = bufferPool[i];

					if ((b.Width != w || b.Height != h) && b.Released)
					{
						// A released buffer of the wrong size can never satisfy this frame.
						b.Dispose();
						bufferPool.RemoveAt(i);
					}
				}

				Span<WaylandBufferState> states = stackalloc WaylandBufferState[bufferPool.Count];

				for (var i = 0; i < bufferPool.Count; i++)
					states[i] = new WaylandBufferState(bufferPool[i].Width, bufferPool[i].Height,
						bufferPool[i].Released);

				var reusable = WaylandBufferPoolPolicy.FindReusable(states, w, h);

				if (reusable >= 0)
					return bufferPool[reusable];

				if (WaylandBufferPoolPolicy.CanAllocate(bufferPool.Count))
				{
					var chosen = WaylandShmBuffer.Create(client.Shm, w, h);
					bufferPool.Add(chosen);
					return chosen;
				}

				return null;
			}
		}

		internal static nint ResolveInputRegion(bool clickThrough, nint emptyRegion)
			=> clickThrough ? emptyRegion : 0;

		// Same-output reposition: one margin commit, no topology lookup, roundtrip, or pixel upload.
		internal bool Reposition(ScreenRect bounds, WaylandLayerShellClient.OutputTarget output)
		{
			lock (stateSync)
				return RepositionCore(bounds, output);
		}

		private bool RepositionCore(ScreenRect bounds, WaylandLayerShellClient.OutputTarget output)
		{
			if (disposed || connectionInvalidated || !client.IsAvailable || surface == null || !surface.IsConfigured || surface.IsClosed)
				return false;

			lock (WaylandLayerShellClient.Sync)
			{
				if (!client.IsOutputCurrent(output) || output.RegistryName != outputName)
					return false;

				var x = bounds.X - output.Bounds.X;
				var y = bounds.Y - output.Bounds.Y;

				if (x == marginLeft && y == marginTop)
					return true;

				surface.SetMargin(y, 0, 0, x);

				if (!surface.Commit() || !client.IsAvailable)
					return false;

				marginLeft = x;
				marginTop = y;
				return true;
			}
		}

		private void HideCore()
		{
			TeardownSurface(connectionInvalidated);
		}

		private void EnsureSurface(WaylandLayerShellClient.OutputTarget output)
		{
			if (surface != null || !client.IsAvailable)
				return;

			try
			{
				lock (WaylandLayerShellClient.Sync)
				{
					if (!client.IsOutputCurrent(output))
						return;

					surface = new WaylandLayerSurface(client, output.Proxy, WaylandNative.LayerOverlay, LayerNamespace);
				}
				outputName = output.RegistryName;
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

		private static unsafe void CopyImageToBuffer(Bitmap image, Rectangle sourcePixels,
			WaylandShmBuffer target, int width, int height)
		{
			if (image == null || target == null || target.Data == 0)
				return;

			var source = Rectangle.Intersect(sourcePixels, new Rectangle(0, 0, image.Width, image.Height));

			if (source.Width <= 0 || source.Height <= 0)
				return;

			var src32 = ImageHelper.EnsureOpaque32Bpp(image);
			int[] sourceXs = null;

			try
			{
				sourceXs = ArrayPool<int>.Shared.Rent(width);
				using var data = src32.Lock();
				var srcBase = (byte*)data.Data;
				var srcStride = data.ScanWidth;
				var srcBpp = data.BytesPerPixel;
				var dstBase = (uint*)target.Data;
				var dstStride = target.Stride / 4;

				for (var x = 0; x < width; x++)
					sourceXs[x] = source.X + SampleIndex(x, width, source.Width);

				// Probe the backend channel order once. Known layouts avoid virtual translation per pixel;
				// an unfamiliar or premultiplied layout takes the exact fallback below.
				const int marker = unchecked((int)0x80102030);
				var translated = (uint)data.TranslateDataToArgb(marker);
				var identity = translated == 0x80102030u;
				var rbSwap = translated == 0x80302010u;

				for (var y = 0; y < height; y++)
				{
					var sourceY = source.Y + SampleIndex(y, height, source.Height);
					var srcRow = srcBase + (long)sourceY * srcStride;
					var dstRow = dstBase + y * dstStride;

					for (var x = 0; x < width; x++)
					{
						var raw = (uint)*(int*)(srcRow + sourceXs[x] * srcBpp);
						uint argb;

						if (rbSwap)
							argb = (raw & 0xFF00FF00u) | ((raw >> 16) & 0xFFu) | ((raw & 0xFFu) << 16);
						else if (identity)
							argb = raw;
						else
							argb = (uint)data.TranslateDataToArgb((int)raw);

						dstRow[x] = Premultiply(argb);
					}
				}
			}
			finally
			{
				if (sourceXs != null)
					ArrayPool<int>.Shared.Return(sourceXs);

				if (!ReferenceEquals(src32, image))
					src32.Dispose();
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal static int SampleIndex(int targetIndex, int targetLength, int sourceLength)
			=> (int)Math.Min(sourceLength - 1L,
				((2L * targetIndex + 1) * sourceLength) / (2L * targetLength));

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

		private void TeardownSurface(bool abandon = false, bool forceLocal = false)
		{
			preparedBuffer = null;
			if (abandon)
				surface?.Abandon();
			else
				surface?.Dispose();
			surface = null;

			lock (WaylandLayerShellClient.Sync)
			{
				// Released buffers are freed now. In-flight buffers retire themselves and keep their mapping alive
				// until wl_buffer.release, avoiding a compositor read from unmapped SHM after surface destruction.
				foreach (var b in bufferPool)
				{
					if (abandon)
						b.Abandon();
					else
					{
						b.Dispose();
						// Dispose defers an in-flight buffer to wl_buffer.release. Connection invalidation stops the
						// dispatcher immediately afterward, so force its local mapping/handle cleanup then.
						if (forceLocal) b.Abandon();
					}
				}

				bufferPool.Clear();

				if (emptyRegion != 0 && !abandon)
				{
					WaylandNative.RegionDestroy(emptyRegion);
				}

				emptyRegion = 0;
			}
			marginLeft = marginTop = int.MinValue;
			outputName = 0;
		}

		public void Dispose()
		{
			lock (stateSync)
			{
				if (disposed)
					return;

				disposed = true;
				TeardownSurface(connectionInvalidated || !client.IsAvailable);
			}

			client.Unregister(this);
		}

		/// <summary>Called by the owning connection before wl_display_disconnect. Removes every raw proxy pointer
		/// and listener handle, and force-releases local SHM resources when release events can no longer arrive.</summary>
		internal void InvalidateConnection(bool connectionLost)
		{
			lock (stateSync)
			{
				if (connectionInvalidated)
					return;

				connectionInvalidated = true;
				TeardownSurface(connectionLost, forceLocal: true);
			}

			client.Unregister(this);
		}
	}
}
#endif
