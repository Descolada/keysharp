using System.Runtime.InteropServices;
using System.Threading;

#if LINUX
namespace Keysharp.Internals.Window.Linux.Wayland
{
	/// <summary>
	/// Dedicated Wayland connection that owns the bindings needed to create our own surfaces
	/// (wl_compositor, wl_shm, wl_seat, wl_output) and the zwlr_layer_shell_v1 extension. Kept
	/// separate from <see cref="WaylandForeignToplevels"/> so the two don't have to share the
	/// libwayland event queue or fight over registry-listener state.
	///
	/// Thread safety: every libwayland call funnels through <see cref="Sync"/>; one background
	/// thread drains incoming events every ~10 ms while callers acquire the same lock to send
	/// requests. libwayland-client itself is not thread-safe, so all proxies must be touched
	/// while holding the lock.
	/// </summary>
	internal sealed class WaylandLayerShellClient : IDisposable
	{
		private static readonly object sync = new();
		private static WaylandLayerShellClient current;
		private static bool initAttempted;

		internal static object Sync => sync;

		/// <summary>Returns the singleton instance, lazily connecting on first access. Null if
		/// the session is not Wayland or the compositor lacks zwlr_layer_shell_v1.</summary>
		internal static WaylandLayerShellClient Current
		{
			get
			{
				lock (sync)
				{
					if (current != null || initAttempted)
						return current;

					initAttempted = true;
					current = TryCreate();
					return current;
				}
			}
		}

		internal nint Display { get; private set; }
		internal nint Compositor { get; private set; }
		internal nint Shm { get; private set; }
		internal nint Seat { get; private set; }
		internal nint Output { get; private set; }
		internal nint LayerShell { get; private set; }
		internal uint CompositorVersion { get; private set; }
		internal uint LayerShellVersion { get; private set; }
		internal uint SeatVersion { get; private set; }

		private nint registry;
		private readonly GCHandle selfHandle;
		private CancellationTokenSource dispatcherCancel;
		private Thread dispatcherThread;
		private volatile bool disposed;

		private WaylandLayerShellClient()
		{
			selfHandle = GCHandle.Alloc(this);
		}

		internal bool IsAvailable => LayerShell != 0 && Compositor != 0 && Shm != 0;

		private static WaylandLayerShellClient TryCreate()
		{
			if (!string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase))
				return null;

			var display = WaylandNative.DisplayConnect(null);

			if (display == 0)
				return null;

			var client = new WaylandLayerShellClient { Display = display };

			try
			{
				client.registry = WaylandNative.DisplayGetRegistry(display);

				if (client.registry == 0)
					throw new IOException("wl_display.get_registry returned null.");

				if (WaylandNative.ProxyAddListener(client.registry, RegistryListener.Pointer, GCHandle.ToIntPtr(client.selfHandle)) != 0)
					throw new IOException("wl_proxy_add_listener for registry failed.");

				// First roundtrip surfaces the available globals; the second processes events from
				// objects we bound inside the first one (none today, but cheap insurance against
				// compositors that delay format announcements until after the bind).
				_ = WaylandNative.DisplayRoundtrip(display);
				_ = WaylandNative.DisplayRoundtrip(display);

				if (!client.IsAvailable)
				{
					client.Dispose();
					return null;
				}

				client.StartDispatcher();
				return client;
			}
			catch
			{
				client.Dispose();
				return null;
			}
		}

		private void StartDispatcher()
		{
			dispatcherCancel = new CancellationTokenSource();
			var token = dispatcherCancel.Token;
			dispatcherThread = new Thread(() => DispatchLoop(token))
			{
				IsBackground = true,
				Name = "KeysharpWaylandLayerShell"
			};
			dispatcherThread.Start();
		}

		private void DispatchLoop(CancellationToken token)
		{
			while (!token.IsCancellationRequested)
			{
				try
				{
					lock (sync)
					{
						if (disposed || Display == 0)
							return;

						_ = WaylandNative.DisplayDispatchPending(Display);
						_ = WaylandNative.DisplayFlush(Display);
					}
				}
				catch
				{
					// Swallow; the next iteration retries. We don't want a transient libwayland
					// failure to take down the dispatcher thread permanently.
				}

				try { Thread.Sleep(10); }
				catch (ThreadInterruptedException) { }
			}
		}

		/// <summary>Forces a synchronous round-trip so any pending requests reach the server and
		/// pending events are dispatched on the caller's thread. Callers should already hold
		/// <see cref="Sync"/>.</summary>
		internal void Roundtrip()
		{
			if (Display != 0)
				_ = WaylandNative.DisplayRoundtrip(Display);
		}

		// --- registry handling ---

		private void OnGlobal(uint name, string interfaceName, uint version)
		{
			switch (interfaceName)
			{
				case "wl_compositor":
					if (Compositor == 0)
					{
						var bound = Math.Min(version, 5u);
						Compositor = WaylandNative.RegistryBind(registry, name, WaylandNative.CompositorInterface, "wl_compositor", bound);
						CompositorVersion = bound;
					}
					break;

				case "wl_shm":
					if (Shm == 0)
						Shm = WaylandNative.RegistryBind(registry, name, WaylandNative.ShmInterface, "wl_shm", Math.Min(version, 1u));
					break;

				case "wl_seat":
					if (Seat == 0)
					{
						var bound = Math.Min(version, 8u);
						Seat = WaylandNative.RegistryBind(registry, name, WaylandNative.SeatInterface, "wl_seat", bound);
						SeatVersion = bound;
					}
					break;

				case "wl_output":
					if (Output == 0)
						Output = WaylandNative.RegistryBind(registry, name, WaylandNative.OutputInterface, "wl_output", Math.Min(version, 3u));
					break;

				case "zwlr_layer_shell_v1":
					if (LayerShell == 0)
					{
						var bound = Math.Min(version, 4u);
						LayerShell = WaylandNative.RegistryBind(registry, name, WaylandNative.Interfaces.WlrLayerShell, bound);
						LayerShellVersion = bound;
					}
					break;
			}
		}

		private static WaylandLayerShellClient Self(nint data) => (WaylandLayerShellClient)GCHandle.FromIntPtr(data).Target;
		private static string Utf8(nint value) => Marshal.PtrToStringUTF8(value) ?? string.Empty;

		private static class RegistryListener
		{
			private static readonly GlobalHandler onGlobal = Global;
			private static readonly GlobalRemoveHandler onGlobalRemove = GlobalRemove;
			internal static readonly nint Pointer = Build();

			private static nint Build()
			{
				var block = Marshal.AllocHGlobal(IntPtr.Size * 2);
				Marshal.WriteIntPtr(block, 0, Marshal.GetFunctionPointerForDelegate(onGlobal));
				Marshal.WriteIntPtr(block, IntPtr.Size, Marshal.GetFunctionPointerForDelegate(onGlobalRemove));
				return block;
			}

			private static void Global(nint data, nint registry, uint name, nint protocolInterface, uint version)
				=> Self(data).OnGlobal(name, Utf8(protocolInterface), version);

			private static void GlobalRemove(nint data, nint registry, uint name) { }

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			private delegate void GlobalHandler(nint data, nint registry, uint name, nint protocolInterface, uint version);
			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			private delegate void GlobalRemoveHandler(nint data, nint registry, uint name);
		}

		public void Dispose()
		{
			lock (sync)
			{
				disposed = true;

				if (ReferenceEquals(current, this))
					current = null;
			}

			try { dispatcherCancel?.Cancel(); } catch { }
			try { dispatcherThread?.Join(100); } catch { }
			dispatcherCancel?.Dispose();
			dispatcherCancel = null;
			dispatcherThread = null;

			lock (sync)
			{
				if (LayerShell != 0)
				{
					if (LayerShellVersion >= 3)
						WaylandNative.LayerShellDestroy(LayerShell);
					else
						WaylandNative.ProxyDestroy(LayerShell);

					LayerShell = 0;
				}

				if (Output != 0) { WaylandNative.ProxyDestroy(Output); Output = 0; }
				if (Seat != 0) { WaylandNative.ProxyDestroy(Seat); Seat = 0; }
				if (Shm != 0) { WaylandNative.ProxyDestroy(Shm); Shm = 0; }
				if (Compositor != 0) { WaylandNative.ProxyDestroy(Compositor); Compositor = 0; }
				if (registry != 0) { WaylandNative.ProxyDestroy(registry); registry = 0; }

				if (Display != 0)
				{
					WaylandNative.DisplayDisconnect(Display);
					Display = 0;
				}
			}

			if (selfHandle.IsAllocated)
				selfHandle.Free();
		}
	}
}
#endif
