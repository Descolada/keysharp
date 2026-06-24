using Keysharp.Builtins;
#if LINUX
namespace Keysharp.Internals.Window.Linux.X11
{
	[StructLayout(LayoutKind.Sequential)]
	internal struct XDestroyWindowEvent
	{
		internal XEventName type;
		internal nint serial;
		internal bool send_event;
		internal nint display;
		internal nint xevent;
		internal nint window;   // Window/XID is 8 bytes on 64-bit (value happened to read correctly here as the
		                        // last field, but keep it pointer-width for correctness and consistency).
	}
}
#endif