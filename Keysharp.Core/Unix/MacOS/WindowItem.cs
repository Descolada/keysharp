#if OSX
namespace Keysharp.Core.MacOS
{
	internal class WindowItem : Common.Window.WindowItemBase
	{
		private readonly Control control;

		internal WindowItem(Control source) : base(source?.Handle ?? nint.Zero)
		{
			control = source;
		}

		internal WindowItem(nint handle) : base(handle)
		{
			control = Forms.Control.FromHandle(handle);
		}

		internal override bool Active
		{
			get => control?.HasFocus ?? false;
			set
			{
				if (!value || control == null)
					return;

				if (control is Window window)
					window.BringToFront();

				control.Focus();
			}
		}

		internal override bool AlwaysOnTop
		{
			get => false;
			set
			{
			}
		}

		internal override bool Bottom
		{
			set
			{
			}
		}

		internal override HashSet<WindowItemBase> ChildWindows
		{
			get
			{
				var set = new HashSet<WindowItemBase>();

				if (control == null)
					return set;

				foreach (var child in control.VisualControls)
					set.Add(new WindowItem(child));

				return set;
			}
		}

		internal override string ClassName => control?.GetType().Name ?? DefaultErrorString;

		internal override Rectangle ClientLocation => Location;

		internal override bool Enabled
		{
			get => control?.Enabled ?? false;
			set
			{
				if (control != null)
					control.Enabled = value;
			}
		}

		internal override bool Exists => Handle != 0 && control != null;

		internal override long ExStyle
		{
			get => 0;
			set
			{
			}
		}

		internal override bool IsHung => false;

		internal override Rectangle Location
		{
			get => control?.GetBounds() ?? Rectangle.Empty;
			set
			{
				if (control != null)
					control.SetBounds(value);
			}
		}

		internal override WindowItemBase NonChildParentWindow => ParentWindow ?? this;

		internal override WindowItemBase ParentWindow
		{
			get
			{
				if (control == null)
					return null;

				if (control.ParentWindow is Window parentWindow)
					return new WindowItem(parentWindow);

				if (control.Parent != null)
					return new WindowItem(control.Parent);

				return null;
			}
		}

		internal override long PID => Environment.ProcessId;

		internal override Size Size
		{
			get => new Size(Location.Width, Location.Height);
			set => Location = new Rectangle(Location.X, Location.Y, value.Width, value.Height);
		}

		internal override long Style
		{
			get => 0;
			set
			{
			}
		}

		internal override List<string> Text => string.IsNullOrEmpty(Title) ? [] : [Title];

		internal override string Title
		{
			get => control?.Text ?? string.Empty;
			set
			{
				if (control != null)
					control.Text = value;
			}
		}

		internal override object Transparency
		{
			get => null;
			set
			{
			}
		}

		internal override object TransparentColor
		{
			get => null;
			set
			{
			}
		}

		internal override bool Visible
		{
			get => control?.Visible ?? false;
			set
			{
				if (control != null)
					control.Visible = value;
			}
		}

		internal override FormWindowState WindowState
		{
			get => control is Window window ? window.WindowState : FormWindowState.Normal;
			set
			{
				if (control is Window window)
					window.WindowState = value;
			}
		}

		internal override void ChildFindPoint(PointAndHwnd pah)
		{
			if (control == null)
				return;

			pah.hwndFound = control.Handle;
			pah.rectFound = Location;
		}

		internal override void Click(Point? location = null)
		{
		}

		internal override void ClickRight(Point? location = null)
		{
		}

		internal override POINT ClientToScreen()
		{
			if (control == null)
				return new POINT();

			var pt = control.PointToScreen(Point.Empty);
			return new POINT(pt.X.Ai(), pt.Y.Ai());
		}

		internal override bool Close()
		{
			if (control is Window window)
			{
				window.Close();
				return true;
			}

			return false;
		}

		internal override bool Hide()
		{
			if (control == null)
				return false;

			control.Visible = false;
			return true;
		}

		internal override bool Kill()
		{
			return false;
		}

		internal override bool Redraw()
		{
			if (control == null)
				return false;

			control.Invalidate();
			return true;
		}

		internal override bool Show()
		{
			if (control == null)
				return false;

			control.Visible = true;
			return true;
		}
	}
}
#endif
