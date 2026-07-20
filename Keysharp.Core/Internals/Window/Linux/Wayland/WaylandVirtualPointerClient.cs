using System.Runtime.InteropServices;
using System.Threading;

#if LINUX
namespace Keysharp.Internals.Window.Linux.Wayland
{
	/// <summary>
	/// Dedicated Wayland connection driving zwlr_virtual_pointer_v1 (wlr-virtual-pointer-unstable-v1) for
	/// compositors that don't offer their own privileged input-injection IPC (Sway, Hyprland, COSMIC). One
	/// long-lived pointer object is created for the client's whole lifetime; requests are one-way/fire-and-forget
	/// so a persistent background-dispatched connection avoids per-call connect/handshake latency. All proxy
	/// access is serialized by <see cref="sync"/>. Injection-only: this protocol cannot report cursor position.
	/// </summary>
	internal sealed class WaylandVirtualPointerClient : IDisposable
	{
		private static readonly object sync = new();
		private static readonly RetryGate probes = new(maximumAttempts: 3,
			initialRetryDelay: TimeSpan.FromMilliseconds(200), maximumRetryDelay: TimeSpan.FromSeconds(2));
		private static WaylandVirtualPointerClient current;

		internal static WaylandVirtualPointerClient Current
		{
			get
			{
				WaylandVirtualPointerClient stale = null;

				lock (sync)
				{
					if (current != null && !current.IsAvailable)
					{
						stale = current;
						current = null;
						probes.Rearm();
					}

					if (current != null)
						return current;
				}

				// Joining the stale dispatcher while holding sync can deadlock it between poll and dispatch.
				stale?.Dispose();

				lock (sync)
				{
					if (current != null)
						return current;

					using var attempt = probes.TryBegin();

					if (attempt == null)
						return null;

					current = TryCreate(out var unavailable);

					if (current != null)
						attempt.Succeed();
					else if (unavailable)
						probes.Suspend();

					return current;
				}
			}
		}

		internal nint Display { get; private set; }
		internal nint Manager { get; private set; }
		internal uint ManagerVersion { get; private set; }
		internal nint Seat { get; private set; }
		internal nint Pointer { get; private set; }

		private nint registry;
		private readonly GCHandle selfHandle;
		private CancellationTokenSource dispatcherCancel;
		private Thread dispatcherThread;
		private volatile bool disposed;
		private volatile bool connectionLost;

		private WaylandVirtualPointerClient() => selfHandle = GCHandle.Alloc(this);

		internal bool IsAvailable => !disposed && !connectionLost && Display != 0 && Manager != 0 && Pointer != 0;

		internal static void Reset()
		{
			WaylandVirtualPointerClient retired;

			lock (sync)
			{
				retired = current;
				current = null;
				probes.Rearm();
			}

			retired?.Dispose();
		}

		private static WaylandVirtualPointerClient TryCreate(out bool unavailable)
		{
			unavailable = false;

			if (!Platform.Desktop.IsWaylandSession)
			{
				unavailable = true;
				return null;
			}

			var display = WaylandNative.DisplayConnect(null);

			if (display == 0)
				return null;

			var client = new WaylandVirtualPointerClient { Display = display };

			try
			{
				client.registry = WaylandNative.DisplayGetRegistry(display);

				if (client.registry == 0
					|| WaylandNative.ProxyAddListener(client.registry, RegistryListener.Pointer,
						GCHandle.ToIntPtr(client.selfHandle)) != 0)
					throw new IOException("wl_registry listener setup failed.");

				for (var i = 0; i < 8 && client.Manager == 0; i++)
					if (WaylandNative.DisplayRoundtrip(display) < 0)
						throw new IOException("wl_display.roundtrip failed.");

				if (client.Manager == 0)
				{
					// The compositor's registry does not advertise zwlr_virtual_pointer_manager_v1 -- a stable
					// capability absence for this compositor generation/build, not a transport retry.
					unavailable = true;
					client.Dispose();
					return null;
				}

				// wl_seat may still be pending; create_virtual_pointer accepts a null seat (compositor default).
				client.Pointer = WaylandNative.VirtualPointerManagerCreateVirtualPointer(client.Manager, client.Seat);

				if (client.Pointer == 0)
					throw new IOException("zwlr_virtual_pointer_manager_v1.create_virtual_pointer failed.");

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
				Name = "KeysharpWaylandVirtualPointer"
			};
			dispatcherThread.Start();
		}

		// zwlr_virtual_pointer_v1 has no events, so this loop only needs to detect disconnects/errors --
		// there is nothing for DispatchPending to actually deliver to this client's own proxies.
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
						// the sole event reader. Polling outside sync removes latency on high-frequency mouse moves.
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

		private bool TryFlush()
		{
			if (Display == 0)
				return false;

			if (WaylandNative.DisplayFlush(Display) >= 0)
				return true;

			// EAGAIN means the request is queued in libwayland and the dispatcher will flush it when the
			// socket is writable; it is not a lost transaction. Every other error is terminal for this connection.
			if (Marshal.GetLastPInvokeError() == 11)
				return true;

			connectionLost = true;
			return false;
		}

		internal bool TryMoveAbsolute(int screenX, int screenY)
		{
			try
			{
				lock (sync)
				{
					if (!IsAvailable)
						return false;

					var vb = Keysharp.Builtins.Monitor.GetVirtualScreenBounds();
					var (x, y, xExtent, yExtent) = WaylandVirtualPointerCoordinates.ToMotionAbsolute(
						screenX, screenY, (int)vb.Left, (int)vb.Top, (int)vb.Width, (int)vb.Height);

					WaylandNative.VirtualPointerMotionAbsolute(Pointer, TimestampMs(), x, y, xExtent, yExtent);
					WaylandNative.VirtualPointerFrame(Pointer);
					return TryFlush();
				}
			}
			catch { connectionLost = true; return false; }
		}

		// No current caller sends a relative move; implemented only for IWaylandBackend interface parity
		// with the KWin/GNOME/Cinnamon backends.
		internal bool TryMoveRelative(int dx, int dy)
		{
			try
			{
				lock (sync)
				{
					if (!IsAvailable)
						return false;

					WaylandNative.VirtualPointerMotion(Pointer, TimestampMs(), dx, dy);
					WaylandNative.VirtualPointerFrame(Pointer);
					return TryFlush();
				}
			}
			catch { connectionLost = true; return false; }
		}

		internal bool TryButton(uint evdevButton, bool pressed)
		{
			try
			{
				lock (sync)
				{
					if (!IsAvailable)
						return false;

					WaylandNative.VirtualPointerButton(Pointer, TimestampMs(), evdevButton, pressed);
					WaylandNative.VirtualPointerFrame(Pointer);
					return TryFlush();
				}
			}
			catch { connectionLost = true; return false; }
		}

		internal bool TryScroll(int delta, bool vertical)
		{
			try
			{
				lock (sync)
				{
					if (!IsAvailable)
						return false;

					// Mirrors KWinBackend.TrySendMouseScroll: both this protocol and KWin's FakeInput produce a real
					// wl_pointer.axis event using "positive = scrolls down/right", inverted from AHK/Keysharp's
					// "positive delta = up" convention on the vertical axis; horizontal direction matches as-is.
					var axis = vertical ? WaylandNative.AxisVerticalScroll : WaylandNative.AxisHorizontalScroll;
					var value = vertical ? -(double)delta / 120.0 : (double)delta / 120.0;
					WaylandNative.VirtualPointerAxis(Pointer, TimestampMs(), axis, value);
					WaylandNative.VirtualPointerFrame(Pointer);
					return TryFlush();
				}
			}
			catch { connectionLost = true; return false; }
		}

		private static uint TimestampMs() => unchecked((uint)Environment.TickCount64);

		// Linux evdev button codes (BTN_LEFT/RIGHT/MIDDLE/BACK/FORWARD). Mirrors the private copy already
		// carried by KWinBackend (WaylandBackend.cs) -- not shared across backends, since this is the only
		// other consumer and a cross-cutting refactor isn't worth it for four constants.
		private const uint BtnLeft = 0x110;
		private const uint BtnRight = 0x111;
		private const uint BtnMiddle = 0x112;
		private const uint BtnForward = 0x115;
		private const uint BtnBack = 0x116;

		internal static uint MapX11ButtonToEvdev(uint x11Button) => x11Button switch
		{
			1 => BtnLeft,
			2 => BtnMiddle,
			3 => BtnRight,
			8 => BtnBack,
			9 => BtnForward,
			_ => 0u
		};

		private void OnGlobal(uint name, string interfaceName, uint version)
		{
			switch (interfaceName)
			{
				case "wl_seat" when Seat == 0:
					Seat = WaylandNative.RegistryBind(registry, name, WaylandNative.SeatInterface, "wl_seat", Math.Min(version, 8u));
					break;

				case WaylandNative.Interfaces.WlrVirtualPointerManagerName when Manager == 0:
					ManagerVersion = Math.Min(version, 2u);
					Manager = WaylandNative.RegistryBind(registry, name,
						WaylandNative.Interfaces.WlrVirtualPointerManager, ManagerVersion);
					break;
			}
		}

		private void OnGlobalRemove(uint name) { }

		private static WaylandVirtualPointerClient Self(nint data)
			=> (WaylandVirtualPointerClient)GCHandle.FromIntPtr(data).Target;
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
			lock (sync)
			{
				if (disposed)
					return;

				disposed = true;
				if (ReferenceEquals(current, this)) current = null;
			}

			try { dispatcherCancel?.Cancel(); } catch { }
			var dispatcherStopped = true;
			try { dispatcherStopped = dispatcherThread?.Join(1000) ?? true; } catch { dispatcherStopped = false; }

			// Never free proxies/listener handles while the dispatch thread could still be inside libwayland. Its
			// bounded poll wakes for cancellation, so this is only a defensive connection leak on a wedged native call.
			if (!dispatcherStopped)
				return;
			dispatcherCancel?.Dispose();
			dispatcherCancel = null;
			dispatcherThread = null;

			lock (sync)
			{
				if (connectionLost)
				{
					Pointer = Manager = Seat = registry = 0;

					if (Display != 0)
					{
						WaylandNative.DisplayDisconnect(Display);
						Display = 0;
					}

					if (selfHandle.IsAllocated) selfHandle.Free();
					return;
				}

				if (Pointer != 0) { WaylandNative.VirtualPointerDestroy(Pointer); Pointer = 0; }
				if (Manager != 0) { WaylandNative.VirtualPointerManagerDestroy(Manager); Manager = 0; }
				if (Seat != 0) { WaylandNative.ProxyDestroy(Seat); Seat = 0; }
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
