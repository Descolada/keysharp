using Keysharp.Builtins;
#if OSX
namespace Keysharp.Internals.Window.MacOS
{
	internal class WindowItem : WindowItemBase
	{
		private readonly Control control;
		private readonly bool isNativeHandle;
		private const long NativeInfoCacheLifetimeTicks = TimeSpan.TicksPerMillisecond * 200;
		private MacNativeWindowInfo nativeInfoCache;
		private bool hasNativeInfoCache;
		private bool nativeInfoCacheIncludesText;
		private long nativeInfoCacheExpiryTicks;

		internal WindowItem(Control source) : base(source?.Handle ?? nint.Zero)
		{
			control = source;
			isNativeHandle = false;
		}

		internal WindowItem(nint handle) : base(handle)
		{
			control = Forms.Control.FromHandle(handle);
			isNativeHandle = control == null;
		}

		internal WindowItem(MacNativeWindowInfo nativeInfo, bool includesTextMetadata) : base((nint)nativeInfo.WindowNumber)
		{
			control = null;
			isNativeHandle = true;
			CacheNativeInfo(nativeInfo, includesTextMetadata);
		}

		private void CacheNativeInfo(MacNativeWindowInfo info, bool includesTextMetadata)
		{
			nativeInfoCache = info;
			hasNativeInfoCache = true;
			nativeInfoCacheIncludesText = includesTextMetadata;
			nativeInfoCacheExpiryTicks = DateTime.UtcNow.Ticks + NativeInfoCacheLifetimeTicks;
		}

		private void InvalidateNativeInfoCache()
		{
			hasNativeInfoCache = false;
			nativeInfoCacheIncludesText = false;
			nativeInfoCacheExpiryTicks = 0;
		}

		private bool TryGetNativeInfo(out MacNativeWindowInfo info, bool includeTextMetadata = false)
		{
			if (!isNativeHandle)
			{
				info = default;
				return false;
			}

			var nowTicks = DateTime.UtcNow.Ticks;

			if (hasNativeInfoCache
				&& nowTicks < nativeInfoCacheExpiryTicks
				&& (!includeTextMetadata || nativeInfoCacheIncludesText))
			{
				info = nativeInfoCache;
				return true;
			}

			if (MacNativeWindows.TryGetWindowInfo(Handle, out var latest, includeTextMetadata))
			{
				CacheNativeInfo(latest, includeTextMetadata);
				info = latest;
				return true;
			}

			InvalidateNativeInfoCache();
			info = default;
			return false;
		}

		private static Form ResolveForm(Control ctrl) => ctrl as Form ?? ctrl?.ParentWindow as Form ?? ctrl?.Parent as Form;

		internal override bool Active
		{
			get
			{
				if (TryGetNativeInfo(out _))
				{
					if (MacAccessibility.TryGetFocusedWindowHandle(out var focused) && focused != 0)
						return focused == Handle;

					var front = MacNativeWindows.GetFrontWindowHandle();
					return front != 0 && front == Handle;
				}

				return control?.HasFocus ?? false;
			}
			set
			{
				if (!value)
					return;

				if (TryGetNativeInfo(out var native))
				{
					_ = MacAccessibility.TryActivateWindow(native);
					return;
				}

				if (control is Window window)
					window.BringToFront();

				control?.Focus();
			}
		}

		internal override bool AlwaysOnTop
		{
			get
			{
				if (TryGetNativeInfo(out _))
					return false; // macOS has no general cross-process "always on top" AX attribute.

				return ResolveForm(control)?.TopMost ?? false;
			}
			set
			{
				if (TryGetNativeInfo(out _))
				{
					Ks.OutputDebugLine("AlwaysOnTop for foreign macOS windows is not implemented yet.");
					return;
				}

				var form = ResolveForm(control);
				if (form != null)
					form.TopMost = value;
			}
		}

		internal override bool Bottom
		{
			set
			{
				if (TryGetNativeInfo(out _))
				{
					Ks.OutputDebugLine("Bottom/Top Z-order control for foreign macOS windows is not implemented yet.");
					return;
				}

				if (control == null)
					return;

				if (value)
				{
					var sendToBack = control.GetType().GetMethod("SendToBack", Type.EmptyTypes);
					sendToBack?.Invoke(control, null);
				}
				else if (control is Window window)
				{
					window.BringToFront();
				}
			}
		}

		internal override HashSet<WindowItemBase> ChildWindows
		{
			get
			{
				var set = new HashSet<WindowItemBase>();

				if (TryGetNativeInfo(out _))
					return set;

				if (control == null)
					return set;

				foreach (var child in control.GetAllControlsRecursive<Control>())
				{
					if (child == null || child is Layout)
						continue;

					set.Add(new WindowItem(child));
				}

				return set;
			}
		}

			internal override string ClassName
			{
				get
				{
					if (TryGetNativeInfo(out var native, includeTextMetadata: true))
						return native.OwnerName.IsNullOrEmpty() ? "NSWindow" : native.OwnerName;

				return control?.GetType().Name ?? DefaultErrorString;
			}
		}

		internal override Rectangle ClientLocation => Location;

		internal override bool Enabled
		{
			get
			{
				if (TryGetNativeInfo(out _))
					return true;

				return control?.Enabled ?? false;
			}
			set
			{
				if (TryGetNativeInfo(out _))
				{
					Ks.OutputDebugLine("Enabled state for foreign macOS windows is not implemented yet.");
					return;
				}

				if (control != null)
					control.Enabled = value;
			}
		}

		internal override bool Exists
		{
			get
			{
				if (TryGetNativeInfo(out _))
					return true;

				return Handle != 0 && control != null;
			}
		}

		internal override long ExStyle
		{
			get => 0;
			set => Ks.OutputDebugLine("ExStyles are not supported on macOS.");
		}

		internal override bool IsHung => false;

			internal override Rectangle Location
			{
				get
				{
					if (TryGetNativeInfo(out var native))
						return native.Bounds;

				return control?.GetBounds() ?? Rectangle.Empty;
			}
				set
				{
					if (TryGetNativeInfo(out var native))
					{
						if (!MacAccessibility.TryMoveResizeWindow(native, value, setPosition: true, setSize: true))
							Ks.OutputDebugLine("Move/Resize for foreign macOS windows failed (likely Accessibility permission or unsupported app).");
						else
							InvalidateNativeInfoCache();
						return;
					}

				control?.SetBounds(value);
			}
		}

		internal override WindowItemBase NonChildParentWindow
		{
			get
			{
				if (TryGetNativeInfo(out _))
					return this;

				return ParentWindow ?? this;
			}
		}

		internal override WindowItemBase ParentWindow
		{
			get
			{
				if (TryGetNativeInfo(out _))
					return null;

				if (control == null)
					return null;

				if (control.ParentWindow is Window parentWindow)
					return new WindowItem(parentWindow);

				if (control.Parent != null)
					return new WindowItem(control.Parent);

				return null;
			}
		}

			internal override long PID => TryGetNativeInfo(out var native) ? native.OwnerPid : Environment.ProcessId;

		internal override Size Size
		{
			get => new Size(Location.Width, Location.Height);
			set => Location = new Rectangle(Location.X, Location.Y, value.Width, value.Height);
		}

		internal override long Style
		{
			get => 0;
			set => Ks.OutputDebugLine("Styles are not supported on macOS.");
		}

			internal override List<string> Text
			{
				get
				{
					if (TryGetNativeInfo(out _, includeTextMetadata: true))
					{
						var title = Title;
						return title.IsNullOrEmpty() ? [] : [title];
					}

					if (string.IsNullOrEmpty(Title))
						return [];

					return [Title];
				}
			}

			internal override string Title
			{
				get
				{
					if (TryGetNativeInfo(out var native, includeTextMetadata: true))
					{
						if (!native.Title.IsNullOrEmpty())
							return native.Title;

						if (MacAccessibility.TryGetWindowTitle(native, out var axTitle) && !axTitle.IsNullOrEmpty())
							return axTitle;

						return string.Empty;
					}

					return control?.Text ?? string.Empty;
				}
				set
				{
					if (TryGetNativeInfo(out _))
				{
					Ks.OutputDebugLine("Setting title of foreign macOS windows is not implemented yet.");
					return;
				}

				if (control != null)
					control.Text = value;
			}
		}

		internal override object Transparency
		{
			get
			{
				if (TryGetNativeInfo(out var native))
					return (long)Math.Clamp(Convert.ToInt32(native.Alpha * 255.0), 0, 255);

				var form = ResolveForm(control);
				if (form == null)
					return -1L;

				var prop = form.GetType().GetProperty("Opacity");
					if (prop?.CanRead == true && prop.GetValue(form) is double opacity)
						return (long)Math.Clamp(Convert.ToInt32(opacity * 255.0), 0, 255);

				return -1L;
			}
			set
			{
				if (TryGetNativeInfo(out _))
				{
					Ks.OutputDebugLine("Opacity control for foreign macOS windows is not implemented yet.");
					return;
				}

				var form = ResolveForm(control);
				if (form == null)
					return;

				var prop = form.GetType().GetProperty("Opacity");
				if (prop?.CanWrite != true)
				{
					Ks.OutputDebugLine("Window opacity is not supported by this macOS backend.");
					return;
				}

				if (value is string s && s.Equals("off", StringComparison.OrdinalIgnoreCase))
				{
					prop.SetValue(form, 1.0);
					return;
				}

				var alpha = Math.Clamp((int)value.Al(), 0, 255);
				prop.SetValue(form, alpha / 255.0);
			}
		}

		internal override object TransparentColor
		{
			get
			{
				Ks.OutputDebugLine("Transparency key/color is not supported on macOS.");
				return 0L;
			}
			set => Ks.OutputDebugLine("Transparency key/color is not supported on macOS.");
		}

		internal override bool Visible
		{
			get
			{
				if (TryGetNativeInfo(out var native))
					return native.Visible;

				return control?.Visible ?? false;
			}
			set
			{
				if (TryGetNativeInfo(out _))
				{
					Ks.OutputDebugLine("Show/Hide for foreign macOS windows is not implemented yet.");
					return;
				}

				if (control != null)
					control.Visible = value;
			}
		}

			internal override FormWindowState WindowState
			{
			get
			{
				if (TryGetNativeInfo(out var native))
				{
					if (MacAccessibility.TryGetWindowState(native, out var state))
						return state;

					return native.Visible ? FormWindowState.Normal : FormWindowState.Minimized;
				}

				return control is Window window ? window.WindowState : FormWindowState.Normal;
			}
				set
				{
					if (TryGetNativeInfo(out var native))
					{
						if (!MacAccessibility.TrySetWindowState(native, value))
							Ks.OutputDebugLine("WindowState for foreign macOS windows failed (likely Accessibility permission or unsupported app).");
						else
							InvalidateNativeInfoCache();
						return;
					}

				if (control is Window window)
					window.WindowState = value;
			}
		}

		internal override void ChildFindPoint(PointAndHwnd pah)
		{
			if (TryGetNativeInfo(out var native))
			{
				if (pah.pt.X >= native.Bounds.Left && pah.pt.X < native.Bounds.Right
					&& pah.pt.Y >= native.Bounds.Top && pah.pt.Y < native.Bounds.Bottom)
				{
					pah.hwndFound = (nint)native.WindowNumber;
					if (MacAccessibility.TryGetElementBoundsAtPoint(native, pah.pt.X, pah.pt.Y, out var childRect)
						&& childRect.Width > 0 && childRect.Height > 0)
						pah.rectFound = childRect;
					else
						pah.rectFound = native.Bounds;
				}
				return;
			}

			if (control == null)
				return;

			pah.hwndFound = control.Handle;
			pah.rectFound = Location;
		}

		internal override void Click(Point? location = null)
		{
			if (TryGetNativeInfo(out var native))
			{
				if (!MacAccessibility.TryClickWindow(native, location, rightButton: false))
					Ks.OutputDebugLine("Native click failed on macOS window.");
				return;
			}

			Ks.OutputDebugLine("ControlClick for Eto controls is not implemented for macOS yet.");
		}

		internal override void ClickRight(Point? location = null)
		{
			if (TryGetNativeInfo(out var native))
			{
				if (!MacAccessibility.TryClickWindow(native, location, rightButton: true))
					Ks.OutputDebugLine("Native right-click failed on macOS window.");
				return;
			}

			Ks.OutputDebugLine("ControlClick Right for Eto controls is not implemented for macOS yet.");
		}

		internal override POINT ClientToScreen()
		{
			if (TryGetNativeInfo(out var native))
				return new POINT(native.Bounds.X, native.Bounds.Y);

			if (control == null)
				return new POINT();

				var pt = control.PointToScreen(Point.Empty);
				return new POINT(Convert.ToInt32(pt.X), Convert.ToInt32(pt.Y));
		}

			internal override bool Close()
			{
				if (TryGetNativeInfo(out var native))
				{
					var closed = MacAccessibility.TryCloseWindow(native);
					if (closed)
						InvalidateNativeInfoCache();
					return closed;
				}

			if (control is Window window)
			{
				window.Close();
				return true;
			}

			return false;
		}

			internal override bool Hide()
			{
				if (TryGetNativeInfo(out var native))
				{
					var hidden = MacAccessibility.TrySetWindowState(native, FormWindowState.Minimized);
					if (hidden)
						InvalidateNativeInfoCache();
					return hidden;
				}

			if (control == null)
				return false;

			control.Visible = false;
			return true;
		}

		internal override bool Kill()
		{
			_ = Close();
			var i = 0;

			while (Exists && i++ < 5)
				System.Threading.Thread.Sleep(0);

			if (!Exists)
				return true;

			try
			{
				using var proc = Process.GetProcessById((int)PID);
				proc.Kill();
			}
			catch
			{
			}

			return !Exists;
		}

		internal override bool Redraw()
		{
			if (TryGetNativeInfo(out _))
				return true;

			if (control == null)
				return false;

			control.Invalidate();
			return true;
		}

			internal override bool Show()
			{
				if (TryGetNativeInfo(out var native))
				{
					var stateOk = MacAccessibility.TrySetWindowState(native, FormWindowState.Normal);
					var activated = MacAccessibility.TryActivateWindow(native);
					if (stateOk || activated)
						InvalidateNativeInfoCache();
					return activated;
				}

			if (control == null)
				return false;

			control.Visible = true;

			if (control is Window window)
				window.BringToFront();

			return true;
		}
	}
}
#endif
