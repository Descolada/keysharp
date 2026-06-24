using Keysharp.Builtins;
#if LINUX
namespace Keysharp.Internals.Window.Linux.X11
{
	[StructLayout(LayoutKind.Sequential)]
	internal struct XCreateWindowEvent
	{
		internal XEventName type;
		internal nint serial;
		internal bool send_event;
		internal nint display;
		internal nint parent;
		internal nint window;   // Window/XID is 8 bytes on 64-bit; a 4-byte uint here shifts every following
		internal int x;         // field (notably override_redirect) by 4 bytes, making them unreadable.
		internal int y;
		internal int width;
		internal int height;
		internal int border_width;
		internal bool override_redirect;
	}
}
#endif