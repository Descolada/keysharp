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
		internal nint window;   // Window/XID is unsigned long (8 bytes) on 64-bit; declaring it as a 4-byte int
		internal nint atom;     // truncated the field and shifted atom (and time/state) off by 4 bytes, so atom
		internal nint time;     // read the always-zero high half of window. Both must be pointer-width.
		internal int state;
	}
}
#endif