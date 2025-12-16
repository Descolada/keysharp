#if LINUX
namespace Keysharp.Core.Linux
{
	/// <summary>
	/// Concrete implementation of WindowManager for the linux platfrom.
	/// </summary>
	internal class WindowManager : WindowManagerBase, IWindowManager
	{
		internal static Lock xLibLock = new (); //The X11 Winforms implementation uses this, so attempt to do the same here.

		// ToDo: There may be more than only one xDisplay
		private static XDisplay Display => XDisplay.Default;

		public static WindowItemBase ActiveWindow => new WindowItem(Display.XGetInputFocusWindow());

		/// <summary>
		/// Return all top level windows.
		/// This behaves differently on linux in that it *does* recurse into child windows.
		/// This is needed because otherwise none of the windows will be properly found.
		/// </summary>
		public static IEnumerable<WindowItemBase> AllWindows
		{
			get
			{
				var attr = new XWindowAttributes();
				var doHidden = ThreadAccessors.A_DetectHiddenWindows;
				var filter = (long id) =>
				{
					if (Xlib.XGetWindowAttributes(Display.Handle, id, ref attr) != 0)
						if (doHidden || attr.map_state == MapState.IsViewable)
							return true;

					return false;
				};
				//return _display.XQueryTree(filter).Select(w => new WindowItem(w));
				return Display.XQueryTreeRecursive(filter).Select(w => new WindowItem(w));
			}
		}

		internal WindowManager()
		{
			Script.TheScript.ProcessesData.CurrentThreadID = (uint)Xlib.gettid();
		}

		public static WindowItemBase CreateWindow(nint id) => new WindowItem(id);

		public static IEnumerable<WindowItemBase> FilterForGroups(IEnumerable<WindowItemBase> windows) => windows;

		public static uint GetFocusedCtrlThread(ref nint apControl, nint aWindow) => 0;

		public static nint GetForegroundWindowHandle() => new nint(Display.XGetInputFocusHandle());

		public static bool IsWindow(nint handle)
		{
			var attr = new XWindowAttributes();
			return Xlib.XGetWindowAttributes(Display.Handle, handle.ToInt64(), ref attr) != 0;
		}

		public static void MaximizeAll()
		{
			foreach (var window in AllWindows)
			{
				//KeysharpEnhancements.OutputDebugLine($"MaximizeAll(): Examiniming window: {window.Title}");
				window.WindowState = FormWindowState.Maximized;
			}
		}

		public static void MinimizeAll()
		{
			foreach (var window in AllWindows)
			{
				//KeysharpEnhancements.OutputDebugLine($"MinimizeAll(): Examiniming window: {window.Title}");
				window.WindowState = FormWindowState.Minimized;
			}
		}

		public static void MinimizeAllUndo()
		{
			foreach (var window in AllWindows)
			{
				//KeysharpEnhancements.OutputDebugLine($"MinimizeAllUndo(): Examiniming window: {window.Title}");
				window.WindowState = FormWindowState.Normal;
			}
		}

		internal void SendNetClientMessage(nint window, nint message_type, nint l0, nint l1, nint l2)
		{
			var xev = new XEvent();
			xev.ClientMessageEvent.type = XEventName.ClientMessage;
			xev.ClientMessageEvent.send_event = true;
			xev.ClientMessageEvent.window = window;
			xev.ClientMessageEvent.message_type = message_type;
			xev.ClientMessageEvent.format = 32;
			xev.ClientMessageEvent.ptr1 = l0;
			xev.ClientMessageEvent.ptr2 = l1;
			xev.ClientMessageEvent.ptr3 = l2;
			_ = Xlib.XSendEvent(Display.Handle, window, false, EventMasks.NoEvent, ref xev);
		}

		internal void SendNetWMMessage(nint window, nint message_type, nint l0, nint l1, nint l2, nint l3)
		{
			var xev = new XEvent();
			xev.ClientMessageEvent.type = XEventName.ClientMessage;
			xev.ClientMessageEvent.send_event = true;
			xev.ClientMessageEvent.window = window;
			xev.ClientMessageEvent.message_type = message_type;
			xev.ClientMessageEvent.format = 32;
			xev.ClientMessageEvent.ptr1 = l0;
			xev.ClientMessageEvent.ptr2 = l1;
			xev.ClientMessageEvent.ptr3 = l2;
			xev.ClientMessageEvent.ptr4 = l3;
			_ = Xlib.XSendEvent(Display.Handle, Display.Root.ID, false, EventMasks.SubstructureRedirect | EventMasks.SubstructureNofity, ref xev);
		}

		public static WindowItemBase WindowFromPoint(POINT location)
		{
			var x = location.X;
			var y = location.Y;

			//Manually searched windows, but that likely is not what we need, given the context it's used in. Mostl likely will need to revisit using AtSpi.//TODO
			foreach (var window in AllWindows)
			{
				var wloc = window.Location;

				if (x >= wloc.X && x < wloc.X + wloc.Width &&
						y >= wloc.Y && y < wloc.Y + wloc.Height)
					return window;
			}

			return null;
		}
	}
}
#endif