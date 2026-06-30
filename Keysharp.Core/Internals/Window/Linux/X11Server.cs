using Keysharp.Builtins;
#if LINUX
namespace Keysharp.Internals.Window.Linux
{
	/// <summary>
	/// Process-wide X11 server helpers shared by the Linux window/event/capture code: the Xlib serialization
	/// lock, a guarded <c>XGetWindowProperty</c>, the EWMH client-message senders, and the test-loop error
	/// handler. These were the X11 internals of the old per-OS WindowManager; they live here now that window
	/// query/control is owned by <see cref="Platform.Window"/> and the neutral <c>WindowSearch</c>.
	/// </summary>
	internal static class X11Server
	{
		// The X11 Winforms implementation uses this lock, so we serialize against it.
		internal static Lock xLibLock = new ();

		private static XDisplay Display => XDisplay.Default;

		private static bool testLoopErrorHandlerInstalled;
		private static readonly XErrorHandler testLoopErrorHandler = HandleTestLoopXError;

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

			if (!Platform.Desktop.IsX11Available || displayHandle == 0 || window == 0 || atom == 0)
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

		internal static void SendNetClientMessage(nint window, nint message_type, nint l0, nint l1, nint l2)
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

		internal static void SendNetWMMessage(nint window, nint message_type, nint l0, nint l1, nint l2, nint l3)
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
	}
}
#endif
