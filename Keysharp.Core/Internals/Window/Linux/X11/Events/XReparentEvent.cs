using Keysharp.Builtins;
#if LINUX
namespace Keysharp.Internals.Window.Linux.X11
{
	[StructLayout(LayoutKind.Sequential)]
	internal struct XReparentEvent
	{
		internal XEventName type;
		internal nint serial;
		internal bool send_event;
		internal nint display;
		internal nint xevent;
		internal nint window;
		internal nint parent;
		internal int x;
		internal int y;
		internal bool override_redirect;
	}
}
#endif