using Keysharp.Builtins;
#if LINUX
namespace Keysharp.Internals.Window.Linux.X11
{
	[StructLayout(LayoutKind.Sequential)]
	internal struct XFocusChangeEvent
	{
		internal XEventName type;
		internal nint serial;
		internal bool send_event;
		internal nint display;
		internal nint window;
		internal int mode;
		internal NotifyDetail detail;
	}
}
#endif