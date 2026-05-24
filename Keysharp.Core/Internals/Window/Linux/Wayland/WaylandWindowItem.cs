#if LINUX
namespace Keysharp.Internals.Window.Linux.Wayland
{
	/// <summary>
	/// Foreign Wayland toplevel exposed by a taskbar-oriented compositor protocol.
	/// These protocols expose title, app-id, activation state, and window state only.
	/// Geometry, PID, child controls, and window text are not available by protocol,
	/// so the corresponding accessors silently return defaults (matching the behavior
	/// of an unspecified X11 WindowItem on a pre-Wayland session).
	/// </summary>
	internal sealed class WaylandWindowItem : WindowItemBase
	{
		internal WaylandWindowItem(WaylandToplevel toplevel)
			: base(toplevel?.Handle ?? 0)
		{
		}

		private WaylandToplevel Toplevel => WaylandForeignToplevels.Current?.Get(Handle);

		internal override bool Active
		{
			get => Toplevel?.Activated == true;
			set
			{
				var manager = WaylandForeignToplevels.Current;
				if (value && manager?.Get(Handle) is WaylandToplevel toplevel)
					_ = manager.Activate(toplevel);
			}
		}

		internal override bool AlwaysOnTop { get => false; set { } }

		internal override bool Bottom { set { } }

		internal override HashSet<WindowItemBase> ChildWindows => [];

		internal override string ClassName => Toplevel?.AppId ?? DefaultObject;

		internal override Rectangle ClientLocation => Rectangle.Empty;

		internal override bool Enabled { get => true; set { } }

		internal override bool Exists => Toplevel != null;

		internal override long ExStyle { get => 0L; set { } }

		internal override bool IsHung => false;

		internal override Rectangle Location { get => Rectangle.Empty; set { } }

		internal override WindowItemBase NonChildParentWindow => null;

		internal override WindowItemBase ParentWindow => new WaylandWindowItem(null);

		internal override long PID => 0L;

		internal override Size Size { get => Size.Empty; set { } }

		internal override long Style { get => 0L; set { } }

		internal override List<string> Text => [];

		internal override object Transparency { get => 0xFFL; set { } }

		internal override object TransparentColor { get => 0L; set { } }

		internal override bool Visible { get => Exists; set { } }

		internal override string Title
		{
			get => Toplevel?.Title ?? DefaultObject;
			set { }
		}

		internal override FormWindowState WindowState
		{
			get
			{
				var toplevel = Toplevel;
				if (toplevel?.Minimized == true)
					return FormWindowState.Minimized;

				return toplevel?.Maximized == true ? FormWindowState.Maximized : FormWindowState.Normal;
			}
			set
			{
				var manager = WaylandForeignToplevels.Current;
				if (manager?.Get(Handle) is WaylandToplevel toplevel)
					_ = manager.SetState(toplevel, value);
			}
		}

		internal override void ChildFindPoint(PointAndHwnd pah) { }

		internal override void Click(Point? location = null) { }

		internal override void ClickRight(Point? location = null) { }

		internal override POINT ClientToScreen() => new(0, 0);

		internal override bool Close()
		{
			var manager = WaylandForeignToplevels.Current;
			return manager?.Get(Handle) is WaylandToplevel toplevel && manager.Close(toplevel);
		}

		internal override bool Hide() => WindowStateSetter(FormWindowState.Minimized);
		// Wayland has no forceful kill; sends a close request the application may refuse.
		internal override bool Kill() => Close();
		internal override bool Redraw() => false;
		internal override bool Show() => WindowStateSetter(FormWindowState.Normal);

		private bool WindowStateSetter(FormWindowState state)
		{
			var manager = WaylandForeignToplevels.Current;
			return manager?.Get(Handle) is WaylandToplevel toplevel && manager.SetState(toplevel, state);
		}
	}
}
#endif
