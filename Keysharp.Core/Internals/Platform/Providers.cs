using Keysharp.Builtins;
namespace Keysharp.Internals.Platform
{
	internal class ControlProvider
	{
		internal ControlManagerBase Manager { get; } = new ControlManager();
	}

	internal static class DriveProvider
	{
		/// <summary>
		/// Creates a Drive for the current platform
		/// </summary>
		/// <param name="drive"></param>
		/// <returns></returns>
		internal static DriveBase CreateDrive(DriveInfo drive)
		{
#if WINDOWS
			return new Keysharp.Internals.Mapper.Windows.Drive(drive);
#elif LINUX
			return new Keysharp.Internals.Mapper.Linux.Drive(drive);
#elif OSX
			return new Keysharp.Internals.Mapper.MacOS.Drive(drive);
#else
#error Unsupported platform. Only WINDOWS, LINUX, and OSX are supported.
#endif
		}
	}

	internal class PlatformProvider
	{
		internal PlatformManagerBase Manager { get; } =
#if WINDOWS
			new PlatformManager()
#elif LINUX
			new Unix.PlatformManager()
#elif OSX
			new Unix.PlatformManager()
#else
#error Unsupported platform. Only WINDOWS, LINUX, and OSX are supported.
#endif
		;
	}

	internal class PermissionProvider
	{
		internal IPermissionManager Manager { get; } =
#if OSX
			new MacPermissionManager();
#elif LINUX
			new LinuxPermissionManager();
#else
			new DefaultPermissionManager();
#endif
	}

	internal static class StatusBarProvider
	{
		/// <summary>
		/// Creates a StatusBar for the current platform
		/// </summary>
		/// <param name="drive"></param>
		/// <returns></returns>
		internal static StatusBarBase CreateStatusBar(nint hwnd)
		{
#if WINDOWS
			return new Keysharp.Internals.Window.Windows.StatusBar(hwnd);
#elif LINUX
			return new Keysharp.Internals.Window.Unix.StatusBar(hwnd);
#elif OSX
			return new Keysharp.Internals.Window.Unix.StatusBar(hwnd);
#else
#error Unsupported platform. Only WINDOWS, LINUX, and OSX are supported.
#endif
		}
	}

	internal class WindowProvider
	{
		internal WindowManagerBase Manager { get; } = new WindowManager();
	}

	internal static class WindowEventBackendProvider
	{
		/// <summary>
		/// Creates the native window-event backend for the current platform, or null if window events are not
		/// supported in the current environment (in which case the hub simply never delivers anything).
		/// </summary>
		internal static IWindowEventBackend Create()
		{
#if WINDOWS
			return new Keysharp.Internals.Window.Windows.WindowEventBackend();
#elif LINUX
			return new Keysharp.Internals.Window.Linux.WindowEventBackend();
#elif OSX
			return new Keysharp.Internals.Window.MacOS.WindowEventBackend();
#else
			return null;
#endif
		}
	}
}
