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
			// On a Wayland session, prefer the compositor-native backend (it sees both native Wayland and XWayland
			// windows). It is used only when the active compositor can actually push window events; otherwise fall
			// through to the X11 backend, which still works for XWayland windows under the forced GDK_BACKEND=x11.
			if (Keysharp.Internals.Platform.Unix.PlatformManager.IsWaylandSession)
			{
				var wayland = Keysharp.Internals.Window.Linux.Wayland.WaylandBackend.Current;

				if (wayland != null && wayland.SupportsWindowEvents)
					return new Keysharp.Internals.Window.Linux.Wayland.WaylandWindowEventBackend(wayland);
			}

			return new Keysharp.Internals.Window.Linux.WindowEventBackend();
#elif OSX
			return new Keysharp.Internals.Window.MacOS.WindowEventBackend();
#else
			return null;
#endif
		}
	}
}
