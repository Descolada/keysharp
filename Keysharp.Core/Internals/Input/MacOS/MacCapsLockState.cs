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
		private static extern int IOHIDGetModifierLockState(uint handle, int selector, [MarshalAs(UnmanagedType.I1)] out bool state);

		[DllImport(IOKitLib)]
		private static extern int IOHIDSetModifierLockState(uint handle, int selector, [MarshalAs(UnmanagedType.I1)] bool state);

		[DllImport(SystemLib)]
		private static extern nint mach_task_self();

		private static readonly Lock connLock = new();
		private static uint connection;
		private static bool connectionFailed;

		private static bool TryGetConnection(out uint conn)
		{
			lock (connLock)
			{
				if (connection != 0)
				{
					conn = connection;
					return true;
				}

				conn = 0;

				if (connectionFailed)
					return false;

				try
				{
					var service = IOServiceGetMatchingService(0, IOServiceMatching(kIOHIDSystemClass));

					if (service == 0)
					{
						connectionFailed = true;
						return false;
					}

					var kr = IOServiceOpen(service, mach_task_self(), kIOHIDParamConnectType, out var handle);
					_ = IOObjectRelease(service);

					if (kr != 0 || handle == 0)
					{
						connectionFailed = true;
						return false;
					}

					connection = handle;
					conn = handle;
					return true;
				}
				catch
				{
					connectionFailed = true;
					return false;
				}
			}
		}

		internal static bool IsAvailable => TryGetConnection(out _);

		internal static bool TryGet(out bool on)
		{
			on = false;
			return TryGetConnection(out var conn)
				&& IOHIDGetModifierLockState(conn, kIOHIDCapsLockState, out on) == 0;
		}

		internal static bool TrySet(bool on)
			=> TryGetConnection(out var conn)
			&& IOHIDSetModifierLockState(conn, kIOHIDCapsLockState, on) == 0;

		internal static bool TryToggle()
			=> TryGet(out var on) && TrySet(!on);
	}
}
#endif
