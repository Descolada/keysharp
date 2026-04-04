using Keysharp.Builtins;
#if LINUX
namespace Keysharp.Internals.Window.Linux.X11
{
	[StructLayout(LayoutKind.Sequential)]
	internal struct XMapEvent
	{
		internal XEventName type;
		internal nint serial;
		internal bool send_event;
		internal nint display;
		internal nint xevent;
		internal nint window;
		internal bool override_redirect;
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct XMapRequestEvent
	{
		internal XEventName type;
		internal nint serial;
		internal bool send_event;
		internal nint display;
		internal nint parent;
		internal nint window;
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct XUnmapEvent
	{
		internal XEventName type;
		internal nint serial;
		internal bool send_event;
		internal nint display;
		internal nint xevent;
		internal nint window;
		internal bool from_configure;
	}
}
#endif