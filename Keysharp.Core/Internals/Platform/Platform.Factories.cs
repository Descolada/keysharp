namespace Keysharp.Internals
{
	internal static partial class Platform
	{
		/// <summary>Per-platform <see cref="Mapper.DriveBase"/> factory (compile-time OS selection).</summary>
		internal static class Drive
		{
			internal static Mapper.DriveBase CreateDrive(DriveInfo drive)
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

		/// <summary>Per-platform <see cref="Window.StatusBarBase"/> factory (compile-time OS selection).</summary>
		internal static class StatusBar
		{
			internal static Window.StatusBarBase CreateStatusBar(nint hwnd)
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
	}
}
