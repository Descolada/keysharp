namespace Keysharp.Internals
{

#if WINDOWS
	internal sealed class WindowsWindow : WindowBase
	{
		// Per-call Win32 read/write helper, built directly (NOT WindowQuery.CreateWindow, which returns the neutral
		// WindowInfo that forwards back here) → no recursion. Windows reads are cheap-ish per-property and memoized
		// by the neutral WindowInfo, so it passes no snapshot source; the live by-handle reads land here.
		// TODO (Windows-machine task): inline these read bodies directly off WindowsAPI and slim Win32Window.
		private static Keysharp.Internals.Window.Windows.Win32Window Item(nint h) => new (h);

		public override WindowInfoBase CreateWindow(nint id) => new WindowInfo(id);

		public override WindowInfoBase ActiveWindow()
		{
			var fh = GetForegroundHandle();
			return fh == 0 ? new WindowInfo(0) : new WindowInfo(fh);
		}

		// --- granular live reads ---
		public override string GetTitle(nint h) => Item(h).Title;
		public override string GetClassName(nint h) => Item(h).ClassName;
		public override long GetPid(nint h) => Item(h).PID;
		public override Rectangle GetBounds(nint h) => Item(h).Bounds;
		public override Rectangle GetClientBounds(nint h) => Item(h).ClientBounds;
		public override long GetStyle(nint h) => Item(h).Style;
		public override long GetExStyle(nint h) => Item(h).ExStyle;
		public override bool GetActive(nint h) => Item(h).Active;
		public override bool GetVisible(nint h) => Item(h).Visible;
		public override bool GetEnabled(nint h) => Item(h).Enabled;
		public override bool GetHung(nint h) => Item(h).IsHung;
		public override bool GetExists(nint h) => Item(h).Exists;
		public override FormWindowState GetWindowState(nint h) => Item(h).WindowState;
		public override bool GetAlwaysOnTop(nint h) => Item(h).AlwaysOnTop;
		public override object GetTransparency(nint h) => Item(h).Transparency;
		public override object GetTransparentColor(nint h) => Item(h).TransparentColor;
		public override POINT ClientToScreen(nint h) => Item(h).ClientToScreen();

		public override bool TryGetText(nint h, bool detectHidden, out List<string> text)
		{
			text = Item(h).GetText(new WindowSearchOptions { DetectHiddenText = detectHidden });
			return true;
		}

		public override void ChildFindPoint(nint h, PointAndHwnd pah) => Item(h).ChildFindPoint(pah);

		public override bool TryClientToScreen(nint h, ref Point pt)
		{
			var o = Item(h).ClientToScreen();
			pt = new Point(pt.X + o.X, pt.Y + o.Y);
			return true;
		}

		public override bool TryGetParent(nint h, out nint parent)
		{
			parent = Item(h).ParentHandle;
			return parent != 0;
		}

		public override bool TryGetTopLevel(nint h, out nint top)
		{
			top = Item(h).TopLevelHandle;
			return top != 0;
		}

		public override bool TryEnumerateChildren(nint h, out IReadOnlyList<nint> children)
		{
			children = Item(h).ChildHandles.ToList();
			return true;
		}

		// --- control ---
		public override bool TrySetAlwaysOnTop(nint h, bool onTop) { Item(h).AlwaysOnTop = onTop; return true; }
		public override bool TryMoveResize(nint h, Rectangle bounds, bool setPos, bool setSize) { Item(h).Bounds = bounds; return true; }
		public override bool TrySetState(nint h, FormWindowState state) { Item(h).WindowState = state; return true; }
		public override bool TrySetStyle(nint h, long style) { Item(h).Style = style; return true; }
		public override bool TrySetExStyle(nint h, long exStyle) { Item(h).ExStyle = exStyle; return true; }
		public override bool TrySetTransparency(nint h, object alpha) { Item(h).Transparency = alpha; return true; }
		public override bool TryActivate(nint h) { Item(h).Active = true; return true; }
		public override bool TrySetZOrder(nint h, ZOrder z) { Item(h).Bottom = z == ZOrder.Bottom; return true; }
		public override bool TryClose(nint h) => Item(h).Close();
		public override bool TryKill(nint h) => Item(h).Kill();
		public override bool TryHide(nint h) => Item(h).Hide();
		public override bool TryShow(nint h) => Item(h).Show();
		public override bool TryRedraw(nint h) => Item(h).Redraw();
		public override bool TryClick(nint h, Point at, uint button, int count) { var it = Item(h); for (var i = 0; i < count; i++) { if (button == 2) it.ClickRight(at); else it.Click(at); } return true; }
		public override bool TrySetTitle(nint h, string title) { Item(h).Title = title; return true; }
		public override bool TrySetVisible(nint h, bool visible) { Item(h).Visible = visible; return true; }
		public override bool TrySetEnabled(nint h, bool enabled) { Item(h).Enabled = enabled; return true; }
		public override bool TrySetTransparentColor(nint h, object color) { Item(h).TransparentColor = color; return true; }

		// Native by-class (+ exact title) lookup: one Win32 FindWindow instead of an EnumWindows scan. Returning
		// true with handle 0 tells WindowQuery there is no such window at all, so it skips the full enumeration.
		public override bool TryFindWindow(string className, string title, out nint handle)
		{
			var hwnd = Os.Windows.WindowsAPI.FindWindow(string.IsNullOrEmpty(className) ? null : className,
													   string.IsNullOrEmpty(title) ? null : title);
			handle = hwnd;
			return true;
		}

		public override nint GetForegroundHandle()
			=> Os.Windows.WindowsAPI.GetForegroundWindow();

		public override bool IsWindow(nint h)
			=> Os.Windows.WindowsAPI.IsWindow(h) || h == Os.Windows.WindowsAPI.HWND_BROADCAST;

		public override uint GetFocusedControlThread(nint window, out nint control)
		{
			var aWindow = window;
			nint ctrl = 0;
			var threadId = 0u;

			// No foreground window → the script's own layout is the safest default.
			if (aWindow == 0)
				aWindow = Os.Windows.WindowsAPI.GetForegroundWindow();

			if (aWindow != 0)
			{
				threadId = Os.Windows.WindowsAPI.GetWindowThreadProcessId(aWindow, out var _);
				var info = Os.Windows.GUITHREADINFO.Default;

				// Necessary for UWP apps (e.g. Edge) where the top-level window's thread differs from the
				// focused control's thread.
				if (Os.Windows.WindowsAPI.GetGUIThreadInfo(threadId, out info) && info.hwndFocus != 0)
				{
					threadId = Os.Windows.WindowsAPI.GetWindowThreadProcessId(info.hwndFocus, out var _);
					ctrl = info.hwndFocus;
				}
			}

			control = ctrl;
			return threadId;
		}

		public override IReadOnlyList<WindowInfoBase> Enumerate(bool includeHidden)
		{
			var list = new List<WindowInfoBase>();
			_ = Os.Windows.WindowsAPI.EnumWindows(delegate (nint hwnd, int lParam)
			{
				if (includeHidden || (Os.Windows.WindowsAPI.IsWindowVisible(hwnd) && !Os.Windows.WindowsAPI.IsWindowCloaked(hwnd)))
					list.Add(new WindowInfo(hwnd));   // empty/lazy — Windows reads live

				return true;
			}, 0);
			return list;
		}

		public override bool TryGetAt(int x, int y, out nint child)
		{
			var ctrl = Os.Windows.WindowsAPI.WindowFromPoint(new POINT(x, y));
			child = ctrl;
			return ctrl != 0;
		}

		public override bool IncludeInGroups(nint h)
		{
			var it = Item(h);
			var exstyle = it.ExStyle;
			var hwnd = h;
			return it.Enabled
				   && (exstyle & Os.Windows.WindowsAPI.WS_EX_TOPMOST) == 0
				   && (exstyle & Os.Windows.WindowsAPI.WS_EX_NOACTIVATE) == 0
				   && (exstyle & (Os.Windows.WindowsAPI.WS_EX_TOOLWINDOW | Os.Windows.WindowsAPI.WS_EX_APPWINDOW)) != Os.Windows.WindowsAPI.WS_EX_TOOLWINDOW
				   && Os.Windows.WindowsAPI.IsWindowVisible(hwnd) && !Os.Windows.WindowsAPI.IsWindowCloaked(hwnd)
				   && Os.Windows.WindowsAPI.GetLastActivePopup(hwnd) != hwnd
				   && Os.Windows.WindowsAPI.GetWindow(hwnd, Os.Windows.WindowsAPI.GW_OWNER) == 0
				   && Os.Windows.WindowsAPI.GetShellWindow() != hwnd;
		}
	}
#endif
}
