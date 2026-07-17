using System.Runtime.InteropServices;
using System.Threading;

#if LINUX
namespace Keysharp.Internals.Window.Linux.Wayland
{
	/// <summary>
	/// Dedicated Wayland connection for layer-shell overlays. Besides the surface factories it owns a live
	/// wl_output + xdg-output topology, so global Keysharp screen coordinates can be resolved to one explicit output
	/// and output-local layer margins. All proxy access is serialized by <see cref="Sync"/>.
	/// </summary>
	internal sealed class WaylandLayerShellClient : IDisposable
	{
		internal readonly record struct OutputTarget(uint RegistryName, nint Proxy, ScreenRect Bounds,
			double BufferScale, int IntegerScale);
		internal readonly record struct OutputSegment(OutputTarget Output, ScreenRect Bounds, int SourceOffsetX,
			int SourceOffsetY);

		private static readonly object sync = new();
		private static WaylandLayerShellClient current;
		private static int consecutiveProbeFailures;
		private static long nextProbeAt;

		internal static object Sync => sync;

		internal static WaylandLayerShellClient Current
		{
			get
			{
				WaylandLayerShellClient stale = null;

				lock (sync)
				{
					if (current != null && !current.IsAvailable)
					{
						stale = current;
						current = null;
					}

					if (current != null)
						return current;
				}

				// Joining the stale dispatcher while holding Sync can deadlock it between poll and dispatch.
				stale?.Dispose();

				lock (sync)
				{
					if (current != null)
						return current;

					var now = Environment.TickCount64;

					if (now < nextProbeAt)
						return null;

					current = TryCreate();

					if (current != null)
					{
						consecutiveProbeFailures = 0;
						nextProbeAt = 0;
					}
					else
					{
						consecutiveProbeFailures = Math.Min(8, consecutiveProbeFailures + 1);
						nextProbeAt = now + WaylandRetryPolicy.DelayMilliseconds(consecutiveProbeFailures);
					}

					return current;
				}
			}
		}

		internal nint Display { get; private set; }
		internal nint Compositor { get; private set; }
		internal nint Shm { get; private set; }
		internal nint LayerShell { get; private set; }
		internal nint Viewporter { get; private set; }
		internal uint LayerShellVersion { get; private set; }

		private readonly Dictionary<uint, WaylandOutput> outputs = [];
		private readonly HashSet<WaylandImageOverlay> children = [];
		private nint registry;
		private nint xdgOutputManager;
		private readonly GCHandle selfHandle;
		private CancellationTokenSource dispatcherCancel;
		private Thread dispatcherThread;
		private volatile bool disposed;
		private volatile bool connectionLost;

		private WaylandLayerShellClient() => selfHandle = GCHandle.Alloc(this);

		internal bool IsAvailable => !disposed && !connectionLost && Display != 0
			&& LayerShell != 0 && Compositor != 0 && Shm != 0;

		internal bool Register(WaylandImageOverlay child)
		{
			lock (sync)
			{
				if (!IsAvailable || child == null)
					return false;

				return children.Add(child);
			}
		}

		internal void Unregister(WaylandImageOverlay child)
		{
			lock (sync)
				_ = children.Remove(child);
		}

		internal IReadOnlyList<DisplayInfo> GetDisplays()
		{
			lock (sync)
			{
				return outputs.Values
					.Where(o => o.Proxy != 0 && o.Bounds.HasArea)
					.OrderBy(o => o.RegistryName)
					.Select(o => new DisplayInfo(o.StableName, o.Bounds, o.Bounds, 1.0,
						o.Bounds.X == 0 && o.Bounds.Y == 0, o.RegistryName))
					.ToArray();
			}
		}

		internal bool TryResolveOutput(ScreenRect bounds, out OutputTarget target)
		{
			lock (sync)
			{
				var displays = outputs.Values
					.Where(o => o.Proxy != 0 && o.Bounds.HasArea)
					.Select(o => new DisplayInfo(o.StableName, o.Bounds, o.Bounds, 1.0,
						o.Bounds.X == 0 && o.Bounds.Y == 0, o.RegistryName))
					.ToArray();

				if (DisplayTopology.TryFind(displays, bounds, out var selected)
					&& outputs.TryGetValue((uint)selected.NativeId, out var output))
				{
					target = new OutputTarget(output.RegistryName, output.Proxy, output.Bounds,
						output.BufferScale, Math.Max(1, output.IntegerScale));
					return true;
				}

				// A compositor can configure a layer surface before output metadata is complete. Still select a real
				// wl_output rather than null; the next Show will use its finished logical geometry.
				var fallback = outputs.Values.FirstOrDefault(o => o.Proxy != 0);

				if (fallback != null)
				{
					target = new OutputTarget(fallback.RegistryName, fallback.Proxy, fallback.Bounds,
						fallback.BufferScale, Math.Max(1, fallback.IntegerScale));
					return true;
				}

				target = default;
				return false;
			}
		}

		/// <summary>
		/// Splits a global rectangle at output boundaries. Layer-shell surfaces are permanently assigned to one
		/// wl_output and are clipped there, so a spanning overlay needs one surface per returned segment.
		/// </summary>
		internal IReadOnlyList<OutputSegment> GetOutputSegments(ScreenRect bounds)
		{
			lock (sync)
			{
				var segments = new List<OutputSegment>(Math.Min(outputs.Count, 4));

				foreach (var output in outputs.Values)
				{
					if (output.Proxy == 0 || !output.Bounds.HasArea)
						continue;

					var ob = output.Bounds;
					var left = Math.Max(bounds.X, ob.X);
					var top = Math.Max(bounds.Y, ob.Y);
					var right = Math.Min((long)bounds.X + bounds.Width, (long)ob.X + ob.Width);
					var bottom = Math.Min((long)bounds.Y + bounds.Height, (long)ob.Y + ob.Height);

					if (right <= left || bottom <= top)
						continue;

					var segmentBounds = new ScreenRect(left, top, (int)(right - left), (int)(bottom - top));
					var target = new OutputTarget(output.RegistryName, output.Proxy, ob, output.BufferScale,
						Math.Max(1, output.IntegerScale));
					segments.Add(new OutputSegment(target, segmentBounds,
						left - bounds.X, top - bounds.Y));
				}

				if (segments.Count > 0)
					return segments;

				// Preserve the historical nearest-output behavior for an entirely off-desktop rectangle. It will be
				// clipped by that output, but remains movable back on screen instead of failing to create a backing.
				if (TryResolveOutput(bounds, out var nearest))
					segments.Add(new OutputSegment(nearest, bounds, 0, 0));

				return segments;
			}
		}

		/// <summary>Checks a captured output identity and geometry against the live topology.</summary>
		internal bool IsOutputCurrent(OutputTarget target)
		{
			lock (sync)
				return !disposed && target.Proxy != 0 && outputs.TryGetValue(target.RegistryName, out var output)
					&& output.Proxy == target.Proxy && output.Bounds == target.Bounds
					&& output.IntegerScale == target.IntegerScale && output.BufferScale == target.BufferScale;
		}

		private static WaylandLayerShellClient TryCreate()
		{
			if (!Platform.Desktop.IsWaylandSession)
				return null;

			var display = WaylandNative.DisplayConnect(null);

			if (display == 0)
				return null;

			var client = new WaylandLayerShellClient { Display = display };

			try
			{
				client.registry = WaylandNative.DisplayGetRegistry(display);

				if (client.registry == 0
					|| WaylandNative.ProxyAddListener(client.registry, RegistryListener.Pointer,
						GCHandle.ToIntPtr(client.selfHandle)) != 0)
					throw new IOException("wl_registry listener setup failed.");

				for (var i = 0; i < 8 && !client.IsAvailable; i++)
					if (WaylandNative.DisplayRoundtrip(display) < 0)
						throw new IOException("wl_display.roundtrip failed.");

				if (!client.IsAvailable)
				{
					client.Dispose();
					return null;
				}

				// Registry globals arrive on the first trip; output/xdg-output properties are child-object events and
				// need another trip before the first overlay is placed.
				if (WaylandNative.DisplayRoundtrip(display) < 0)
					throw new IOException("wl_display output roundtrip failed.");

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
			try
			{
				var pollFds = new WaylandNative.PollFd[1];

				while (!token.IsCancellationRequested)
				{
					var readPrepared = false;
					try
					{
						int fd;

						lock (sync)
						{
							if (disposed || Display == 0)
								return;

							while (WaylandNative.DisplayPrepareRead(Display) != 0)
								if (WaylandNative.DisplayDispatchPending(Display) < 0)
									return;

							readPrepared = true;
							_ = WaylandNative.DisplayFlush(Display);
							fd = WaylandNative.DisplayGetFd(Display);
							pollFds[0] = new WaylandNative.PollFd
							{
								FileDescriptor = fd,
								Events = WaylandNative.POLLIN
							};
						}

						// Requests may be marshalled and flushed while a read is prepared; this dispatcher remains
						// the sole event reader. Polling outside Sync removes animation latency and idle busy-waiting.
						var ready = fd >= 0 ? WaylandNative.Poll(pollFds, 1, 100) : -1;

						lock (sync)
						{
							if (disposed || Display == 0)
							{
								if (readPrepared && Display != 0)
									WaylandNative.DisplayCancelRead(Display);
								return;
							}

							if (ready > 0 && (pollFds[0].ReturnedEvents & WaylandNative.POLLIN) != 0)
							{
								var readResult = WaylandNative.DisplayReadEvents(Display);
								readPrepared = false;

								if (readResult < 0)
									return;
							}
							else
							{
								WaylandNative.DisplayCancelRead(Display);
								readPrepared = false;
							}

							if (WaylandNative.DisplayDispatchPending(Display) < 0)
								return;
						}
					}
					catch
					{
						if (readPrepared)
							try
							{
								lock (sync)
									if (Display != 0) WaylandNative.DisplayCancelRead(Display);
							}
							catch { }
					}
				}
			}
			finally
			{
				if (!token.IsCancellationRequested)
					connectionLost = true;
			}
		}

		internal bool TryFlush()
		{
			if (Display == 0)
				return false;

			if (WaylandNative.DisplayFlush(Display) >= 0)
				return true;

			// EAGAIN means the request is queued in libwayland and the dispatcher will flush it when the socket is
			// writable; it is not a lost transaction. Every other error is terminal for this connection.
			if (Marshal.GetLastPInvokeError() == 11)
				return true;

			connectionLost = true;
			return false;
		}

		private void OnGlobal(uint name, string interfaceName, uint version)
		{
			switch (interfaceName)
			{
				case "wl_compositor" when Compositor == 0:
					var compositorVersion = Math.Min(version, 5u);
					Compositor = WaylandNative.RegistryBind(registry, name, WaylandNative.CompositorInterface,
						"wl_compositor", compositorVersion);
					break;

				case "wl_shm" when Shm == 0:
					Shm = WaylandNative.RegistryBind(registry, name, WaylandNative.ShmInterface, "wl_shm", 1);
					break;

				case "wl_output":
					BindOutput(name, version);
					break;

				case "zxdg_output_manager_v1" when xdgOutputManager == 0:
					xdgOutputManager = WaylandNative.RegistryBind(registry, name,
						WaylandNative.Interfaces.XdgOutputManager, Math.Min(version, 3u));

					foreach (var output in outputs.Values)
						WaylandOutputBinding.BindXdgOutput(output, xdgOutputManager);
					break;

				case "wp_viewporter" when Viewporter == 0:
					Viewporter = WaylandNative.RegistryBind(registry, name,
						WaylandNative.Interfaces.WpViewporter, Math.Min(version, 1u));
					break;

				case "zwlr_layer_shell_v1" when LayerShell == 0:
					LayerShellVersion = Math.Min(version, 4u);
					LayerShell = WaylandNative.RegistryBind(registry, name,
						WaylandNative.Interfaces.WlrLayerShell, LayerShellVersion);
					break;
			}
		}

		private void BindOutput(uint name, uint version)
		{
			if (outputs.ContainsKey(name))
				return;

			var output = WaylandOutputBinding.Bind(registry, name, version, xdgOutputManager);

			if (output != null)
				outputs.Add(name, output);
		}

		private void OnGlobalRemove(uint name) => RemoveOutput(name);

		private void RemoveOutput(uint name)
		{
			if (!outputs.Remove(name, out var output))
				return;

			WaylandOutputBinding.Release(output);
		}

		private static WaylandLayerShellClient Self(nint data)
			=> (WaylandLayerShellClient)GCHandle.FromIntPtr(data).Target;
		private static string Utf8(nint value) => Marshal.PtrToStringUTF8(value) ?? string.Empty;

		private static class RegistryListener
		{
			private static readonly GlobalHandler onGlobal = Global;
			private static readonly GlobalRemoveHandler onGlobalRemove = GlobalRemove;
			internal static readonly nint Pointer = WaylandListenerTable.Allocate(onGlobal, onGlobalRemove);

			private static void Global(nint data, nint registry, uint name, nint protocolInterface, uint version)
				=> Self(data).OnGlobal(name, Utf8(protocolInterface), version);
			private static void GlobalRemove(nint data, nint registry, uint name) => Self(data).OnGlobalRemove(name);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			private delegate void GlobalHandler(nint data, nint registry, uint name, nint protocolInterface, uint version);
			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			private delegate void GlobalRemoveHandler(nint data, nint registry, uint name);
		}

		public void Dispose()
		{
			WaylandImageOverlay[] liveChildren;

			lock (sync)
			{
				if (disposed)
					return;

				disposed = true;
				if (ReferenceEquals(current, this)) current = null;
				liveChildren = children.ToArray();
			}

			try { dispatcherCancel?.Cancel(); } catch { }
			var dispatcherStopped = true;
			try { dispatcherStopped = dispatcherThread?.Join(1000) ?? true; } catch { dispatcherStopped = false; }

			// Never free proxies/listener handles while the dispatch thread could still be inside libwayland. Its bounded
			// poll wakes for cancellation, so this is only a defensive connection leak on a genuinely wedged native call.
			if (!dispatcherStopped)
				return;
			dispatcherCancel?.Dispose();
			dispatcherCancel = null;
			dispatcherThread = null;

			// Children retain raw wl_proxy pointers. Invalidate every child before display_disconnect so a later
			// Overlay.Dispose cannot marshal through freed memory. On connection loss the child abandons protocol
			// objects locally and force-frees its SHM mappings because wl_buffer.release can no longer arrive.
			foreach (var child in liveChildren)
				try { child.InvalidateConnection(connectionLost); }
				catch { }

			lock (sync)
			{
				children.Clear();

				if (connectionLost)
				{
					foreach (var output in outputs.Values)
						WaylandOutputBinding.Abandon(output);

					outputs.Clear();
					Viewporter = xdgOutputManager = LayerShell = Shm = Compositor = registry = 0;

					if (Display != 0)
					{
						WaylandNative.DisplayDisconnect(Display);
						Display = 0;
					}

					if (selfHandle.IsAllocated) selfHandle.Free();
					return;
				}

				foreach (var name in outputs.Keys.ToArray())
					RemoveOutput(name);

				if (Viewporter != 0) { WaylandNative.ViewporterDestroy(Viewporter); Viewporter = 0; }
				if (xdgOutputManager != 0) { WaylandNative.XdgOutputManagerDestroy(xdgOutputManager); xdgOutputManager = 0; }

				if (LayerShell != 0)
				{
					if (LayerShellVersion >= 3) WaylandNative.LayerShellDestroy(LayerShell);
					else WaylandNative.ProxyDestroy(LayerShell);
					LayerShell = 0;
				}

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
