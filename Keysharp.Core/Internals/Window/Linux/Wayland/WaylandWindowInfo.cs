#if LINUX
namespace Keysharp.Internals.Window.Linux.Wayland
{
	/// <summary>
	/// The neutral window for a compositor-backed Wayland window: a <see cref="WindowInfoBase"/> that holds the
	/// compositor's one-pass payload (KWin/GNOME/Cinnamon IPC) directly — no separate snapshot object, since the
	/// payload is eager and immutable. Symmetric with the Eto <c>ControlInfo</c> and macOS's <c>MacWindowInfo</c>:
	/// a per-backend subtype that reads scalars from its own source. The non-scalar members (text, children,
	/// parent, client-origin) come from the shared <see cref="WindowInfoBase"/>. Windows/X11 use the lazy
	/// <see cref="WindowInfo"/>. The backend-shaped fields (AppId, Minimized/Maximized, Decorated, *Geometry) are
	/// translated to the neutral surface here; the Wayland-specific extras are exposed on top.
	/// </summary>
	internal sealed class WaylandWindowInfo : WindowInfoBase
	{
		private const long WsCaption = 0x00C00000L;   // WS_CAPTION: a decorated (titlebar) toplevel

		// scalars backing the neutral overrides (named to avoid colliding with the base getters)
		private readonly string winTitle;
		private readonly long pid;
		private readonly object transparency;
		private readonly bool active, visible, alwaysOnTop;

		// --- Wayland-specific payload, read by LinuxWindow internals + WaylandSelfPositioner ---
		public string CompositorId { get; }
		public string AppId { get; }
		public Rectangle FrameGeometry { get; }
		public Rectangle ClientGeometry { get; }
		public bool Minimized { get; }
		public bool Maximized { get; }
		public bool Decorated { get; }

		internal WaylandWindowInfo(nint handle, string compositorId = "", string title = "", string appId = "",
								   long pid = 0, Rectangle frameGeometry = default, Rectangle clientGeometry = default,
								   bool active = false, bool minimized = false, bool maximized = false,
								   bool visible = false, bool alwaysOnTop = false, bool decorated = true,
								   object transparency = null) : base(handle)
		{
			CompositorId = compositorId ?? string.Empty;
			winTitle = title ?? string.Empty;
			AppId = appId ?? string.Empty;
			this.pid = pid;
			FrameGeometry = frameGeometry;
			ClientGeometry = clientGeometry;
			this.active = active;
			Minimized = minimized;
			Maximized = maximized;
			this.visible = visible;
			this.alwaysOnTop = alwaysOnTop;
			Decorated = decorated;
			this.transparency = transparency ?? 0xFFL;
		}

		internal override string Title => winTitle;
		internal override string ClassName => AppId;
		internal override long PID => pid;
		internal override Rectangle Bounds => FrameGeometry;
		internal override Rectangle ClientBounds => ClientGeometry;
		internal override long Style => Decorated ? WsCaption : 0L;
		internal override long ExStyle => 0L;
		internal override bool Active => active;
		internal override bool Visible => visible;
		internal override bool Enabled => true;
		internal override bool IsHung => false;
		internal override bool Exists => true;
		internal override FormWindowState WindowState => Minimized ? FormWindowState.Minimized : Maximized ? FormWindowState.Maximized : FormWindowState.Normal;
		internal override bool AlwaysOnTop => alwaysOnTop;
		internal override object Transparency => transparency;
		internal override object TransparentColor => 0L;
	}
}
#endif
