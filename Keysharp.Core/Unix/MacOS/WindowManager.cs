#if OSX
namespace Keysharp.Core.MacOS
{
	internal class WindowManager : WindowManagerBase, IWindowManager
	{
		public static WindowItemBase ActiveWindow
		{
			get
			{
				var hwnd = MacNativeWindows.GetFrontWindowHandle();
				if (hwnd != 0)
					return new WindowItem(hwnd);

				if (Application.Instance?.Windows != null)
				{
					var visible = Application.Instance.Windows.FirstOrDefault(w => w.Visible);
					if (visible != null)
						return new WindowItem(visible);
				}

				return new WindowItem(0);
			}
		}

		public static IEnumerable<WindowItemBase> AllWindows
		{
			get
			{
				var windows = MacNativeWindows.Snapshot();
				var list = new List<WindowItemBase>(windows.Count + 8);
				var seen = new HashSet<nint>();

				for (int i = 0; i < windows.Count; i++)
				{
					var h = (nint)windows[i].WindowNumber;
					list.Add(new WindowItem(h));
					seen.Add(h);
				}

				// Keep app-owned Eto windows visible to window search if not exposed via CG snapshot.
				if (Application.Instance?.Windows != null)
				{
					foreach (var w in Application.Instance.Windows)
					{
						if (w == null || w.Handle == 0 || !seen.Add(w.Handle))
							continue;

						list.Add(new WindowItem(w));
					}
				}

				return list;
			}
		}

		internal WindowManager()
		{
			Script.TheScript.ProcessesData.CurrentThreadID = (uint)Environment.CurrentManagedThreadId;
		}

		public static WindowItemBase CreateWindow(nint id) => new WindowItem(id);

		public static IEnumerable<WindowItemBase> FilterForGroups(IEnumerable<WindowItemBase> windows) => windows;

		public static uint GetFocusedCtrlThread(ref nint apControl, nint aWindow)
		{
			apControl = 0;

			var hwnd = aWindow != 0 ? aWindow : ActiveWindow?.Handle ?? 0;
			if (hwnd == 0)
				return 0;

			if (MacNativeWindows.TryGetWindowInfo(hwnd, out var native))
				return (uint)Math.Max(0, native.OwnerPid);

			return 0;
		}

		public static nint GetForegroundWindowHandle() => ActiveWindow?.Handle ?? 0;

		public static bool IsWindow(nint handle)
		{
			if (handle == 0)
				return false;

			if (Control.FromHandle(handle) != null)
				return true;

			return MacNativeWindows.TryGetWindowInfo(handle, out _);
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
		{
			if (MacNativeWindows.TryGetWindowAtPoint(location, out var native))
			{
				var window = new WindowItem((nint)native.WindowNumber);
				var pah = new PointAndHwnd(location);
				window.ChildFindPoint(pah);
				if (pah.hwndFound != 0)
					return WindowManager.CreateWindow(pah.hwndFound);

				return window;
			}

			return ActiveWindow;
		}

		public static WindowItemBase WindowFromPoint(POINT location)
		{
			if (MacNativeWindows.TryGetWindowAtPoint(location, out var native))
				return new WindowItem((nint)native.WindowNumber);

			return ChildWindowFromPoint(location);
		}
	}
}
#endif
