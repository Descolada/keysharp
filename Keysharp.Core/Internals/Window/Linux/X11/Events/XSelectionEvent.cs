using Keysharp.Builtins;
#if LINUX
namespace Keysharp.Internals.Window.Linux.X11
{
	[StructLayout(LayoutKind.Sequential)]
	internal struct XSelectionClearEvent
	{
		internal XEventName type;
		internal nint serial;
		internal bool send_event;
		internal nint display;
		internal nint window;
		internal nint selection;
		internal nint time;
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct XSelectionEvent
	{
		internal XEventName type;
		internal nint serial;
		internal bool send_event;
		internal nint display;
		internal nint requestor;
		internal nint selection;
		internal nint target;
		internal nint property;
		internal nint time;
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct XSelectionRequestEvent
	{
		internal XEventName type;
		internal nint serial;
		internal bool send_event;
		internal nint display;
		internal nint owner;
		internal nint requestor;
		internal nint selection;
		internal nint target;
		internal nint property;
		internal nint time;
	}
}
#endif