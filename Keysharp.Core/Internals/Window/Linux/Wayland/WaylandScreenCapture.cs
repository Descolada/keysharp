#if LINUX
using System.IO;
using System.Runtime.InteropServices;
using Eto.Drawing;

namespace Keysharp.Internals.Window.Linux.Wayland
{
	/// <summary>
	/// Single-shot screen capture via zwlr_screencopy_unstable_v1. Opens a fresh
	/// Wayland connection per call, probes the registry for the screencopy
	/// manager / wl_shm / wl_outputs, picks the output whose logical geometry
	/// contains the requested rect, copies one frame into a memfd-backed SHM
	/// buffer, and converts the result into an Eto <see cref="Bitmap"/>.
	///
	/// Returns null whenever the compositor doesn't advertise the protocol
	/// (GNOME, KDE without wlroots, ...). Callers fall back to whatever capture
	/// path they used before.
	///
	/// Targets protocol version 3 (sway, hyprland, Wayfire, labwc, river, KWin
	/// with wlroots-protocols enabled), but accepts whatever version the
	/// compositor advertises down to 1; unused events (damage, linux_dmabuf,
	/// buffer_done) are ignored.
	/// </summary>
	internal static class WaylandScreenCapture
	{
		private const string ScreencopyManagerName = "zwlr_screencopy_manager_v1";
		private const string ScreencopyFrameName = "zwlr_screencopy_frame_v1";
		private const string ShmName = "wl_shm";
		private const string OutputName = "wl_output";

		private const uint ScreencopyCaptureOutputRegionOpcode = 1;
		private const uint ScreencopyFrameCopyOpcode = 0;
		private const uint ScreencopyFrameDestroyOpcode = 1;

		/// <summary>
		/// wlroots zwlr_screencopy region grab. The compositor flavor is already resolved by the
		/// <c>WlrootsScreen</c> impl (the only caller), so there is no backend dispatch here — just the protocol.
		/// KWin/GNOME region grabs go through <c>ScreencapHelper</c> directly from their own IScreen impls.
		/// </summary>
		internal static Bitmap TryCapture(int x, int y, int w, int h)
		{
			if (w <= 0 || h <= 0)
				return null;

			using var session = Session.TryOpen();
			return session?.Capture(x, y, w, h);
		}

		private sealed class OutputInfo
		{
			internal nint Proxy;
			internal uint Name;
			internal int X;
			internal int Y;
			internal int Width;
			internal int Height;
			internal bool Done;
		}

		private sealed class FrameState
		{
			internal uint Format;
			internal int Width;
			internal int Height;
			internal int Stride;
			internal bool BufferInfoReady;
			internal bool Ready;
			internal bool Failed;
			internal uint Flags;
		}

		private sealed class Session : IDisposable
		{
			private readonly nint display;
			private readonly GCHandle selfHandle;
			private readonly List<GCHandle> outputHandles = [];
			private readonly Dictionary<uint, OutputInfo> outputsByName = [];
			private readonly Dictionary<nint, OutputInfo> outputsByProxy = [];
			private nint registry;
			private nint shm;
			private nint screencopyManager;
			private uint screencopyManagerVersion;

			private Session(nint display)
			{
				this.display = display;
				selfHandle = GCHandle.Alloc(this);
			}

			internal static Session TryOpen()
			{
				var display = WaylandNative.DisplayConnect(null);

				if (display == 0)
					return null;

				var self = new Session(display);

				try
				{
					self.registry = WaylandNative.DisplayGetRegistry(display);

					if (self.registry == 0 || WaylandNative.ProxyAddListener(self.registry, RegistryListener.Pointer, GCHandle.ToIntPtr(self.selfHandle)) != 0)
						return null;

					_ = WaylandNative.DisplayRoundtrip(display);

					if (self.screencopyManager == 0 || self.shm == 0 || self.outputsByName.Count == 0)
						return null;

					// Second roundtrip delivers each wl_output's geometry / mode / done events
					// so we know each monitor's logical position and size.
					_ = WaylandNative.DisplayRoundtrip(display);

					var keep = self;
					self = null;
					return keep;
				}
				finally
				{
					self?.Dispose();
				}
			}

			internal Bitmap Capture(int x, int y, int w, int h)
			{
				var output = SelectOutput(x, y);

				if (output == null)
					return null;

				var localX = x - output.X;
				var localY = y - output.Y;

				var frame = WaylandNative.MarshalCaptureOutputRegion(
					screencopyManager,
					ScreencopyCaptureOutputRegionOpcode,
					ScreencopyInterfaces.Frame.Pointer,
					screencopyManagerVersion,
					0,
					0,
					0,
					output.Proxy,
					localX, localY, w, h);

				if (frame == 0)
					return null;

				var state = new FrameState();
				var stateHandle = GCHandle.Alloc(state);

				try
				{
					if (WaylandNative.ProxyAddListener(frame, FrameListener.Pointer, GCHandle.ToIntPtr(stateHandle)) != 0)
						return null;

					// Pump events until the buffer description arrives or the compositor
					// gives up. wl_display_dispatch blocks for at least one event each call,
					// so a small cap is enough to fail fast on misbehaving compositors.
					for (var i = 0; i < 50 && !state.BufferInfoReady && !state.Failed; i++)
						if (WaylandNative.DisplayDispatch(display) < 0)
							return null;

					if (!state.BufferInfoReady || state.Failed)
						return null;

					if (state.Format != WaylandNative.WlShmFormatArgb8888 && state.Format != WaylandNative.WlShmFormatXrgb8888)
						return null;

					using var buffer = ShmFrameBuffer.Create(shm, state.Width, state.Height, state.Stride, state.Format);

					if (buffer == null)
						return null;

					WaylandNative.MarshalObjectRequest(frame, ScreencopyFrameCopyOpcode, 0, WaylandNative.ProxyGetVersion(frame), 0, buffer.Buffer);
					_ = WaylandNative.DisplayFlush(display);

					for (var i = 0; i < 200 && !state.Ready && !state.Failed; i++)
						if (WaylandNative.DisplayDispatch(display) < 0)
							return null;

					if (!state.Ready)
						return null;

					return BuildBitmap(buffer.Data, state.Stride, state.Width, state.Height, state.Format, (state.Flags & 1) != 0);
				}
				finally
				{
					WaylandNative.MarshalRequest(frame, ScreencopyFrameDestroyOpcode, 0, WaylandNative.ProxyGetVersion(frame), WaylandNative.DestroyFlag);

					if (stateHandle.IsAllocated)
						stateHandle.Free();
				}
			}

			private OutputInfo SelectOutput(int x, int y)
			{
				OutputInfo best = null;

				foreach (var info in outputsByName.Values)
				{
					if (!info.Done || info.Width <= 0 || info.Height <= 0)
						continue;

					if (x >= info.X && x < info.X + info.Width && y >= info.Y && y < info.Y + info.Height)
						return info;

					// Remember any finished output as a fallback for rects that lie outside
					// every known monitor (e.g. negative coords on a single-output setup).
					best ??= info;
				}

				return best;
			}

			private static unsafe Bitmap BuildBitmap(nint src, int srcStride, int width, int height, uint format, bool flipY)
			{
				// On Gtk, Bitmap(w,h,Format32bppRgba) is backed by a Gdk.Pixbuf whose memory layout
				// is R,G,B,A — *not* the B,G,R,A layout that wl_shm ARGB8888/XRGB8888 deliver. We
				// can't ask Eto for a BGRA-laid-out bitmap directly, so we swap R↔B per pixel here.
				// For XRGB8888 the source alpha byte is undefined; force 0xFF so the result is fully
				// opaque (BitmapDataHandler reports A=0 as transparent otherwise).
				var pixelFormat = format == WaylandNative.WlShmFormatXrgb8888 ? PixelFormat.Format32bppRgb : PixelFormat.Format32bppRgba;
				var bitmap = new Bitmap(width, height, pixelFormat);
				var opaqueAlpha = format == WaylandNative.WlShmFormatXrgb8888;

				try
				{
					using var data = bitmap.Lock();

					for (var row = 0; row < height; row++)
					{
						var srcRow = flipY ? height - 1 - row : row;
						var srcPtr = (uint*)((byte*)src + ((long)srcRow * srcStride));
						var dstPtr = (uint*)((byte*)data.Data + ((long)row * data.ScanWidth));

						for (var col = 0; col < width; col++)
						{
							var p = srcPtr[col];
							// p in little-endian int is A_R_G_B (high..low), bytes-in-memory B,G,R,A.
							// Repack to bytes-in-memory R,G,B,A i.e. int A_B_G_R.
							var a = opaqueAlpha ? 0xFFu : (p >> 24) & 0xFFu;
							var r = (p >> 16) & 0xFFu;
							var g = (p >> 8) & 0xFFu;
							var b = p & 0xFFu;
							dstPtr[col] = (a << 24) | (b << 16) | (g << 8) | r;
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

			public void Dispose()
			{
				foreach (var handle in outputHandles)
					if (handle.IsAllocated)
						handle.Free();

				outputHandles.Clear();

				foreach (var output in outputsByName.Values)
					if (output.Proxy != 0)
						WaylandNative.ProxyDestroy(output.Proxy);

				outputsByName.Clear();
				outputsByProxy.Clear();

				if (screencopyManager != 0)
				{
					WaylandNative.ProxyDestroy(screencopyManager);
					screencopyManager = 0;
				}

				if (shm != 0)
				{
					WaylandNative.ProxyDestroy(shm);
					shm = 0;
				}

				if (registry != 0)
				{
					WaylandNative.ProxyDestroy(registry);
					registry = 0;
				}

				if (selfHandle.IsAllocated)
					selfHandle.Free();

				if (display != 0)
					WaylandNative.DisplayDisconnect(display);
			}

			private void BindManager(uint name, uint version)
			{
				if (screencopyManager != 0)
					return;

				screencopyManagerVersion = Math.Min(version, 3u);
				screencopyManager = WaylandNative.RegistryBind(registry, name, ScreencopyInterfaces.Manager, screencopyManagerVersion);
			}

			private void BindShm(uint name, uint version)
			{
				if (shm == 0)
					shm = WaylandNative.RegistryBind(registry, name, WaylandNative.ShmInterface, ShmName, Math.Min(version, 1u));
			}

			private void BindOutput(uint name, uint version)
			{
				if (outputsByName.ContainsKey(name))
					return;

				var proxy = WaylandNative.RegistryBind(registry, name, WaylandNative.OutputInterface, OutputName, Math.Min(version, 4u));

				if (proxy == 0)
					return;

				var info = new OutputInfo { Proxy = proxy, Name = name };
				outputsByName[name] = info;
				outputsByProxy[proxy] = info;

				var data = GCHandle.Alloc(info);
				outputHandles.Add(data);
				_ = WaylandNative.ProxyAddListener(proxy, OutputListener.Pointer, GCHandle.ToIntPtr(data));
			}

			private static Session Self(nint data) => (Session)GCHandle.FromIntPtr(data).Target;
			private static OutputInfo Output(nint data) => (OutputInfo)GCHandle.FromIntPtr(data).Target;
			private static FrameState Frame(nint data) => (FrameState)GCHandle.FromIntPtr(data).Target;
			private static string Utf8(nint value) => Marshal.PtrToStringUTF8(value) ?? string.Empty;

			private static class RegistryListener
			{
				private static readonly GlobalHandler onGlobal = Global;
				private static readonly GlobalRemoveHandler onGlobalRemove = GlobalRemove;
				internal static readonly nint Pointer = ListenerBlock.Create(onGlobal, onGlobalRemove);

				private static void Global(nint data, nint registry, uint name, nint protocolInterface, uint version)
				{
					var session = Self(data);

					switch (Utf8(protocolInterface))
					{
						case ScreencopyManagerName: session.BindManager(name, version); break;
						case ShmName: session.BindShm(name, version); break;
						case OutputName: session.BindOutput(name, version); break;
					}
				}

				private static void GlobalRemove(nint data, nint registry, uint name) { }

				[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
				private delegate void GlobalHandler(nint data, nint registry, uint name, nint protocolInterface, uint version);
				[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
				private delegate void GlobalRemoveHandler(nint data, nint registry, uint name);
			}

			private static class OutputListener
			{
				// Six wl_output events through v2; later versions (v3 name, v4 description) sit on the
				// end and we leave their slots unhandled because we don't need them.
				private static readonly GeometryHandler onGeometry = (data, _, gx, gy, _, _, _, _, _, _) =>
				{
					var output = Output(data);
					output.X = gx;
					output.Y = gy;
				};

				private static readonly ModeHandler onMode = (data, _, flags, mw, mh, _) =>
				{
					var output = Output(data);

					if ((flags & 1) != 0 && mw > 0 && mh > 0)
					{
						output.Width = mw;
						output.Height = mh;
					}
				};

				private static readonly VoidHandler onDone = (data, _) => Output(data).Done = true;
				private static readonly ScaleHandler onScale = (data, _, _) => { };
				private static readonly StringHandler onName = (data, _, _) => { };
				private static readonly StringHandler onDescription = (data, _, _) => { };

				internal static readonly nint Pointer = ListenerBlock.Create(onGeometry, onMode, onDone, onScale, onName, onDescription);

				[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
				private delegate void GeometryHandler(nint data, nint output, int x, int y, int physicalWidth, int physicalHeight, int subpixel, nint make, nint model, int transform);
				[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
				private delegate void ModeHandler(nint data, nint output, uint flags, int width, int height, int refresh);
				[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
				private delegate void VoidHandler(nint data, nint output);
				[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
				private delegate void ScaleHandler(nint data, nint output, int factor);
				[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
				private delegate void StringHandler(nint data, nint output, nint value);
			}

			private static class FrameListener
			{
				private static readonly BufferHandler onBuffer = (data, _, format, width, height, stride) =>
				{
					var state = Frame(data);
					state.Format = format;
					state.Width = (int)width;
					state.Height = (int)height;
					state.Stride = (int)stride;
					state.BufferInfoReady = true;
				};

				private static readonly FlagsHandler onFlags = (data, _, flags) => Frame(data).Flags = flags;
				private static readonly ReadyHandler onReady = (data, _, _, _, _) => Frame(data).Ready = true;
				private static readonly VoidHandler onFailed = (data, _) => Frame(data).Failed = true;
				private static readonly DamageHandler onDamage = (data, _, _, _, _, _) => { };
				private static readonly DmabufHandler onDmabuf = (data, _, _, _, _) => { };
				private static readonly VoidHandler onBufferDone = (data, _) => { };

				internal static readonly nint Pointer = ListenerBlock.Create(onBuffer, onFlags, onReady, onFailed, onDamage, onDmabuf, onBufferDone);

				[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
				private delegate void BufferHandler(nint data, nint frame, uint format, uint width, uint height, uint stride);
				[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
				private delegate void FlagsHandler(nint data, nint frame, uint flags);
				[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
				private delegate void ReadyHandler(nint data, nint frame, uint tvSecHi, uint tvSecLo, uint tvNsec);
				[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
				private delegate void VoidHandler(nint data, nint frame);
				[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
				private delegate void DamageHandler(nint data, nint frame, uint x, uint y, uint width, uint height);
				[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
				private delegate void DmabufHandler(nint data, nint frame, uint format, uint width, uint height);
			}

			private static class ListenerBlock
			{
				internal static nint Create(params Delegate[] delegates)
				{
					var block = Marshal.AllocHGlobal(delegates.Length * IntPtr.Size);

					for (var i = 0; i < delegates.Length; i++)
						Marshal.WriteIntPtr(block, i * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(delegates[i]));

					return block;
				}
			}
		}

		// Local resource wrapper for the single-shot SHM buffer used during capture. Distinct from
		// WaylandShmBuffer because we don't need its wl_buffer.release listener and the stride
		// comes from the compositor (not assumed to be width*4).
		private sealed class ShmFrameBuffer : IDisposable
		{
			private int fd = -1;
			private nint mapping;
			private nuint mapLength;
			private nint pool;
			internal nint Buffer { get; private set; }
			internal nint Data => mapping;

			internal static ShmFrameBuffer Create(nint shm, int width, int height, int stride, uint format)
			{
				if (shm == 0 || width <= 0 || height <= 0 || stride < width * 4)
					return null;

				var size = (long)stride * height;

				if (size <= 0)
					return null;

				var fb = new ShmFrameBuffer();

				try
				{
					fb.fd = WaylandNative.MemfdCreate("keysharp-wlr-screencopy", WaylandNative.MFD_CLOEXEC);

					if (fb.fd < 0)
						return null;

					if (WaylandNative.Ftruncate(fb.fd, size) != 0)
						return null;

					fb.mapping = WaylandNative.Mmap(0, (nuint)size, WaylandNative.PROT_READ | WaylandNative.PROT_WRITE,
						WaylandNative.MAP_SHARED, fb.fd, 0);

					if (fb.mapping == WaylandNative.MAP_FAILED)
					{
						fb.mapping = 0;
						return null;
					}

					fb.mapLength = (nuint)size;
					fb.pool = WaylandNative.ShmCreatePool(shm, fb.fd, (int)size);

					if (fb.pool == 0)
						return null;

					fb.Buffer = WaylandNative.ShmPoolCreateBuffer(fb.pool, 0, width, height, stride, format);

					if (fb.Buffer == 0)
						return null;

					var keep = fb;
					fb = null;
					return keep;
				}
				finally
				{
					fb?.Dispose();
				}
			}

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

				if (mapping != 0)
				{
					_ = WaylandNative.Munmap(mapping, mapLength);
					mapping = 0;
				}

				if (fd >= 0)
				{
					_ = WaylandNative.Close(fd);
					fd = -1;
				}
			}
		}

		private static class ScreencopyInterfaces
		{
			// Wire descriptions for zwlr_screencopy_manager_v1 and zwlr_screencopy_frame_v1. Built
			// locally because libwayland only exports symbols for the core protocol.
			internal static readonly WaylandNative.ProtocolInterface Frame = new(ScreencopyFrameName, 3,
				[
					("copy", "o", [0]),
					("destroy", "", []),
					("copy_with_damage", "2o", [0])
				],
				[
					("buffer", "uuuu", []),
					("flags", "u", []),
					("ready", "uuu", []),
					("failed", "", []),
					("damage", "2uuuu", []),
					("linux_dmabuf", "3uuu", []),
					("buffer_done", "3", [])
				]);

			internal static readonly WaylandNative.ProtocolInterface Manager = new(ScreencopyManagerName, 3,
				[
					("capture_output", "nio", [Frame.Pointer, 0, 0]),
					("capture_output_region", "nioiiii", [Frame.Pointer, 0, 0]),
					("destroy", "3", [])
				],
				[]);
		}
	}
}
#endif
