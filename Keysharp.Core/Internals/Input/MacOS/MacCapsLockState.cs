#if OSX
using System.Runtime.InteropServices;

namespace Keysharp.Internals.Input.MacOS
{
	/// <summary>
	/// Reads and writes the macOS CapsLock lock state through IOKit.
	/// CGEvent-posted CapsLock keystrokes do not change the toggle state (the lock
	/// state lives in the HID driver below the event tap), so both querying and
	/// toggling must go through IOHIDGet/SetModifierLockState instead.
	/// </summary>
	internal static class MacCapsLockState
	{
		private const string IOKitLib = "/System/Library/Frameworks/IOKit.framework/IOKit";
		private const string SystemLib = "/usr/lib/libSystem.dylib";

		private const uint kIOHIDParamConnectType = 1;
		private const int kIOHIDCapsLockState = 1;
		private const string kIOHIDSystemClass = "IOHIDSystem";

		[DllImport(IOKitLib)]
		private static extern uint IOServiceGetMatchingService(uint mainPort, nint matching);

		[DllImport(IOKitLib)]
		private static extern nint IOServiceMatching(string name);

		[DllImport(IOKitLib)]
		private static extern int IOServiceOpen(uint service, nint owningTask, uint type, out uint connect);

		[DllImport(IOKitLib)]
		private static extern int IOObjectRelease(uint obj);

		[DllImport(IOKitLib)]
		private static extern int IOServiceClose(uint connect);

		[DllImport(IOKitLib)]
		private static extern int IOHIDGetModifierLockState(uint handle, int selector, [MarshalAs(UnmanagedType.I1)] out bool state);

		[DllImport(IOKitLib)]
		private static extern int IOHIDSetModifierLockState(uint handle, int selector, [MarshalAs(UnmanagedType.I1)] bool state);

		[DllImport(SystemLib)]
		private static extern nint mach_task_self();

		// Own the connection and every operation performed through it under one lock. Besides making a
		// toggle atomic, this prevents a stale-handle retry on one thread from closing the handle while
		// another thread is inside IOHIDGet/SetModifierLockState.
		private static readonly Lock stateLock = new();
		private static uint connection;

		static MacCapsLockState()
			=> AppDomain.CurrentDomain.ProcessExit += (_, _) => CloseConnection();

		private static bool TryGetConnectionCore(out uint conn)
		{
			if (connection != 0)
			{
				conn = connection;
				return true;
			}

			conn = 0;
			uint service = 0;
			try
			{
				service = IOServiceGetMatchingService(0, IOServiceMatching(kIOHIDSystemClass));
				if (service == 0)
					return false;

				var kr = IOServiceOpen(service, mach_task_self(), kIOHIDParamConnectType, out var handle);
				if (kr != 0 || handle == 0)
					return false;

				connection = handle;
				conn = handle;
				return true;
			}
			catch
			{
				return false;
			}
			finally
			{
				if (service != 0)
					_ = IOObjectRelease(service);
			}
		}

		internal static bool IsAvailable
		{
			get
			{
				lock (stateLock)
					return TryGetConnectionCore(out _);
			}
		}

		internal static bool TryGet(out bool on)
		{
			lock (stateLock)
				return TryAccessCore(null, out on);
		}

		internal static bool TrySet(bool on)
		{
			lock (stateLock)
				return TryAccessCore(on, out _);
		}

		internal static bool TryToggle()
		{
			lock (stateLock)
				return TryAccessCore(null, out var on) && TryAccessCore(!on, out _);
		}

		private static bool TryAccessCore(bool? desired, out bool state)
		{
			state = desired.GetValueOrDefault();
			for (var attempt = 0; attempt < 2; attempt++)
			{
				if (!TryGetConnectionCore(out var conn))
					return false;

				try
				{
					var result = desired.HasValue
						? IOHIDSetModifierLockState(conn, kIOHIDCapsLockState, desired.Value)
						: IOHIDGetModifierLockState(conn, kIOHIDCapsLockState, out state);
					if (result == 0)
						return true;
				}
				catch { /* Retry once with a fresh connection. */ }

				InvalidateConnectionCore(conn);
			}

			return false;
		}

		private static void InvalidateConnectionCore(uint expected)
		{
			if (connection != expected)
				return;

			try { _ = IOServiceClose(connection); }
			catch { /* The handle is unusable either way; forget it below. */ }
			connection = 0;
		}

		private static void CloseConnection()
		{
			lock (stateLock)
				InvalidateConnectionCore(connection);
		}
	}
}
#endif
