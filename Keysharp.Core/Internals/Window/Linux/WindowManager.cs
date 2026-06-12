using Keysharp.Builtins;
#if LINUX
namespace Keysharp.Internals.Window.Linux
{
	/// <summary>
	/// Concrete implementation of WindowManager for the linux platfrom.
	/// </summary>
	internal class WindowManager : WindowManagerBase, IWindowManager
	{
		internal static Lock xLibLock = new (); //The X11 Winforms implementation uses this, so attempt to do the same here.
		private static bool testLoopErrorHandlerInstalled;
		private static readonly XErrorHandler testLoopErrorHandler = HandleTestLoopXError;

		// ToDo: There may be more than only one xDisplay
		private static XDisplay Display => XDisplay.Default;

		private static Wayland.IWaylandBackend WaylandBackend
			=> PlatformManager.IsWaylandSession ? Wayland.WaylandBackend.Current : null;

		private static WindowItemBase CreateWaylandWindow(Wayland.IWaylandBackend backend, Wayland.WaylandWindowInfo info)
			=> new Wayland.WaylandWindowItem(backend, info);

		private static int HandleTestLoopXError(nint displayHandle, ref XErrorEvent errorEvent)
		{
			_ = displayHandle;

			if (errorEvent.error_code == 3)
			{
				Ks.OutputDebugLine($"Suppressed X11 BadWindow during test UI loop: request={errorEvent.request_code} resource=0x{errorEvent.resourceid.ToInt64():x}");
				return 0;
			}

			Ks.OutputDebugLine($"Suppressed X11 error during test UI loop: code={errorEvent.error_code} request={errorEvent.request_code} resource=0x{errorEvent.resourceid.ToInt64():x}");
			return 0;
		}

		internal static void InstallTestLoopXErrorHandler()
		{
			lock (xLibLock)
			{
				if (testLoopErrorHandlerInstalled)
					return;

				_ = Xlib.XSetErrorHandler(testLoopErrorHandler);
				testLoopErrorHandlerInstalled = true;
			}
		}

		internal static bool TryGetWindowProperty(nint displayHandle, long window, nint atom, nint longOffset, nint longLength,
			bool delete, nint reqType, out nint actualType, out int actualFormat, out nint nitems, out nint bytesAfter, out nint prop)
		{
			actualType = 0;
			actualFormat = 0;
			nitems = 0;
			bytesAfter = 0;
			prop = 0;

			if (!PlatformManager.IsX11Available || displayHandle == 0 || window == 0 || atom == 0)
				return false;

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
					var result = Xlib.XGetWindowProperty(displayHandle, window, atom, longOffset, longLength, delete, reqType,
						out actualType, out actualFormat, out nitems, out bytesAfter, ref prop);
					_ = Xlib.XSync(displayHandle, false);

					if (!success || result != 0)
					{
						if (prop != 0)
						{
							_ = Xlib.XFree(prop);
							prop = 0;
						}

						actualType = 0;
						actualFormat = 0;
						nitems = 0;
						bytesAfter = 0;
						return false;
					}

					return true;
				}
				finally
				{
					_ = Xlib.XSetErrorHandler(oldHandler);
				}
			}
		}

		public static WindowItemBase ActiveWindow
		{
			get {
				var backend = WaylandBackend;
				if (backend?.TryGetActiveWindow(out var waylandActive) == true)
					return CreateWaylandWindow(backend, waylandActive);

				if (Wayland.WaylandForeignToplevels.Current?.Active is Wayland.WaylandToplevel foreignActive)
					return new Wayland.WaylandWindowItem(foreignActive);

				var activeId = 0L;
				nint prop = 0;

				if (TryGetWindowProperty(Display.Handle,
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
						out prop))
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
			=> EnumerateWindows(ThreadAccessors.A_DetectHiddenWindows);

		public static IEnumerable<WindowItemBase> EnumerateWindows(bool detectHiddenWindows)
		{
			// AHK on Windows enumerates via EnumWindows, which yields top-to-bottom z-order
			// (index 0 = topmost). The Linux compositor APIs we use here all natively expose
			// the opposite — KWin's workspace.stackingOrder, GNOME's get_window_actors, and
			// X11's XQueryTree all return bottom-to-top — so we reverse at this single layer
			// so WinGetList[1], WinExist tiebreaks, etc. match the Windows convention. The
			// per-backend TryListWindows / TryGetWindowAt paths still see the natural
			// compositor order, which is what their own logic expects.
			var backend = WaylandBackend;
			if (backend?.TryListWindows(detectHiddenWindows, out var backendWindows) == true)
				return backendWindows.Reverse().Select(window => CreateWaylandWindow(backend, window)).ToList();

			var waylandWindows = Wayland.WaylandForeignToplevels.Current?.Enumerate()
				.Select(toplevel => (WindowItemBase)new Wayland.WaylandWindowItem(toplevel))
				.ToList() ?? [];

			if (!PlatformManager.IsX11Available)
			{
				waylandWindows.Reverse();
				return waylandWindows;
			}

			var attr = new XWindowAttributes();
			var filter = (long id) =>
			{
				if (Xlib.XGetWindowAttributes(Display.Handle, id, ref attr) != 0)
					if (detectHiddenWindows || attr.map_state == MapState.IsViewable)
						return true;

				return false;
			};
			var topLevels = new List<WindowItemBase>(waylandWindows);
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

			topLevels.Reverse();
			return topLevels;
		}

		internal WindowManager()
		{
		}

		public static WindowItemBase CreateWindow(nint id)
		{
			var backend = WaylandBackend;
			if (backend?.TryGetWindow(id, out var backendWindow) == true)
				return CreateWaylandWindow(backend, backendWindow);

			if (Wayland.WaylandForeignToplevels.Current?.Get(id) is Wayland.WaylandToplevel waylandToplevel)
				return new Wayland.WaylandWindowItem(waylandToplevel);

			return new WindowItem(id);
		}

		public static IEnumerable<WindowItemBase> FilterForGroups(IEnumerable<WindowItemBase> windows) => windows;

		public static uint GetFocusedCtrlThread(ref nint apControl, nint aWindow) => 0;

		public static nint GetForegroundWindowHandle()
		{
			var backend = WaylandBackend;
			if (backend?.TryGetActiveWindow(out var waylandActive) == true)
				return waylandActive.Handle;

			return new nint(Display.XGetInputFocusHandle());
		}

		public static bool IsWindow(nint handle)
		{
			var backend = WaylandBackend;
			if (backend?.TryGetWindow(handle, out _) == true)
				return true;

			if (Wayland.WaylandForeignToplevels.Current?.IsWindow(handle) == true)
				return true;

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
				//Ks.OutputDebugLine($"MaximizeAll(): Examiniming window: {window.Title}");
				window.WindowState = FormWindowState.Maximized;
			}
		}

		public static void MinimizeAll()
		{
			foreach (var window in AllWindows)
			{
				//Ks.OutputDebugLine($"MinimizeAll(): Examiniming window: {window.Title}");
				window.WindowState = FormWindowState.Minimized;
			}
		}

		public static void MinimizeAllUndo()
		{
			foreach (var window in AllWindows)
			{
				//Ks.OutputDebugLine($"MinimizeAllUndo(): Examiniming window: {window.Title}");
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

		public static WindowItemBase ChildWindowFromPoint(POINT location)
		{
			var backend = WaylandBackend;
			if (backend?.TryGetWindowAt(location.X, location.Y, out var backendWindow) == true)
				return CreateWaylandWindow(backend, backendWindow);

			if (!PlatformManager.IsX11Available)
				return null;

			// Adjust coordinates for DPI scaling if enabled.
#if DPI
			var scale = Accessors.A_ScaledScreenDPI;
			if (scale > 0)
			{
				location.X = (int)(location.X * scale);
				location.Y = (int)(location.Y * scale);
			}
#endif

			var root = (nint)Display.Root.ID;
				return FindWindowAtPointRecursive(root, location.X, location.Y);
		}

		public static WindowItemBase WindowFromPoint(POINT location)
		{
			var child = ChildWindowFromPoint(location);
			if (child == null || !child.IsSpecified)
				return child;

			return child.NonChildParentWindow ?? child;
		}

		private static WindowItemBase FindWindowAtPointRecursive(nint window, int rootX, int rootY)
		{
			var root = (nint)Display.Root.ID;
			var attr = new XWindowAttributes();

			if (Xlib.XGetWindowAttributes(Display.Handle, window, ref attr) == 0)
				return null;

			if (!Xlib.XTranslateCoordinates(Display.Handle, root, window, rootX, rootY, out var winX, out var winY, out _))
				return null;

			if (winX < 0 || winY < 0 || winX >= attr.width + attr.border_width || winY >= attr.height + attr.border_width)
				return null;

			nint childrenReturn = nint.Zero;

			try
			{
				if (Xlib.XQueryTree(Display.Handle, window, out _, out _, out childrenReturn, out var nChildrenReturn) != 0 && childrenReturn != nint.Zero)
				{
					for (var i = nChildrenReturn - 1; i >= 0; i--)
					{
						var child = Marshal.ReadIntPtr(childrenReturn, i * IntPtr.Size);
						if (child == nint.Zero)
							continue;

						var found = FindWindowAtPointRecursive(child, rootX, rootY);
						if (found != null && found.IsSpecified)
							return found;
					}
				}
			}
			finally
			{
				if (childrenReturn != nint.Zero)
					_ = Xlib.XFree(childrenReturn);
			}

			return attr.map_state == MapState.IsViewable ? new WindowItem(new nint(window)) : null;
		}
	}
}
#endif
