#if OSX
namespace Keysharp.Core.MacOS
{
	internal class WindowManager : WindowManagerBase, IWindowManager
	{
		public static WindowItemBase ActiveWindow
		{
			get
			{
				if (Application.Instance?.Windows == null)
					return new WindowItem(0);

				var active = Application.Instance.Windows.FirstOrDefault(w => w.Visible)
					?? Application.Instance.Windows.FirstOrDefault();

				return active != null ? new WindowItem(active) : new WindowItem(0);
			}
		}

		public static IEnumerable<WindowItemBase> AllWindows
		{
			get
			{
				if (Application.Instance?.Windows == null)
					return [];

				return Application.Instance.Windows.Select(window => (WindowItemBase)new WindowItem(window)).ToList();
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
			return 0;
		}

		public static nint GetForegroundWindowHandle() => ActiveWindow?.Handle ?? 0;

		public static bool IsWindow(nint handle) => handle != 0 && Control.FromHandle(handle) != null;

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
			foreach (var window in AllWindows)
			{
				var rect = window.Location;
				if (location.X >= rect.X && location.X <= rect.Right && location.Y >= rect.Y && location.Y <= rect.Bottom)
					return window;
			}

			return ActiveWindow;
		}

		public static WindowItemBase WindowFromPoint(POINT location)
		{
			return ChildWindowFromPoint(location);
		}
	}
}
#endif
