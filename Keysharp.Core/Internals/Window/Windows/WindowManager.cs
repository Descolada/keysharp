using Keysharp.Builtins;
#if WINDOWS
namespace Keysharp.Internals.Window.Windows
{
	/// <summary>
	/// Concrete implementation of WindowManager for the Windows platfrom.
	/// </summary>
	internal class WindowManager : WindowManagerBase, IWindowManager
	{
		public static WindowItemBase ActiveWindow => CreateWindow(WindowsAPI.GetForegroundWindow());
		private static int lastWindowCount = 64;

		/// <summary>
		/// Return all top level windows. This does not recurse into child windows.
		/// </summary>
		public static IEnumerable<WindowItemBase> AllWindows
			=> EnumerateWindows(ThreadAccessors.A_DetectHiddenWindows);

		internal static bool IsDetectableTopLevelWindow(nint hwnd)
			=> WindowsAPI.IsWindowVisible(hwnd) && !WindowsAPI.IsWindowCloaked(hwnd);

		public static IEnumerable<WindowItemBase> EnumerateWindows(bool detectHiddenWindows)
		{
			var windows = new List<WindowItemBase>(lastWindowCount);
			_ = WindowsAPI.EnumWindows(delegate (nint hwnd, int lParam)
			{
				if (detectHiddenWindows || IsDetectableTopLevelWindow(hwnd))
					windows.Add(CreateWindow(hwnd));

				return true;
			}, 0);
			lastWindowCount = windows.Count;
			return windows;
		}

		internal WindowManager() {
		}

		public static WindowItemBase CreateWindow(nint id) => new WindowItem(id);

		public static IEnumerable<WindowItemBase> FilterForGroups(IEnumerable<WindowItemBase> windows)
		{
			return windows.Where((w) =>
			{
				var exstyle = w.ExStyle;
				return w.Enabled &&
					   (exstyle & WindowsAPI.WS_EX_TOPMOST) == 0 &&
					   (exstyle & WindowsAPI.WS_EX_NOACTIVATE) == 0 &&
					   (exstyle & (WindowsAPI.WS_EX_TOOLWINDOW | WindowsAPI.WS_EX_APPWINDOW)) != WindowsAPI.WS_EX_TOOLWINDOW &&
					   IsDetectableTopLevelWindow(w.Handle) &&
					   WindowsAPI.GetLastActivePopup(w.Handle) != w.Handle &&
					   WindowsAPI.GetWindow(w.Handle, WindowsAPI.GW_OWNER) == 0 &&
					   WindowsAPI.GetShellWindow() != w.Handle;
			});
		}

		public static new WindowItemBase FindWindow(SearchCriteria criteria, bool last = false)
		{
			WindowItemBase found = null;

			if (criteria.IsEmpty)
				return found;

			var matchOptions = WindowSearchOptions.Merge(criteria.Options);
			var detectHiddenWindows = ShouldDetectHiddenWindows(criteria);

			if (criteria.Active)
			{
				var activeWindow = ActiveWindow;
				return activeWindow is WindowItemBase active && active.IsSpecified && active.Equals(criteria, matchOptions) ? active : null;
			}

			if (criteria.ID != 0)
			{
				if (IsWindow(criteria.ID) && CreateWindow(criteria.ID) is WindowItemBase temp && temp.Equals(criteria, matchOptions))
					return temp;

				return null;
			}

			var mm = matchOptions.TitleMatchMode ?? ThreadAccessors.A_TitleMatchMode;

			if (mm < 4) //If the matching mode is not RegEx then try to take an optimized path
			{
				var hasTitle = !criteria.Title.IsNullOrEmpty();

				if (!criteria.ClassName.IsNullOrEmpty() || (mm == 3 && hasTitle))
				{
					var hwnd = WindowsAPI.FindWindow(criteria.ClassName == "" ? null : criteria.ClassName, !hasTitle || mm != 3 ? null : criteria.Title);

					if (hwnd == 0) //If there is no match with FindWindow then there can't be a match among AllWindows
						return found;

					found = WindowManager.CreateWindow(hwnd);

					if (((matchOptions.DetectHiddenWindows ?? ThreadAccessors.A_DetectHiddenWindows) || found.Visible) && found.Equals(criteria, matchOptions)) //Evaluate any other criteria as well before accepting the match
						return found;

					found = null;
				}
			}

			foreach (var window in EnumerateWindows(detectHiddenWindows))
			{
				if (window.Equals(criteria, matchOptions))
				{
					found = window;

					if (!last)
						break;
				}
			}

			return found;
		}

		public static uint GetFocusedCtrlThread(ref nint apControl, nint aWindow)
		{
			// Determine the thread for which we want the keyboard layout.
			// When no foreground window, the script's own layout seems like the safest default.
			var thread_id = 0u;

			if (aWindow == 0)
				aWindow = WindowsAPI.GetForegroundWindow();

			if (aWindow != 0)
			{
				// Get thread of aWindow (which should be the foreground window).
				thread_id = WindowsAPI.GetWindowThreadProcessId(aWindow, out var _);
				// Get focus.  Benchmarks showed this additional step added only 6% to the time,
				// and the total was only around 4µs per iteration anyway (on a Core i5-4460).
				// It is necessary for UWP apps such as Microsoft Edge, and any others where
				// the top-level window belongs to a different thread than the focused control.
				var thread_info = GUITHREADINFO.Default;

				if (WindowsAPI.GetGUIThreadInfo(thread_id, out thread_info) && thread_info.hwndFocus != 0)
				{
					// Use the focused control's thread.
					thread_id = WindowsAPI.GetWindowThreadProcessId(thread_info.hwndFocus, out var _);

					if (apControl != 0)
						apControl = thread_info.hwndFocus;
				}
			}

			return thread_id;
		}

		public static nint GetForegroundWindowHandle() => WindowsAPI.GetForegroundWindow();

		public static bool IsWindow(nint handle) => WindowsAPI.IsWindow(handle) || handle == WindowsAPI.HWND_BROADCAST;

		public static void MaximizeAll()
		{
			foreach (var window in AllWindows)
				window.WindowState = FormWindowState.Maximized;
			WindowItemBase.DoWinDelay();
		}

		public static void MinimizeAll()
		{
			var window = FindWindow(new SearchCriteria { ClassName = "Shell_TrayWnd" });
			_ = WindowsAPI.PostMessage(window.Handle, WindowsAPI.WM_COMMAND, new nint(419), 0);
			WindowItemBase.DoWinDelay();
		}

		public static void MinimizeAllUndo()
		{
			var window = FindWindow(new SearchCriteria { ClassName = "Shell_TrayWnd" });
			_ = WindowsAPI.PostMessage(window.Handle, WindowsAPI.WM_COMMAND, new nint(416), 0);
			WindowItemBase.DoWinDelay();
		}

		public static WindowItemBase ChildWindowFromPoint(POINT location)
		{
			var ctrl = WindowsAPI.WindowFromPoint(location);

			if (ctrl != 0)
				return CreateWindow(ctrl);

			return null;
		}

		public static WindowItemBase WindowFromPoint(POINT location)
		{
			var child = WindowsAPI.WindowFromPoint(location);

			if (child == 0)
				return null;

			var top = WindowsAPI.GetAncestor(child, gaFlags.GA_ROOT);
			if (top == 0)
				top = child;

			return CreateWindow(top);
		}
	}
}

#endif
