using System.Runtime.InteropServices;

#if LINUX
namespace Keysharp.Internals.Window.Linux.Wayland
{
	/// <summary>
	/// One ARGB8888 buffer backed by a memfd-backed wl_shm_pool. Lifecycle:
	///   1. Create() — allocates the memfd, mmaps it, and creates wl_shm_pool + wl_buffer proxies.
	///   2. Caller writes pixels into Data.
	///   3. Surface attaches Buffer, damages, commits — compositor reads from the same mapping.
	///   4. Dispose() — destroys both proxies, unmaps, closes the fd.
	///
	/// Buffers must not be disposed while the compositor still owns them. Callers should keep the
	/// buffer alive until either (a) a newer buffer has been attached AND the compositor has
	/// released this one (via the wl_buffer.release event), or (b) the surface is being destroyed
	/// and the compositor will release everything implicitly.
	/// </summary>
	internal sealed class WaylandShmBuffer : IDisposable
	{
		internal int Width { get; }
		internal int Height { get; }
		internal int Stride { get; }
		internal uint Format { get; }
		internal nint Buffer { get; private set; }
		internal nint Data { get; private set; }
		internal nuint MapLength { get; }

		private int fd;
		private nint pool;
		private GCHandle listenerHandle;
		private volatile bool released = true;
		internal bool Released => released;

		private WaylandShmBuffer(int fd, nint mapping, nuint mapLength, nint pool, nint buffer,
			int width, int height, int stride, uint format)
		{
			this.fd = fd;
			Data = mapping;
			MapLength = mapLength;
			this.pool = pool;
			Buffer = buffer;
			Width = width;
			Height = height;
			Stride = stride;
			Format = format;
			listenerHandle = GCHandle.Alloc(this);
			_ = WaylandNative.ProxyAddListener(buffer, ReleaseListener.Pointer, GCHandle.ToIntPtr(listenerHandle));
		}

		/// <summary>
		/// Allocates a new ARGB8888 buffer of the requested pixel size. The compositor must already
		/// have advertised wl_shm.format for ARGB8888 (every compositor does — it's mandatory).
		/// </summary>
		internal static WaylandShmBuffer Create(nint shm, int width, int height, uint format = WaylandNative.WlShmFormatArgb8888)
		{
			if (shm == 0)
				throw new InvalidOperationException("wl_shm global is not bound.");

			if (width <= 0 || height <= 0)
				throw new ArgumentOutOfRangeException(nameof(width), "Width and height must be positive.");

			var stride = width * 4;
			var size = (long)stride * height;

			var fd = WaylandNative.MemfdCreate("keysharp-wl-shm", WaylandNative.MFD_CLOEXEC);

			if (fd < 0)
				throw new IOException($"memfd_create failed: errno={Marshal.GetLastPInvokeError()}");

			try
			{
				if (WaylandNative.Ftruncate(fd, size) != 0)
					throw new IOException($"ftruncate({size}) failed: errno={Marshal.GetLastPInvokeError()}");

				var mapping = WaylandNative.Mmap(0, (nuint)size, WaylandNative.PROT_READ | WaylandNative.PROT_WRITE,
					WaylandNative.MAP_SHARED, fd, 0);

				if (mapping == WaylandNative.MAP_FAILED)
					throw new IOException($"mmap failed: errno={Marshal.GetLastPInvokeError()}");

				try
				{
					var pool = WaylandNative.ShmCreatePool(shm, fd, (int)size);

					if (pool == 0)
						throw new IOException("wl_shm.create_pool returned null.");

					try
					{
						var buffer = WaylandNative.ShmPoolCreateBuffer(pool, 0, width, height, stride, format);

						if (buffer == 0)
							throw new IOException("wl_shm_pool.create_buffer returned null.");

						return new WaylandShmBuffer(fd, mapping, (nuint)size, pool, buffer, width, height, stride, format);
					}
					catch
					{
						WaylandNative.ShmPoolDestroy(pool);
						throw;
					}
				}
				catch
				{
					_ = WaylandNative.Munmap(mapping, (nuint)size);
					throw;
				}
			}
			catch
			{
				_ = WaylandNative.Close(fd);
				throw;
			}
		}

		/// <summary>
		/// Fills the entire buffer with a single premultiplied ARGB8888 colour. Useful for clearing
		/// before re-rendering or for solid backgrounds.
		/// </summary>
		internal unsafe void Fill(uint argb)
		{
			if (Data == 0)
				return;

			var ptr = (uint*)Data;
			var count = (Stride * Height) / 4;

			for (var i = 0; i < count; i++)
				ptr[i] = argb;
		}

		/// <summary>
		/// Copies a managed pixel array (ARGB8888, row-major, stride equal to width*4) into the
		/// underlying SHM region. The source array must be exactly Width * Height pixels.
		/// </summary>
		internal void CopyFrom(ReadOnlySpan<uint> pixels)
		{
			if (Data == 0)
				return;

			if (pixels.Length != Width * Height)
				throw new ArgumentException($"Pixel buffer length {pixels.Length} does not match {Width}x{Height} = {Width * Height}.", nameof(pixels));

			unsafe
			{
				fixed (uint* src = pixels)
				{
					System.Buffer.MemoryCopy(src, (void*)Data, (long)MapLength, (long)(Width * Height * 4));
				}
			}
		}

		// Listener glue for the wl_buffer.release event. The compositor sends this exactly when the
		// buffer is no longer in use and the client may safely reuse or destroy it.
		internal void MarkReleased() => released = true;

		internal void MarkInFlight() => released = false;

		public void Dispose()
		{
			if (Buffer != 0)
			{
				WaylandNative.BufferDestroy(Buffer);
				Buffer = 0;
			}

			if (pool != 0)
			{
				WaylandNative.ShmPoolDestroy(pool);
				pool = 0;
			}

			if (Data != 0)
			{
				_ = WaylandNative.Munmap(Data, MapLength);
				Data = 0;
			}

			if (fd >= 0)
			{
				_ = WaylandNative.Close(fd);
				fd = -1;
			}

			if (listenerHandle.IsAllocated)
				listenerHandle.Free();
		}

		private static class ReleaseListener
		{
			private static readonly ReleaseHandler onRelease = Release;
			internal static readonly nint Pointer = Build();

			private static nint Build()
			{
				var block = Marshal.AllocHGlobal(IntPtr.Size);
				Marshal.WriteIntPtr(block, 0, Marshal.GetFunctionPointerForDelegate(onRelease));
				return block;
			}

			private static void Release(nint data, nint buffer)
			{
				var handle = GCHandle.FromIntPtr(data);

				if (handle.IsAllocated && handle.Target is WaylandShmBuffer self)
					self.MarkReleased();
			}

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			private delegate void ReleaseHandler(nint data, nint buffer);
		}
	}
}
#endif
