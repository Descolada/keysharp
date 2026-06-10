using Keysharp.Builtins;
#if OSX
namespace Keysharp.Internals.Window.MacOS
{
	internal class WindowManager : WindowManagerBase, IWindowManager
	{
			public static WindowItemBase ActiveWindow
			{
				get
				{
					if (MacAccessibility.TryGetFocusedWindowHandle(out var focusedHwnd) && focusedHwnd != 0)
						return new WindowItem(focusedHwnd);

					return new WindowItem(0);
				}
			}

			public static IEnumerable<WindowItemBase> AllWindows => EnumerateWindows(ThreadAccessors.A_DetectHiddenWindows);

			public static IEnumerable<WindowItemBase> EnumerateWindows(bool detectHiddenWindows)
			{
				// Snapshot() requests window titles (kCGWindowName), which macOS omits for windows
				// owned by other processes unless Screen Recording access has been granted. Without
				// it, every other app's window has an empty title and title-based WinTitle matching
				// (e.g. WinHide("Calculator")) silently finds nothing.
				_ = MacAccessibility.EnsureScreenCaptureAccess("enumerate window titles", prompt: true);

				var windows = MacNativeWindows.Snapshot();
				var list = new List<WindowItemBase>(windows.Count);

				for (int i = 0; i < windows.Count; i++)
				{
					var window = new WindowItem(windows[i], includesTextMetadata: true);

					if (detectHiddenWindows || window.Visible)
						list.Add(window);
				}

				return list;
			}

		internal WindowManager()
		{
		}

		public static WindowItemBase CreateWindow(nint id) => new WindowItem(id);

		public static IEnumerable<WindowItemBase> FilterForGroups(IEnumerable<WindowItemBase> windows) => windows;

		public static uint GetFocusedCtrlThread(ref nint apControl, nint aWindow)
		{
			apControl = 0;

			var hwnd = aWindow != 0 ? aWindow : ActiveWindow?.Handle ?? 0;
			if (hwnd == 0)
				return 0;

			if (MacNativeWindows.TryGetWindowInfo(hwnd, out var native, includeTextMetadata: false))
				return (uint)Math.Max(0, native.OwnerPid);

			return 0;
		}

		public static nint GetForegroundWindowHandle() => ActiveWindow?.Handle ?? 0;

		public static bool IsWindow(nint handle)
		{
			if (handle == 0)
				return false;

			return MacNativeWindows.TryGetWindowInfo(handle, out _, includeTextMetadata: false);
		}

		public static void MaximizeAll()
		{
			foreach (var window in AllWindows)
				window.WindowState = FormWindowState.Maximized;
		}

		public static void MinimizeAll()
		{
			foreach (var window in AllWindows)
				window.WindowState = FormWindowState.Minimized;
		}

		public static void MinimizeAllUndo()
		{
			foreach (var window in AllWindows)
				window.WindowState = FormWindowState.Normal;
		}

		public static WindowItemBase ChildWindowFromPoint(POINT location)
			=> FindWindowFromPoint(location);

		public static WindowItemBase WindowFromPoint(POINT location)
			=> FindWindowFromPoint(location);

		private static WindowItemBase FindWindowFromPoint(POINT location)
		{
			if (MacNativeWindows.TryGetWindowAtPoint(location, out var native))
				return new WindowItem(native, includesTextMetadata: false);

			return null;
		}
	}
}
#endif
