namespace Keysharp.Core.Common.Platform
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
			return new Windows.Drive(drive);
#elif LINUX
			return new Linux.Drive(drive);
#elif OSX
			throw new PlatformNotSupportedException("Drive operations are not implemented on macOS yet.");
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
			return new Core.Windows.StatusBar(hwnd);
#elif LINUX
			return new Core.Unix.StatusBar(hwnd);
#elif OSX
			return new Core.Unix.StatusBar(hwnd);
#else
#error Unsupported platform. Only WINDOWS, LINUX, and OSX are supported.
#endif
		}
	}

	internal class WindowProvider
	{
		internal WindowManagerBase Manager { get; } = new WindowManager();
	}
}
