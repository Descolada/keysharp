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

		public static WindowItemBase ActiveWindow 
		{
			get {
				var activeId = 0L;
				nint prop = 0;

				if (Xlib.XGetWindowProperty(Display.Handle,
						Display.Root.ID,
						Display._NET_ACTIVE_WINDOW,
						0,
						new nint(1),
						false,
						(nint)XAtom.AnyPropertyType,
						out _,
						out _,
						out var nitems,
						out _,
						ref prop) == 0)
				{
					if (nitems.ToInt64() > 0 && prop != 0)
						activeId = Marshal.ReadInt64(prop);
				}

				if (prop != 0)
					_ = Xlib.XFree(prop);

				if (activeId != 0)
					return new WindowItem(new nint(activeId));

				var focused = Display.XGetInputFocusWindow();
				if (focused.ID == 0 || focused.ID == 1)
					return new WindowItem(0);

				var item = new WindowItem(focused);
				return item.NonChildParentWindow ?? item;
			}
		}

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
				var topLevels = new List<WindowItemBase>();
				var seen = new HashSet<long>();
				foreach (var window in Display.XQueryTreeRecursive(filter).Select(w => new WindowItem(w)))
				{
					var topLevel = window.NonChildParentWindow ?? window;
					if (topLevel == null || topLevel.Handle == 0)
						continue;

					var key = topLevel.Handle.ToInt64();
					if (seen.Add(key))
						topLevels.Add(topLevel);
				}

				return topLevels;
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
			if (!PlatformManager.IsX11Available || handle == 0)
				return false;

			var attr = new XWindowAttributes();
			bool success = true;

			lock (xLibLock)
			{
				var oldHandler = Xlib.XSetErrorHandler((nint _, ref XErrorEvent __) =>
				{
					success = false;
					return 0;
				});

				try
				{
					var result = Xlib.XGetWindowAttributes(Display.Handle, handle.ToInt64(), ref attr) != 0;
					_ = Xlib.XSync(Display.Handle, false);
					return success && result;
				}
				finally
				{
					_ = Xlib.XSetErrorHandler(oldHandler);
				}
			}
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
			if (!PlatformManager.IsX11Available)
				return null;

			var root = (nint)Display.Root.ID;
			if (!Xlib.XQueryPointer(Display.Handle, root,
					out _,
					out var child,
					out _,
					out _,
					out _,
					out _,
					out _))
				return null;

			if (child == 0)
				return null;

			// Walk down to the deepest child under the pointer.
			nint current = child;
			while (true)
			{
				if (!Xlib.XQueryPointer(Display.Handle, current,
						out _,
						out var childReturn,
						out _,
						out _,
						out _,
						out _,
						out _))
					break;

				if (childReturn == 0)
					break;

				current = childReturn;
			}

			if (current == 0)
				return null;

			var attr = new XWindowAttributes();
			if (Xlib.XGetWindowAttributes(Display.Handle, current, ref attr) == 0 || attr.map_state != MapState.IsViewable)
				return null;

			return new WindowItem(new nint(current));
		}
	}
}
#endif
