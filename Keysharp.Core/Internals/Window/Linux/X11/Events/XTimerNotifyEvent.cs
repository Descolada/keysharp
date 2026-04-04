using Keysharp.Builtins;
#if LINUX
namespace Keysharp.Internals.Window.Linux.X11
{
	[StructLayout(LayoutKind.Sequential)]
	internal struct XTimerNotifyEvent
	{
		internal XEventName type;
		internal EventHandler handler;
	}
}
#endif