﻿#if LINUX
namespace Keysharp.Core.Linux.X11
{
	[StructLayout(LayoutKind.Sequential)]
	internal struct XKeyEvent
	{
		internal XEventName type;
		internal nint serial;
		internal bool send_event;
		internal nint display;
		internal nint window;
		internal nint root;
		internal nint subwindow;
		internal nint time;
		internal int x;
		internal int y;
		internal int x_root;
		internal int y_root;
		internal int state;
		internal XKeys keycode;
		internal bool same_screen;
	}
}
#endif