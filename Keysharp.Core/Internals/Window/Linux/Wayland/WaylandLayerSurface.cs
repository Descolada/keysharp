using System.Runtime.InteropServices;
using System.Threading;

#if LINUX
namespace Keysharp.Internals.Window.Linux.Wayland
{
	/// <summary>
	/// One <c>zwlr_layer_surface_v1</c> wrapping its underlying <c>wl_surface</c>. Owns the
	/// configure/ack/commit dance and the input-region setup. Higher-level helpers (tooltip,
	/// cursor monitor) compose this with a <see cref="WaylandShmBuffer"/>.
	///
	/// All Wayland calls must be performed while holding <see cref="WaylandLayerShellClient.Sync"/>;
	/// the configure callback is invoked from the dispatcher thread under that same lock.
	/// </summary>
	internal sealed class WaylandLayerSurface : IDisposable
	{
		private readonly WaylandLayerShellClient client;
		private readonly GCHandle selfHandle;
		private readonly ManualResetEventSlim configuredEvent = new(false);
		private nint surface;
		private nint layerSurface;
		private uint pendingSerial;
		private bool ackPending;
		private bool disposed;

		internal int ConfiguredWidth { get; private set; }
		internal int ConfiguredHeight { get; private set; }
		internal bool IsConfigured { get; private set; }
		internal bool IsClosed { get; private set; }
		internal nint Surface => surface;

		internal event Action Configured;
		internal event Action Closed;

		internal WaylandLayerSurface(WaylandLayerShellClient client, uint layer, string ns)
		{
			this.client = client ?? throw new ArgumentNullException(nameof(client));
			selfHandle = GCHandle.Alloc(this);

			lock (WaylandLayerShellClient.Sync)
			{
				surface = WaylandNative.CompositorCreateSurface(client.Compositor);

				if (surface == 0)
					throw new IOException("wl_compositor.create_surface returned null.");

				layerSurface = WaylandNative.LayerShellGetLayerSurface(client.LayerShell, surface, 0, layer, ns ?? "keysharp");

				if (layerSurface == 0)
				{
					WaylandNative.SurfaceDestroy(surface);
					surface = 0;
					throw new IOException("zwlr_layer_shell_v1.get_layer_surface returned null.");
				}

				_ = WaylandNative.ProxyAddListener(layerSurface, LayerSurfaceListener.Pointer, GCHandle.ToIntPtr(selfHandle));
			}
		}

		internal void SetSize(uint width, uint height)
		{
			lock (WaylandLayerShellClient.Sync)
			{
				if (layerSurface != 0)
					WaylandNative.LayerSurfaceSetSize(layerSurface, width, height);
			}
		}

		internal void SetAnchor(uint anchor)
		{
			lock (WaylandLayerShellClient.Sync)
			{
				if (layerSurface != 0)
					WaylandNative.LayerSurfaceSetAnchor(layerSurface, anchor);
			}
		}

		internal void SetMargin(int top, int right, int bottom, int left)
		{
			lock (WaylandLayerShellClient.Sync)
			{
				if (layerSurface != 0)
					WaylandNative.LayerSurfaceSetMargin(layerSurface, top, right, bottom, left);
			}
		}

		internal void SetExclusiveZone(int zone)
		{
			lock (WaylandLayerShellClient.Sync)
			{
				if (layerSurface != 0)
					WaylandNative.LayerSurfaceSetExclusiveZone(layerSurface, zone);
			}
		}

		internal void SetKeyboardInteractivity(uint interactivity)
		{
			lock (WaylandLayerShellClient.Sync)
			{
				if (layerSurface != 0)
					WaylandNative.LayerSurfaceSetKeyboardInteractivity(layerSurface, interactivity);
			}
		}

		/// <summary>
		/// Replaces this surface's input region. Pass <c>null</c> to use libwayland's default
		/// (the buffer area receives all input); pass an empty region to make the surface input-
		/// transparent so clicks pass straight through to whatever is underneath. Tooltips want
		/// transparency; cursor monitors want default (full) input.
		/// </summary>
		internal void SetInputRegion(nint region)
		{
			lock (WaylandLayerShellClient.Sync)
			{
				if (surface != 0)
					WaylandNative.SurfaceSetInputRegion(surface, region);
			}
		}

		internal void AttachBuffer(WaylandShmBuffer buffer)
		{
			if (buffer == null)
				return;

			lock (WaylandLayerShellClient.Sync)
			{
				if (surface == 0)
					return;

				WaylandNative.SurfaceAttach(surface, buffer.Buffer, 0, 0);
				WaylandNative.SurfaceDamage(surface, 0, 0, buffer.Width, buffer.Height);
				buffer.MarkInFlight();
			}
		}

		internal void Commit()
		{
			lock (WaylandLayerShellClient.Sync)
			{
				// ack_configure MUST come before the wl_surface.commit that attaches a buffer
				// in response to it — the layer-shell spec is explicit that committing without
				// the ack causes the buffer to be ignored silently.
				if (ackPending && layerSurface != 0)
				{
					WaylandNative.LayerSurfaceAckConfigure(layerSurface, pendingSerial);
					ackPending = false;
				}

				if (surface != 0)
					WaylandNative.SurfaceCommit(surface);

				if (client.Display != 0)
					_ = WaylandNative.DisplayFlush(client.Display);
			}
		}

		/// <summary>
		/// Blocks until a configure event is processed (or until the timeout elapses, or the
		/// surface is closed). Returns true on configure, false on timeout/close. Always does a
		/// roundtrip so that subsequent calls — e.g. after we change set_size/set_margin and
		/// commit to trigger a fresh configure cycle — see the new configure rather than the
		/// stale "already configured once" flag. Must NOT be called while holding
		/// <see cref="WaylandLayerShellClient.Sync"/>; the dispatcher needs the lock to fire
		/// the configure callback.
		/// </summary>
		internal bool WaitForConfigure(int timeoutMs = 1000)
		{
			// Drain any pending configure events first. After a roundtrip every event the
			// compositor sent before the sync reply has been dispatched, so IsConfigured /
			// ackPending reflect the latest state.
			lock (WaylandLayerShellClient.Sync)
				client.Roundtrip();

			if (IsConfigured)
				return true;

			return configuredEvent.Wait(timeoutMs) && IsConfigured;
		}

		// --- listener callback ---

		private void OnConfigure(uint serial, uint width, uint height)
		{
			pendingSerial = serial;
			ackPending = true;
			ConfiguredWidth = (int)width;
			ConfiguredHeight = (int)height;
			IsConfigured = true;
			configuredEvent.Set();

			try { Configured?.Invoke(); }
			catch { }
		}

		private void OnClosed()
		{
			IsClosed = true;
			configuredEvent.Set();

			try { Closed?.Invoke(); }
			catch { }
		}

		public void Dispose()
		{
			if (disposed)
				return;

			disposed = true;
			configuredEvent.Set();
			configuredEvent.Dispose();

			lock (WaylandLayerShellClient.Sync)
			{
				if (layerSurface != 0)
				{
					WaylandNative.LayerSurfaceDestroy(layerSurface);
					layerSurface = 0;
				}

				if (surface != 0)
				{
					WaylandNative.SurfaceDestroy(surface);
					surface = 0;
				}

				if (client.Display != 0)
					_ = WaylandNative.DisplayFlush(client.Display);
			}

			if (selfHandle.IsAllocated)
				selfHandle.Free();
		}

		private static WaylandLayerSurface Self(nint data) => (WaylandLayerSurface)GCHandle.FromIntPtr(data).Target;

		private static class LayerSurfaceListener
		{
			private static readonly ConfigureHandler onConfigure = Configure;
			private static readonly ClosedHandler onClosed = ClosedEvent;
			internal static readonly nint Pointer = Build();

			private static nint Build()
			{
				var block = Marshal.AllocHGlobal(IntPtr.Size * 2);
				Marshal.WriteIntPtr(block, 0, Marshal.GetFunctionPointerForDelegate(onConfigure));
				Marshal.WriteIntPtr(block, IntPtr.Size, Marshal.GetFunctionPointerForDelegate(onClosed));
				return block;
			}

			private static void Configure(nint data, nint layerSurface, uint serial, uint width, uint height)
				=> Self(data).OnConfigure(serial, width, height);

			private static void ClosedEvent(nint data, nint layerSurface)
				=> Self(data).OnClosed();

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			private delegate void ConfigureHandler(nint data, nint layerSurface, uint serial, uint width, uint height);
			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			private delegate void ClosedHandler(nint data, nint layerSurface);
		}
	}
}
#endif
