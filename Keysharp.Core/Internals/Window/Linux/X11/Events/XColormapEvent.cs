using Keysharp.Builtins;
#if LINUX
namespace Keysharp.Internals.Window.Linux.X11
{
	[StructLayout(LayoutKind.Sequential)]
	internal struct XColormapEvent
	{
		internal XEventName type;
		internal nint serial;
		internal bool send_event;
		internal nint display;
		internal nint window;
		internal nint colormap;
		internal bool c_new;
		internal int state;
	}
}
#endif