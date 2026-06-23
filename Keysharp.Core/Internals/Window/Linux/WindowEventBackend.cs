#if LINUX
using Gdk;
using Keysharp.Builtins;
using Keysharp.Internals.Window.Linux.X11;

namespace Keysharp.Internals.Window.Linux
{
	/// <summary>
	/// Linux <see cref="IWindowEventBackend"/>. Rather than running a private X11 connection on its own
	/// <c>XNextEvent</c> pump (which would race the GDK/GLib main loop for events on a shared connection), this
	/// hooks GDK's existing event loop on the UI thread: it adds a GDK filter to the root window (the same
	/// mechanism <c>Unix/MessageFilter</c> uses for <c>OnMessage</c>) and, on GDK's own X connection, OR-s the
	/// root event mask with <c>SubstructureNotify | PropertyChange</c> so the events we need are delivered
	/// without clobbering the masks GDK relies on. The filter receives the raw <c>XEvent</c>, normalizes it, and
	/// never consumes it. Because <c>GDK_BACKEND=x11</c> is forced (Script.cs), this works on X11 and on Wayland
	/// via XWayland; native Wayland sources (GNOME/KWin/wlroots) are not yet wired, and <c>Restore</c> is not yet
	/// emitted.
	/// </summary>
	internal sealed class WindowEventBackend : IWindowEventBackend
	{
		[DllImport("libgdk-3.so.0")]
		private static extern nint gdk_x11_get_default_xdisplay();

		private readonly Lock gate = new();
		private WindowEventMask enabledMask = WindowEventMask.None;
		private FilterFunc filter;             // kept alive while attached so GDK can call it
		private bool filterAttached;
		private long rootXid;
		private bool disposed;

		public Action<WindowEventRaw> Sink { get; set; }

		public void Start(WindowEventMask mask)
		{
			lock (gate)
			{
				if (disposed)
					return;

				enabledMask |= mask;

				if (filterAttached)
					return;
			}

			Script.PostToUIThread(AttachOnUI);
		}

		public void Stop(WindowEventMask mask)
		{
			bool detach;

			lock (gate)
			{
				enabledMask &= ~mask;
				detach = enabledMask == WindowEventMask.None;
			}

			if (detach)
				Script.PostToUIThread(DetachOnUI);
		}

		public void Dispose()
		{
			disposed = true;

			lock (gate)
				enabledMask = WindowEventMask.None;

			Script.PostToUIThread(DetachOnUI);
		}

		// ---- UI-thread attach/detach (runs on the GDK main loop) ----------------------------

		private void AttachOnUI()
		{
			lock (gate)
				if (disposed || filterAttached)
					return;

			var root = Gdk.Screen.Default?.RootWindow;

			if (root == null)
			{
				Ks.OutputDebugLine("WinEvent: no GDK root window for the X11 event filter.");
				return;
			}

			// Add our event mask to GDK's X connection on the root window, preserving GDK's existing mask.
			try
			{
				var dpy = gdk_x11_get_default_xdisplay();

				if (dpy != 0)
				{
					rootXid = Xlib.XDefaultRootWindow(dpy);
					var attr = new XWindowAttributes();

					if (Xlib.XGetWindowAttributes(dpy, rootXid, ref attr) != 0)
					{
						var combined = (EventMasks)attr.your_event_mask.ToInt64() | EventMasks.SubstructureNofity | EventMasks.PropertyChange;
						_ = Xlib.XSelectInput(dpy, rootXid, combined);
						_ = Xlib.XFlush(dpy);
					}
				}
			}
			catch (Exception ex)
			{
				Ks.OutputDebugLine($"WinEvent X11 root-mask selection failed: {ex.Message}");
			}

			filter = Filter;

			try
			{
				root.AddFilter(filter);

				lock (gate)
					filterAttached = true;
			}
			catch (Exception ex)
			{
				filter = null;
				Ks.OutputDebugLine($"WinEvent X11 filter attach failed: {ex.Message}");
			}
		}

		private void DetachOnUI()
		{
			FilterFunc f;

			lock (gate)
			{
				if (!filterAttached)
					return;

				f = filter;
				filter = null;
				filterAttached = false;
			}

			// Leave the extra root mask selected: it is harmless (GDK ignores the surplus events) and restoring
			// it could remove masks GDK added in the meantime.
			try
			{
				Gdk.Screen.Default?.RootWindow?.RemoveFilter(f);
			}
			catch (Exception ex)
			{
				Ks.OutputDebugLine($"WinEvent X11 filter detach failed: {ex.Message}");
			}
		}

		// ---- GDK filter (runs on the UI thread, must never block or consume) ----------------

		private FilterReturn Filter(nint xevent, Event evnt)
		{
			try
			{
				if (xevent != 0)
					Handle(xevent);
			}
			catch (Exception ex)
			{
				Ks.OutputDebugLine($"WinEvent X11 filter error: {ex.Message}");
			}

			return FilterReturn.Continue;   // never consume — let GDK process the event normally
		}

		private void Handle(nint xeventPtr)
		{
			var sink = Sink;

			if (sink == null)
				return;

			WindowEventMask mask;

			lock (gate)
				mask = enabledMask;

			if (mask == WindowEventMask.None)
				return;

			var ev = Marshal.PtrToStructure<XEvent>(xeventPtr);

			switch (ev.type)
			{
				case XEventName.CreateNotify:
					// Only top-level windows (direct children of root).
					if ((mask & WindowEventMask.Create) != 0 && ev.CreateWindowEvent.parent == rootXid)
						sink(new WindowEventRaw(WindowEventType.Create, (nint)ev.CreateWindowEvent.window, NowMs()));
					break;

				case XEventName.DestroyNotify:
					if ((mask & WindowEventMask.Close) != 0)
						sink(new WindowEventRaw(WindowEventType.Close, (nint)ev.DestroyWindowEvent.window, NowMs()));
					break;

				case XEventName.MapNotify:
					if ((mask & WindowEventMask.Show) != 0)
						sink(new WindowEventRaw(WindowEventType.Show, ev.MapEvent.window, NowMs()));
					break;

				case XEventName.UnmapNotify:
					// X11 has no dedicated "minimize" event; an iconified top-level is unmapped.
					if ((mask & WindowEventMask.Minimize) != 0)
						sink(new WindowEventRaw(WindowEventType.Minimize, ev.UnmapEvent.window, NowMs()));
					break;

				case XEventName.ConfigureNotify:
					if ((mask & WindowEventMask.Move) != 0)
						sink(new WindowEventRaw(WindowEventType.Move, ev.ConfigureEvent.window, NowMs()));
					break;

				case XEventName.PropertyNotify:
					HandleProperty(ref ev, mask, sink);
					break;
			}
		}

		private void HandleProperty(ref XEvent ev, WindowEventMask mask, Action<WindowEventRaw> sink)
		{
			var atom = (uint)ev.PropertyEvent.atom;

			if ((mask & WindowEventMask.Active) != 0 && (long)ev.PropertyEvent.window == rootXid && atom == (uint)XDisplay.Default._NET_ACTIVE_WINDOW)
			{
				var active = ReadActiveWindow();

				if (active != 0)
					sink(new WindowEventRaw(WindowEventType.Active, active, NowMs()));

				return;
			}

			if ((mask & WindowEventMask.TitleChange) != 0 && (atom == (uint)XDisplay.Default._NET_WM_NAME || atom == (uint)XAtom.XA_WM_NAME))
				sink(new WindowEventRaw(WindowEventType.TitleChange, (nint)ev.PropertyEvent.window, NowMs()));
		}

		private static nint ReadActiveWindow()
		{
			nint prop = 0;

			try
			{
				if (WindowManager.TryGetWindowProperty(XDisplay.Default.Handle, XDisplay.Default.Root.ID, XDisplay.Default._NET_ACTIVE_WINDOW,
					0, new nint(1), false, (nint)XAtom.AnyPropertyType, out _, out _, out var nitems, out _, out prop)
					&& nitems.ToInt64() > 0 && prop != 0)
				{
					return new nint(Marshal.ReadInt64(prop));
				}
			}
			catch
			{
			}
			finally
			{
				if (prop != 0)
					_ = Xlib.XFree(prop);
			}

			return 0;
		}

		private static long NowMs() => Environment.TickCount64;
	}
}
#endif
