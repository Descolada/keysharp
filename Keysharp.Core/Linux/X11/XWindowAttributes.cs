﻿#if LINUX
namespace Keysharp.Core.Linux.X11
{
	[StructLayout(LayoutKind.Sequential)]
	internal struct XSetWindowAttributes
	{
		internal nint background_pixmap;
		internal nint background_pixel;
		internal nint border_pixmap;
		internal nint border_pixel;
		internal Gravity bit_gravity;
		internal Gravity win_gravity;
		internal int backing_store;
		internal nint backing_planes;
		internal nint backing_pixel;
		internal bool save_under;
		internal nint event_mask;
		internal nint do_not_propagate_mask;
		internal bool override_redirect;
		internal nint colormap;
		internal nint cursor;
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct XWindowAttributes
	{
		internal int x;
		internal int y;
		internal int width;
		internal int height;
		internal int border_width;
		internal int depth;
		internal nint visual;
		internal nint root;
		internal int c_class;
		internal Gravity bit_gravity;
		internal Gravity win_gravity;
		internal int backing_store;
		internal nint backing_planes;
		internal nint backing_pixel;
		internal bool save_under;
		internal nint colormap;
		internal bool map_installed;
		internal MapState map_state;
		internal nint all_event_masks;
		internal nint your_event_mask;
		internal nint do_not_propagate_mask;
		internal bool override_direct;
		internal nint screen;
	}
}
#endif