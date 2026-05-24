#if LINUX
namespace Keysharp.Internals.Window.Linux.Wayland
{
	internal sealed class WaylandWindowItem : WindowItemBase
	{
		private readonly IWaylandBackend backend;
		private readonly bool isBackendWindow;
		private readonly WaylandWindowInfo snapshot;

		internal WaylandWindowItem(IWaylandBackend backend, WaylandWindowInfo info)
			: base(info?.Handle ?? 0)
		{
			this.backend = backend;
			snapshot = info;
			isBackendWindow = true;
		}

		internal WaylandWindowItem(WaylandToplevel toplevel)
			: base(toplevel?.Handle ?? 0)
		{
		}

		// WindowItemBase instances are short-lived (see WindowItemBase docs): every enumeration
		// creates fresh ones, so we can hold the snapshot for this item's lifetime instead of
		// firing a fresh KWin scripting round-trip on every property read. Callers that need a
		// fresh view re-query through WindowManager.
		private WaylandWindowInfo Info => snapshot;

		private WaylandToplevel Toplevel => WaylandForeignToplevels.Current?.Get(Handle);

		internal override bool Active
		{
			get => isBackendWindow ? Info?.Active == true : Toplevel?.Activated == true;
			set
			{
				if (value && IsSpecified)
				{
					if (isBackendWindow)
						_ = backend.TryActivateWindow(Handle);
					else
					{
						var manager = WaylandForeignToplevels.Current;
						if (manager?.Get(Handle) is WaylandToplevel toplevel)
							_ = manager.Activate(toplevel);
					}
				}
			}
		}

		internal override bool AlwaysOnTop { get => false; set { } }

		internal override bool Bottom { set { } }

		internal override HashSet<WindowItemBase> ChildWindows => [];

		internal override string ClassName => isBackendWindow ? Info?.AppId ?? DefaultObject : Toplevel?.AppId ?? DefaultObject;

		internal override Rectangle ClientLocation
		{
			get
			{
				if (!isBackendWindow)
					return Rectangle.Empty;

				var rect = Info?.ClientGeometry ?? Rectangle.Empty;
				return new Rectangle(0, 0, rect.Width, rect.Height);
			}
		}

		internal override bool Enabled { get => true; set { } }

		internal override bool Exists => isBackendWindow ? Info != null : Toplevel != null;

		internal override long ExStyle { get => 0L; set { } }

		internal override bool IsHung => false;

		internal override Rectangle Location
		{
			get => isBackendWindow ? Info?.FrameGeometry ?? Rectangle.Empty : Rectangle.Empty;
			set
			{
				if (isBackendWindow && IsSpecified)
					_ = backend.TryMoveResizeWindow(Handle, value, setPosition: true, setSize: false);
			}
		}

		internal override WindowItemBase NonChildParentWindow => isBackendWindow ? this : null;

		internal override WindowItemBase ParentWindow => isBackendWindow ? new WaylandWindowItem(backend, null) : new WaylandWindowItem((WaylandToplevel)null);

		internal override long PID => isBackendWindow ? Info?.PID ?? 0L : 0L;

		internal override Size Size
		{
			get
			{
				if (!isBackendWindow)
					return Size.Empty;

				var rect = Info?.FrameGeometry ?? Rectangle.Empty;
				return new Size(rect.Width, rect.Height);
			}
			set
			{
				if (!isBackendWindow || !IsSpecified)
					return;

				var rect = Info?.FrameGeometry ?? Rectangle.Empty;
				rect.Width = value.Width;
				rect.Height = value.Height;
				_ = backend.TryMoveResizeWindow(Handle, rect, setPosition: false, setSize: true);
			}
		}

		internal override long Style { get => 0L; set { } }

		internal override List<string> Text => [];

		internal override object Transparency { get => 0xFFL; set { } }

		internal override object TransparentColor { get => 0L; set { } }

		internal override bool Visible
		{
			get => isBackendWindow ? Info?.Visible == true : Exists;
			set
			{
				if (!isBackendWindow || !IsSpecified)
					return;

				_ = value
					? backend.TrySetWindowState(Handle, FormWindowState.Normal)
					: backend.TrySetWindowState(Handle, FormWindowState.Minimized);
			}
		}

		internal override string Title
		{
			get => isBackendWindow ? Info?.Title ?? DefaultObject : Toplevel?.Title ?? DefaultObject;
			set { }
		}

		internal override FormWindowState WindowState
		{
			get
			{
				if (!isBackendWindow)
				{
					var toplevel = Toplevel;
					if (toplevel?.Minimized == true)
						return FormWindowState.Minimized;

					return toplevel?.Maximized == true ? FormWindowState.Maximized : FormWindowState.Normal;
				}

				var info = Info;

				if (info?.Minimized == true)
					return FormWindowState.Minimized;

				return info?.Maximized == true ? FormWindowState.Maximized : FormWindowState.Normal;
			}
			set
			{
				if (isBackendWindow && IsSpecified)
					_ = backend.TrySetWindowState(Handle, value);
				else
				{
					var manager = WaylandForeignToplevels.Current;
					if (manager?.Get(Handle) is WaylandToplevel toplevel)
						_ = manager.SetState(toplevel, value);
				}
			}
		}

		internal override void ChildFindPoint(PointAndHwnd pah) { }

		internal override void Click(Point? location = null) { }

		internal override void ClickRight(Point? location = null) { }

		internal override POINT ClientToScreen()
		{
			if (!isBackendWindow)
				return new POINT(0, 0);

			var rect = Info?.ClientGeometry ?? Rectangle.Empty;
			return new POINT(rect.X, rect.Y);
		}

		internal override bool Close()
		{
			if (!IsSpecified)
				return false;

			if (isBackendWindow)
				return backend.TryCloseWindow(Handle);

			var manager = WaylandForeignToplevels.Current;
			return manager?.Get(Handle) is WaylandToplevel toplevel && manager.Close(toplevel);
		}

		internal override bool Hide() => WindowStateSetter(FormWindowState.Minimized);

		internal override bool Kill() => Close();

		internal override bool Redraw() => false;

		internal override bool Show() => WindowStateSetter(FormWindowState.Normal);

		private bool WindowStateSetter(FormWindowState state)
		{
			if (!IsSpecified)
				return false;

			if (isBackendWindow)
				return backend.TrySetWindowState(Handle, state);

			var manager = WaylandForeignToplevels.Current;
			return manager?.Get(Handle) is WaylandToplevel toplevel && manager.SetState(toplevel, state);
		}
	}
}
#endif
