#if OSX
namespace Keysharp.Internals.Window.MacOS
{
	/// <summary>
	/// The neutral window for a macOS window: a <see cref="WindowInfoBase"/> whose scalar state IS its 1:1
	/// <see cref="MacWindowSnapshot"/> (one kCGWindow batch fetch, frozen for life). Symmetric with the Wayland
	/// <c>WaylandWindowInfo</c> and the Eto <c>ControlInfo</c> — each is a per-backend subtype reading scalars
	/// from its own source. The non-scalar members (text, children, parent, client-origin) come from the shared
	/// <see cref="WindowInfoBase"/>, which routes them by handle through <see cref="MacWindow"/> to a FRESH
	/// <see cref="MacWindowSnapshot"/> (a different type), so there is no recursion. Windows/X11 use the lazy
	/// <see cref="WindowInfo"/>.
	/// </summary>
	internal sealed class MacWindowInfo : WindowInfoBase
	{
		private readonly MacWindowSnapshot s;

		internal MacWindowInfo(MacWindowSnapshot snapshot) : base(snapshot.Handle) => s = snapshot;

		internal override string Title => s.Title;
		internal override string ClassName => s.ClassName;
		internal override long PID => s.PID;
		internal override Rectangle Bounds => s.Bounds;
		internal override Rectangle ClientBounds => s.ClientBounds;
		internal override long Style => s.Style;
		internal override long ExStyle => s.ExStyle;
		internal override bool Active => s.Active;
		internal override bool Visible => s.Visible;
		internal override bool Enabled => s.Enabled;
		internal override bool IsHung => s.IsHung;
		internal override bool Exists => s.Exists;
		internal override FormWindowState WindowState => s.WindowState;
		internal override bool AlwaysOnTop => s.AlwaysOnTop;
		internal override object Transparency => s.Transparency;
		internal override object TransparentColor => s.TransparentColor;
	}
}
#endif
