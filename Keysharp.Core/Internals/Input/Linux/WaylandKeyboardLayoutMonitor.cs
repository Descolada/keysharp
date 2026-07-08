#if LINUX
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Keysharp.Internals.Window.Linux.Wayland;

namespace Keysharp.Internals.Input.Linux
{
	/// <summary>
	/// Minimal Wayland keyboard listener used only to mirror the compositor-provided XKB keymap
	/// into the managed layout mapper. The input hook itself still comes from keysharp-inputd.
	/// </summary>
	internal sealed class WaylandKeyboardLayoutMonitor : IDisposable
	{
		private const uint WlSeatCapabilityKeyboard = 1u << 1;
		private const uint WlKeyboardKeymapFormatXkbV1 = 1;
		private const int MaxInitialRoundtrips = 8;

		private readonly object sync = new();
		private readonly Action keymapChanged;
		private readonly GCHandle selfHandle;

		private nint display;
		private nint registry;
		private nint seat;
		private nint keyboard;
		private uint seatVersion;
		private string keymapText;
		private uint group;
		private bool groupKnown;
		private bool started;
		private bool unavailable;
		private bool disposed;
		private CancellationTokenSource dispatcherCancel;
		private Thread dispatcherThread;

		internal WaylandKeyboardLayoutMonitor(Action keymapChanged)
		{
			this.keymapChanged = keymapChanged;
			selfHandle = GCHandle.Alloc(this);
		}

		internal bool TryGetKeymap(out string text)
		{
			text = null;

			if (!TryStart())
				return false;

			lock (sync)
			{
				text = keymapText;
				return !string.IsNullOrEmpty(text);
			}
		}

		internal bool TryGetGroup(out uint activeGroup)
		{
			activeGroup = 0;

			if (!TryStart())
				return false;

			lock (sync)
			{
				if (!groupKnown)
					return false;

				activeGroup = group;
				return true;
			}
		}

		private bool TryStart()
		{
			lock (sync)
			{
				if (started)
					return true;

				if (unavailable || disposed || !Platform.Desktop.IsWaylandSession)
					return false;

				display = WaylandNative.DisplayConnect(null);

				if (display == 0)
				{
					unavailable = true;
					return false;
				}

				try
				{
					registry = WaylandNative.DisplayGetRegistry(display);

					if (registry == 0)
						throw new IOException("wl_display.get_registry returned null.");

					if (WaylandNative.ProxyAddListener(registry, RegistryListener.Pointer, GCHandle.ToIntPtr(selfHandle)) != 0)
						throw new IOException("wl_proxy_add_listener for registry failed.");

					for (var i = 0; i < MaxInitialRoundtrips && string.IsNullOrEmpty(keymapText); i++)
					{
						if (WaylandNative.DisplayRoundtrip(display) < 0)
							throw new IOException("wl_display.roundtrip failed.");
					}

					started = true;
					StartDispatcher();
					return true;
				}
				catch
				{
					DisposeCore();
					unavailable = true;
					return false;
				}
			}
		}

		private void StartDispatcher()
		{
			dispatcherCancel = new CancellationTokenSource();
			var token = dispatcherCancel.Token;
			dispatcherThread = new Thread(() => DispatchLoop(token))
			{
				IsBackground = true,
				Name = "KeysharpWaylandKeyboardLayout"
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
						if (disposed || display == 0)
							return;

						_ = WaylandNative.DisplayDispatchPending(display);
						_ = WaylandNative.DisplayFlush(display);
					}
				}
				catch
				{
					// Keep this best-effort; the layout provider still has XKB_DEFAULT_* fallback.
				}

				try { Thread.Sleep(50); }
				catch (ThreadInterruptedException) { }
			}
		}

		private void OnGlobal(uint name, string interfaceName, uint version)
		{
			if (interfaceName != "wl_seat" || seat != 0)
				return;

			var bound = Math.Min(version, 8u);
			seat = WaylandNative.RegistryBind(registry, name, WaylandNative.SeatInterface, "wl_seat", bound);
			seatVersion = bound;

			if (seat != 0)
				_ = WaylandNative.ProxyAddListener(seat, SeatListener.Pointer, GCHandle.ToIntPtr(selfHandle));
		}

		private void OnSeatCapabilities(uint capabilities)
		{
			var hasKeyboard = (capabilities & WlSeatCapabilityKeyboard) != 0;

			if (hasKeyboard && keyboard == 0 && seat != 0)
			{
				keyboard = WaylandNative.SeatGetKeyboard(seat);

				if (keyboard != 0)
					_ = WaylandNative.ProxyAddListener(keyboard, KeyboardListener.Pointer, GCHandle.ToIntPtr(selfHandle));
			}
			else if (!hasKeyboard && keyboard != 0)
			{
				WaylandNative.KeyboardRelease(keyboard);
				keyboard = 0;
				keymapText = null;
				group = 0;
				groupKnown = false;
			}
		}

		private void OnKeymap(uint format, int fd, uint size)
		{
			if (format != WlKeyboardKeymapFormatXkbV1 || fd < 0 || size == 0)
			{
				if (fd >= 0)
					_ = WaylandNative.Close(fd);

				return;
			}

			string text;

			try
			{
				using var handle = new SafeFileHandle(new IntPtr(fd), ownsHandle: true);
				using var stream = new FileStream(handle, FileAccess.Read);
				var capped = checked((int)Math.Min(size, int.MaxValue));
				var bytes = new byte[capped];
				var total = 0;

				while (total < bytes.Length)
				{
					var read = stream.Read(bytes, total, bytes.Length - total);

					if (read <= 0)
						break;

					total += read;
				}

				text = Encoding.UTF8.GetString(bytes, 0, total).TrimEnd('\0');
			}
			catch
			{
				return;
			}

			if (string.IsNullOrEmpty(text))
				return;

			var changed = false;

			lock (sync)
			{
				if (!string.Equals(keymapText, text, StringComparison.Ordinal))
				{
					keymapText = text;
					changed = true;
				}
			}

			if (changed)
				NotifyKeymapChanged();
		}

		private void OnModifiers(uint activeGroup)
		{
			lock (sync)
			{
				group = activeGroup;
				groupKnown = true;
			}
		}

		private void NotifyKeymapChanged()
		{
			if (keymapChanged == null)
				return;

			ThreadPool.QueueUserWorkItem(_ =>
			{
				try { keymapChanged(); }
				catch { }
			});
		}

		private static WaylandKeyboardLayoutMonitor Self(nint data) => (WaylandKeyboardLayoutMonitor)GCHandle.FromIntPtr(data).Target;
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

		private static class SeatListener
		{
			private static readonly CapabilitiesHandler onCapabilities = Capabilities;
			private static readonly NameHandler onName = Name;
			internal static readonly nint Pointer = Build();

			private static nint Build()
			{
				var block = Marshal.AllocHGlobal(IntPtr.Size * 2);
				Marshal.WriteIntPtr(block, 0, Marshal.GetFunctionPointerForDelegate(onCapabilities));
				Marshal.WriteIntPtr(block, IntPtr.Size, Marshal.GetFunctionPointerForDelegate(onName));
				return block;
			}

			private static void Capabilities(nint data, nint seat, uint capabilities)
				=> Self(data).OnSeatCapabilities(capabilities);

			private static void Name(nint data, nint seat, nint name) { }

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			private delegate void CapabilitiesHandler(nint data, nint seat, uint capabilities);
			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			private delegate void NameHandler(nint data, nint seat, nint name);
		}

		private static class KeyboardListener
		{
			private static readonly KeymapHandler onKeymap = Keymap;
			private static readonly EnterHandler onEnter = Enter;
			private static readonly LeaveHandler onLeave = Leave;
			private static readonly KeyHandler onKey = Key;
			private static readonly ModifiersHandler onModifiers = Modifiers;
			private static readonly RepeatInfoHandler onRepeatInfo = RepeatInfo;
			internal static readonly nint Pointer = Build();

			private static nint Build()
			{
				var block = Marshal.AllocHGlobal(IntPtr.Size * 6);
				Marshal.WriteIntPtr(block, 0, Marshal.GetFunctionPointerForDelegate(onKeymap));
				Marshal.WriteIntPtr(block, IntPtr.Size, Marshal.GetFunctionPointerForDelegate(onEnter));
				Marshal.WriteIntPtr(block, IntPtr.Size * 2, Marshal.GetFunctionPointerForDelegate(onLeave));
				Marshal.WriteIntPtr(block, IntPtr.Size * 3, Marshal.GetFunctionPointerForDelegate(onKey));
				Marshal.WriteIntPtr(block, IntPtr.Size * 4, Marshal.GetFunctionPointerForDelegate(onModifiers));
				Marshal.WriteIntPtr(block, IntPtr.Size * 5, Marshal.GetFunctionPointerForDelegate(onRepeatInfo));
				return block;
			}

			private static void Keymap(nint data, nint keyboard, uint format, int fd, uint size)
				=> Self(data).OnKeymap(format, fd, size);

			private static void Enter(nint data, nint keyboard, uint serial, nint surface, nint keys) { }
			private static void Leave(nint data, nint keyboard, uint serial, nint surface) { }
			private static void Key(nint data, nint keyboard, uint serial, uint time, uint key, uint state) { }

			private static void Modifiers(nint data, nint keyboard, uint serial, uint modsDepressed, uint modsLatched, uint modsLocked, uint group)
				=> Self(data).OnModifiers(group);

			private static void RepeatInfo(nint data, nint keyboard, int rate, int delay) { }

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			private delegate void KeymapHandler(nint data, nint keyboard, uint format, int fd, uint size);
			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			private delegate void EnterHandler(nint data, nint keyboard, uint serial, nint surface, nint keys);
			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			private delegate void LeaveHandler(nint data, nint keyboard, uint serial, nint surface);
			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			private delegate void KeyHandler(nint data, nint keyboard, uint serial, uint time, uint key, uint state);
			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			private delegate void ModifiersHandler(nint data, nint keyboard, uint serial, uint modsDepressed, uint modsLatched, uint modsLocked, uint group);
			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			private delegate void RepeatInfoHandler(nint data, nint keyboard, int rate, int delay);
		}

		public void Dispose()
		{
			lock (sync)
				disposed = true;

			try { dispatcherCancel?.Cancel(); } catch { }
			try { dispatcherThread?.Join(100); } catch { }
			dispatcherCancel?.Dispose();
			dispatcherCancel = null;
			dispatcherThread = null;

			lock (sync)
				DisposeCore();

			if (selfHandle.IsAllocated)
				selfHandle.Free();
		}

		private void DisposeCore()
		{
			if (keyboard != 0)
			{
				WaylandNative.KeyboardRelease(keyboard);
				keyboard = 0;
			}

			if (seat != 0) { WaylandNative.ProxyDestroy(seat); seat = 0; }
			if (registry != 0) { WaylandNative.ProxyDestroy(registry); registry = 0; }

			if (display != 0)
			{
				WaylandNative.DisplayDisconnect(display);
				display = 0;
			}

			started = false;
			keymapText = null;
			group = 0;
			groupKnown = false;
			seatVersion = 0;
		}
	}
}
#endif
