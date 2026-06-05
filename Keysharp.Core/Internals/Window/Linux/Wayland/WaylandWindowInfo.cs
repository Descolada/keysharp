#if LINUX
namespace Keysharp.Internals.Window.Linux.Wayland
{
	internal sealed class WaylandWindowInfo
	{
		internal nint Handle { get; init; }
		internal string CompositorId { get; init; } = string.Empty;
		internal string Title { get; init; } = string.Empty;
		internal string AppId { get; init; } = string.Empty;
		internal long PID { get; init; }
		internal Rectangle FrameGeometry { get; init; } = Rectangle.Empty;
		internal Rectangle ClientGeometry { get; init; } = Rectangle.Empty;
		internal bool Active { get; init; }
		internal bool Minimized { get; init; }
		internal bool Maximized { get; init; }
		internal bool Visible { get; init; }
	}
}
#endif
