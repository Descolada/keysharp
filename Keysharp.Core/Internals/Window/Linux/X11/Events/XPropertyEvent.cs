using Keysharp.Builtins;
#if LINUX
namespace Keysharp.Internals.Window.Linux.X11
{
	[StructLayout(LayoutKind.Sequential)]
	internal struct XPropertyEvent
	{
		internal XEventName type;
		internal nint serial;
		internal bool send_event;
		internal nint display;
		internal int window;
		internal XAtom atom;
		internal nint time;
		internal int state;
	}
}
#endif