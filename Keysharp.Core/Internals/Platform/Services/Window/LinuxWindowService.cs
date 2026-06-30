namespace Keysharp.Internals
{

#if LINUX
	internal sealed class LinuxWindow : WindowBase
	{
		// The single home of Linux window dispatch. Every query/control routes through here and fans out three
		// ways by what owns the window: (1) a compositor-IPC Wayland backend (KWin/GNOME/Cinnamon, which
		// self-filters by handle), (2) a wlr-foreign-toplevel window (sway/Hyprland/wlroots/COSMIC), or (3) an
		// X11 window — including OUR OWN toolkit windows, handled inside the X11 read/write helper via
		// Control.FromHandle. The X11 half reuses the proven WindowInfo read/write logic by constructing the
		// X11 item DIRECTLY (never via WindowQuery.CreateWindow, which now returns the neutral WindowInfo that
		// forwards back here — direct construction is what keeps this recursion-free). The neutral WindowInfo
		// holds a cached snapshot for Wayland-backend windows (so per-property reads don't fire a fresh KWin
		// round-trip) and reaches these granular getters only for the LIVE X11/foreign paths.
		private const long WsCaption = 0x00C00000L;

		// Resolved once: the active compositor backend (null on X11 sessions / no-IPC compositors).
		private readonly IWaylandBackend wayland
			= IsWaylandSession ? WaylandBackend.Current : null;

		private static XDisplay Display => XDisplay.Default;

		// X11 read/write helper, constructed directly (NOT WindowQuery.CreateWindow → no recursion). The
		// Wayland branches are always tried first, so this is only reached for genuine X11/own-toolkit handles.
		private static X11Window X11(nint h) => new (h);

		private bool Backend(nint h, out WaylandWindowInfo info)
		{
			info = null;
			return wayland != null && wayland.TryGetWindow(h, out info);
		}

		// Foreign-toplevel handles are the ONLY negative window ids (allocated via nextHandle-- from -1); X11 XIDs
		// and KWin/GNOME synthetic ids are positive. So a non-negative handle can never be a foreign toplevel —
		// skip the wl_display Refresh roundtrip that Get/IsWindow would otherwise do on EVERY property read of an
		// X11/XWayland or compositor-backed window held under a Wayland session.
		private static WaylandToplevel Foreign(nint h)
			=> h < 0 ? WaylandForeignToplevels.Current?.Get(h) : null;

		// "Is this a Wayland window (compositor-backed or foreign-toplevel), i.e. NOT an X11 window?" Used by the
		// getters whose Wayland answer is a constant, so it must not pay a roundtrip: Backend(h) is a cheap dict
		// miss for non-backend handles, and a negative id is by construction a foreign toplevel.
		private bool IsWayland(nint h)
			=> Backend(h, out _) || h < 0;

		// === query: granular single-property reads (LIVE; Wayland-backend served from the item's cache) ===

		public override string GetTitle(nint h)
		{
			if (Backend(h, out var info)) return info.Title ?? DefaultObject;
			if (Foreign(h) is { } tl) return tl.Title ?? DefaultObject;
			return X11(h).Title;
		}

		public override string GetClassName(nint h)
		{
			if (Backend(h, out var info)) return info.ClassName;
			if (Foreign(h) is { } tl) return tl.AppId ?? DefaultObject;
			return X11(h).ClassName;
		}

		public override long GetPid(nint h)
		{
			if (Backend(h, out var info)) return info.PID;
			if (Foreign(h) != null) return 0L;
			return X11(h).PID;
		}

		public override Rectangle GetBounds(nint h)
		{
			if (Backend(h, out var info)) return info.Bounds;
			if (Foreign(h) != null) return Rectangle.Empty;
			return X11(h).Bounds;
		}

		public override Rectangle GetClientBounds(nint h)
		{
			if (Backend(h, out var info)) return info.ClientBounds;
			if (Foreign(h) != null) return Rectangle.Empty;
			return X11(h).ClientBounds;
		}

		public override long GetStyle(nint h)
		{
			// Wayland has no Win32 styles; the only one with a real equivalent is WS_CAPTION <-> decoration.
			if (Backend(h, out var info)) return info.Style;
			if (Foreign(h) != null) return 0L;
			return X11(h).Style;
		}

		public override long GetExStyle(nint h)
		{
			if (IsWayland(h)) return 0L;
			return X11(h).ExStyle;
		}

		public override bool GetActive(nint h)
		{
			if (Backend(h, out var info)) return info.Active;
			if (Foreign(h) is { } tl) return tl.Activated;
			return X11(h).Active;
		}

		public override bool GetVisible(nint h)
		{
			if (Backend(h, out var info)) return info.Visible;
			if (Foreign(h) != null) return true;   // a known foreign-toplevel is, by definition, mapped
			return X11(h).Visible;
		}

		public override bool GetEnabled(nint h)
		{
			if (IsWayland(h)) return true;
			return X11(h).Enabled;
		}

		public override bool GetHung(nint h)
		{
			if (IsWayland(h)) return false;
			return X11(h).IsHung;
		}

		public override bool GetExists(nint h)
		{
			if (Backend(h, out _) || Foreign(h) != null) return true;
			return X11(h).Exists;
		}

		public override FormWindowState GetWindowState(nint h)
		{
			if (Backend(h, out var info)) return info.WindowState;

			if (Foreign(h) is { } tl)
				return tl.Minimized ? FormWindowState.Minimized : tl.Maximized ? FormWindowState.Maximized : FormWindowState.Normal;

			return X11(h).WindowState;
		}

		public override bool GetAlwaysOnTop(nint h)
		{
			if (Backend(h, out var info)) return info.AlwaysOnTop;
			if (Foreign(h) != null) return false;   // no foreign-client way to read keep-above
			return X11(h).AlwaysOnTop;
		}

		public override object GetTransparency(nint h)
		{
			if (IsWayland(h)) return 0xFFL;
			return X11(h).Transparency;
		}

		public override object GetTransparentColor(nint h)
		{
			if (IsWayland(h)) return 0L;
			return X11(h).TransparentColor;
		}

		public override POINT ClientToScreen(nint h)
		{
			if (Backend(h, out var info)) { var r = info.ClientGeometry; return new POINT(r.X, r.Y); }
			if (Foreign(h) != null) return new POINT(0, 0);
			return X11(h).ClientToScreen();
		}

		public override bool TryGetText(nint h, bool detectHidden, out List<string> text)
		{
			if (IsWayland(h)) { text = []; return true; }
			text = X11(h).GetText(new WindowSearchOptions { DetectHiddenText = detectHidden });
			return true;
		}

		public override void ChildFindPoint(nint h, PointAndHwnd pah)
		{
			if (IsWayland(h)) return;
			X11(h).ChildFindPoint(pah);
		}

		public override bool TryClientToScreen(nint h, ref Point pt)
		{
			var origin = ClientToScreen(h);
			pt = new Point(pt.X + origin.X, pt.Y + origin.Y);
			return true;
		}

		public override bool TryGetParent(nint h, out nint parent)
		{
			if (IsWayland(h)) { parent = default; return false; }
			var p = X11(h).ParentWindow;
			parent = p?.Handle ?? 0;
			return p?.Handle is nint v && v != 0;
		}

		public override bool TryGetTopLevel(nint h, out nint top)
		{
			if (Backend(h, out _)) { top = h; return true; }   // a backend toplevel is its own top
			if (Foreign(h) != null) { top = default; return false; }
			var t = X11(h).NonChildParentWindow;
			top = t?.Handle ?? 0;
			return t?.Handle is nint v && v != 0;
		}

		public override bool TryEnumerateChildren(nint h, out IReadOnlyList<nint> children)
		{
			if (IsWayland(h)) { children = []; return true; }
			var list = new List<nint>();

			foreach (var child in X11(h).ChildWindows)
				list.Add(child);

			children = list;
			return true;
		}

		public override nint GetForegroundHandle()
		{
			if (wayland?.TryGetActiveWindow(out var active) == true)
				return active.Handle;

			return new nint(Display.XGetInputFocusHandle());
		}

		public override bool IsWindow(nint h)
		{
			if (wayland?.TryGetWindow(h, out _) == true)
				return true;

			if (WaylandForeignToplevels.Current?.IsWindow(h) == true)
				return true;

			if (!IsX11Available || h == 0)
				return false;

			var attr = new XWindowAttributes();
			var success = true;

			lock (X11Server.xLibLock)
			{
				var oldHandler = Xlib.XSetErrorHandler((nint _, ref XErrorEvent __) =>
				{
					success = false;
					return 0;
				});

				try
				{
					var result = Xlib.XGetWindowAttributes(Display.Handle, h.ToInt64(), ref attr) != 0;
					_ = Xlib.XSync(Display.Handle, false);
					return success && result;
				}
				finally
				{
					_ = Xlib.XSetErrorHandler(oldHandler);
				}
			}
		}

		public override bool TrySetAlwaysOnTop(nint h, bool onTop)
		{
			// Guard on MEMBERSHIP, not on IPC success: a compositor-IPC backend (KWin/GNOME/Cinnamon) can return
			// false on a transient bridge timeout for a window it genuinely manages. Returning that false (so the
			// neutral WindowInfo raises OSError) is correct; falling through to X11 would feed a synthetic
			// compositor id to Xlib as if it were an XID.
			if (Backend(h, out _))
				return wayland.TrySetAlwaysOnTop(h, onTop);

			// Foreign-toplevel compositors (sway/Hyprland/wlroots/COSMIC) expose no keep-above protocol.
			if (WaylandForeignToplevels.Current?.IsWindow(h) == true)
				return false;

			if (!IsX11Available || h == 0)
				return false;

			var disp = Display;

			if (Control.FromHandle(h) is Form form)   // one of our own X11 windows
				form.TopMost = onTop;
			else
				X11Server.SendNetWMMessage(h, disp._NET_WM_STATE, onTop ? 1 : 0, disp._NET_WM_STATE_ABOVE, 0, 0);

			_ = Xlib.XFlush(disp.Handle);
			return true;
		}

		public override bool TryClose(nint h)
		{
			if (Backend(h, out _))   // membership guard (see TrySetAlwaysOnTop): never fall a backend id through to X11.
				return wayland.TryCloseWindow(h);

			var toplevels = WaylandForeignToplevels.Current;
			if (toplevels?.Get(h) is WaylandToplevel tl)
				return toplevels.Close(tl);

			if (!IsX11Available || h == 0)
				return false;

			if (Control.FromHandle(h) is Form form)   // one of our own X11 windows
			{
				if (form.Disposing || form.IsDisposed)
					return false;

				form.Close();
				return true;
			}

			var disp = Display;
			var ev = new XEvent();
			ev.ClientMessageEvent.type = XEventName.ClientMessage;
			ev.ClientMessageEvent.window = h;
			ev.ClientMessageEvent.message_type = disp.WM_PROTOCOLS;
			ev.ClientMessageEvent.format = 32;
			ev.ClientMessageEvent.ptr1 = disp.WM_DELETE_WINDOW;
			return Xlib.XSendEvent(disp.Handle, h, false, EventMasks.NoEvent, ref ev) != 0;
		}

		public override bool TryKill(nint h)
		{
			if (Backend(h, out _) || Foreign(h) != null)   // WaylandWindowItem.Kill => Close
				return TryClose(h);

			return IsX11Available && X11(h).Kill();   // reuses the proven close-then-XKillClient loop
		}

		public override bool TrySetZOrder(nint h, ZOrder z)
		{
			// Wayland-managed windows: stacking is compositor-controlled.
			if (wayland?.TryGetWindow(h, out _) == true || WaylandForeignToplevels.Current?.IsWindow(h) == true)
				return false;

			if (!IsX11Available || h == 0)
				return false;

			var disp = Display;

			if (z == ZOrder.Bottom)
				_ = Xlib.XLowerWindow(disp.Handle, h);
			else
				_ = Xlib.XRaiseWindow(disp.Handle, h);

			_ = Xlib.XFlush(disp.Handle);
			return true;
		}

		public override bool TryHide(nint h)
		{
			if (Backend(h, out _))   // membership guard (see TrySetAlwaysOnTop).
				return wayland.TrySetWindowState(h, FormWindowState.Minimized);

			if (WaylandForeignToplevels.Current is { } tlm && tlm.Get(h) is WaylandToplevel tl)
				return tlm.SetState(tl, FormWindowState.Minimized);

			if (!IsX11Available || h == 0)
				return false;

			if (Control.FromHandle(h) is Control ctrl)   // our own window/control
			{
				ctrl.Visible = false;
				return true;
			}

			return Xlib.XUnmapWindow(Display.Handle, h) != 0;
		}

		public override bool TryShow(nint h)
		{
			if (Backend(h, out _))   // membership guard (see TrySetAlwaysOnTop).
				return wayland.TrySetWindowState(h, FormWindowState.Normal);

			if (WaylandForeignToplevels.Current is { } tlm && tlm.Get(h) is WaylandToplevel tl)
				return tlm.SetState(tl, FormWindowState.Normal);

			if (!IsX11Available || h == 0)
				return false;

			if (Control.FromHandle(h) is Control ctrl)
			{
				ctrl.Visible = true;
				return true;
			}

			return Xlib.XMapWindow(Display.Handle, h) != 0;
		}

		public override bool TryRedraw(nint h)
		{
			if (wayland?.TryGetWindow(h, out _) == true)   // WaylandWindowItem.Redraw => false
				return false;

			if (!IsX11Available || h == 0)
				return false;

			return Xlib.XClearWindow(Display.Handle, h) != 0;
		}

		public override bool TrySetState(nint h, FormWindowState state)
		{
			if (Backend(h, out _))   // membership guard (see TrySetAlwaysOnTop).
				return wayland.TrySetWindowState(h, state);

			if (WaylandForeignToplevels.Current is { } tlm && tlm.Get(h) is WaylandToplevel tl)
				return tlm.SetState(tl, state);

			if (!IsX11Available || h == 0)
				return false;

			var disp = Display;

			if (Control.FromHandle(h) is Form form)
				form.WindowState = state;

			var current = GetWindowState(h);   // reuse the proven X11/Wayland getter

			if (current == state)
				return true;

			switch (state)   // logic mirrors WinForms, lifted verbatim from the X11 WindowInfo
			{
				case FormWindowState.Normal:
					lock (X11Server.xLibLock)
					{
						if (current == FormWindowState.Minimized)
						{
							_ = Xlib.XMapWindow(disp.Handle, h);
							X11Server.SendNetWMMessage(h, disp._NET_ACTIVE_WINDOW, (nint)1, 0, 0, 0);
						}
						else if (current == FormWindowState.Maximized)
						{
							X11Server.SendNetWMMessage(h, disp._NET_WM_STATE, 2 /* toggle */, disp._NET_WM_STATE_MAXIMIZED_HORZ, disp._NET_WM_STATE_MAXIMIZED_VERT, 0);
							X11Server.SendNetWMMessage(h, disp._NET_ACTIVE_WINDOW, (nint)1, 0, 0, 0);
						}
					}

					break;

				case FormWindowState.Minimized:
					lock (X11Server.xLibLock)
					{
						if (current == FormWindowState.Maximized)
							X11Server.SendNetWMMessage(h, disp._NET_WM_STATE, 2 /* toggle */, disp._NET_WM_STATE_MAXIMIZED_HORZ, disp._NET_WM_STATE_MAXIMIZED_VERT, 0);

						_ = Xlib.XIconifyWindow(disp.Handle, h.ToInt64(), disp.ScreenNumber);
					}

					break;

				case FormWindowState.Maximized:
					lock (X11Server.xLibLock)
					{
						if (current == FormWindowState.Minimized)
							_ = Xlib.XMapWindow(disp.Handle, h);

						X11Server.SendNetWMMessage(h, disp._NET_WM_STATE, 1 /* add */, disp._NET_WM_STATE_MAXIMIZED_HORZ, disp._NET_WM_STATE_MAXIMIZED_VERT, 0);
					}

					X11Server.SendNetWMMessage(h, disp._NET_ACTIVE_WINDOW, (nint)1, 0, 0, 0);
					break;
			}

			_ = Xlib.XFlush(disp.Handle);
			return true;
		}

		// The remaining control verbs reuse the proven X11 read/write logic via the directly-constructed X11
		// helper (NOT WindowQuery.CreateWindow → no recursion), with the Wayland branches folded in front.

		public override bool TryMoveResize(nint h, Rectangle bounds, bool setPos, bool setSize)
		{
			if (Backend(h, out var info))
			{
				var rect = info.FrameGeometry;

				if (bounds.X != WindowInfoBase.Unchanged) rect.X = bounds.X;
				if (bounds.Y != WindowInfoBase.Unchanged) rect.Y = bounds.Y;
				if (bounds.Width != WindowInfoBase.Unchanged) rect.Width = bounds.Width;
				if (bounds.Height != WindowInfoBase.Unchanged) rect.Height = bounds.Height;

				if (!wayland.TryMoveResizeWindow(h, rect, setPos, setSize))
					return false;

				if (setPos)
					// If WaylandSelfPositioner is still placing one of our own windows, let it converge here.
					WaylandSelfPositioner.NotifyExternalMove(h, bounds.X, bounds.Y);

				return true;
			}

			if (Foreign(h) != null)
				return false;   // wlr-foreign-toplevel exposes no geometry; the caller raises OSError

			X11(h).Bounds = bounds;
			return true;
		}

		public override bool TryActivate(nint h)
		{
			if (Backend(h, out _))
				return wayland.TryActivateWindow(h);

			if (WaylandForeignToplevels.Current is { } tlm && tlm.Get(h) is WaylandToplevel tl)
				return tlm.Activate(tl);

			X11(h).Active = true;
			return true;
		}

		public override bool TrySetStyle(nint h, long style)
		{
			// Only WS_CAPTION maps to a Wayland concept (the compositor's decoration state).
			if (Backend(h, out _))
				return wayland.TrySetNoBorder(h, (style & WsCaption) != WsCaption);

			if (Foreign(h) != null)
				return false;

			X11(h).Style = style;   // logs "cannot set styles on linux"
			return false;
		}

		public override bool TrySetExStyle(nint h, long exStyle)
		{
			if (IsWayland(h))
				return false;

			X11(h).ExStyle = exStyle;   // logs "ExStyles cannot be set on linux"
			return false;
		}

		public override bool TrySetTransparency(nint h, object alpha)
		{
			if (IsWayland(h))
				return false;   // Wayland opacity is compositor-owned.

			X11(h).Transparency = alpha;
			return true;
		}

		public override bool TryClick(nint h, Point at, uint button, int count)
		{
			if (IsWayland(h))
				return false;   // synthetic clicks are unsupported on Wayland.

			X11(h).Click(at);
			return true;
		}

		public override bool TrySetTitle(nint h, string title)
		{
			if (IsWayland(h))
				return false;   // foreign clients can't have their title set.

			X11(h).Title = title;
			return true;
		}

		public override bool TrySetVisible(nint h, bool visible)
		{
			if (Backend(h, out _))
				return wayland.TrySetWindowState(h, visible ? FormWindowState.Normal : FormWindowState.Minimized);

			if (Foreign(h) != null)
				return false;

			X11(h).Visible = visible;
			return true;
		}

		public override bool TrySetEnabled(nint h, bool enabled)
		{
			if (IsWayland(h))
				return false;

			if (Control.FromHandle(h) is Control ctrl)
			{
				ctrl.Enabled = enabled;
				return true;
			}

			X11(h).Enabled = enabled;   // logs/explains the unsupported foreign-window path.
			return false;
		}

		public override bool TrySetTransparentColor(nint h, object color)
		{
			if (IsWayland(h))
				return false;

			X11(h).TransparentColor = color;   // logs "Transparency key/color not supported on linux."
			return false;
		}

		// === batched query (moved from the per-OS Linux WindowManager; the neutral WindowSearch builds the one
		// WindowInfo from these results — cached for a full snapshot, live for a handle-only one). ===

		public override WindowInfoBase CreateWindow(nint id)
		{
			// The compositor's one-pass payload IS the window (a WaylandWindowInfo : WindowInfoBase) — return it
			// directly; the live by-handle getters below read the same payload type. X11/own toolkit stay lazy.
			if (wayland?.TryGetWindow(id, out var info) == true) return info;                                // KWin/GNOME/Cinnamon
			if (Foreign(id) != null) return new WindowInfo(id);// wlr-foreign-toplevel
			return new WindowInfo(id);                                    // X11 / own toolkit — lazy
		}

		// X11 has no portable "focused control's thread" concept; the focused-layout lookup that uses this is a
		// Windows-only refinement, so report 0 (the legacy Linux behavior).
		public override uint GetFocusedControlThread(nint window, out nint control)
		{
			control = default;
			return 0;
		}

		public override WindowInfoBase ActiveWindow()
		{
			if (wayland?.TryGetActiveWindow(out var wa) == true) return wa;

			if (WaylandForeignToplevels.Current?.Active is WaylandToplevel fa)
				return new WindowInfo(fa.Handle);

			var activeId = 0L;
			nint prop = 0;

			if (X11Server.TryGetWindowProperty(Display.Handle, Display.Root.ID, Display._NET_ACTIVE_WINDOW,
					0, new nint(1), false, (nint)XAtom.AnyPropertyType, out _, out _, out var nitems, out _, out prop))
				if (nitems.ToInt64() > 0 && prop != 0)
					activeId = Marshal.ReadInt64(prop);

			if (prop != 0)
				_ = Xlib.XFree(prop);

			if (activeId != 0) return new WindowInfo((nint)activeId);

			var focused = Display.XGetInputFocusWindow();

			if (focused.ID == 0 || focused.ID == 1) return new WindowInfo(0);

			var top = X11((nint)focused.ID).NonChildParentWindow;
			return new WindowInfo(top?.Handle ?? (nint)focused.ID);
		}

		public override IReadOnlyList<WindowInfoBase> Enumerate(bool includeHidden)
		{
			var list = new List<WindowInfoBase>();

			// AHK yields top-to-bottom z-order (index 0 = topmost). Every Linux source here is natively
			// bottom-to-top, so we reverse at this single layer to match the Windows convention.
			if (wayland?.TryListWindows(includeHidden, out var backendWindows) == true)
			{
				foreach (var w in backendWindows)
					list.Add(w);

				list.Reverse();
				return list;
			}

			if (WaylandForeignToplevels.Current is { } tlm)
				foreach (var tl in tlm.Enumerate())
					list.Add(new WindowInfo(tl.Handle));

			if (!IsX11Available)
			{
				list.Reverse();
				return list;
			}

			var attr = new XWindowAttributes();
			var filter = (long id) =>
			{
				if (Xlib.XGetWindowAttributes(Display.Handle, id, ref attr) != 0)
					if (includeHidden || attr.map_state == MapState.IsViewable)
						return true;

				return false;
			};
			var seen = new HashSet<long>();

			foreach (var w in Display.XQueryTreeRecursive(filter))
			{
				var top = X11((nint)w.ID).NonChildParentWindow;
				var topHandle = top?.Handle ?? 0;

				if (topHandle != 0 && seen.Add(topHandle.ToInt64()))
					list.Add(new WindowInfo(topHandle));
			}

			list.Reverse();
			return list;
		}

		public override bool TryGetAt(int x, int y, out nint child)
		{
			if (wayland?.TryGetWindowAt(x, y, out var info) == true) { child = info.Handle; return true; }

			if (!IsX11Available) { child = default; return false; }

#if DPI
			var scale = A_ScaledScreenDPI;

			if (scale > 0)
			{
				x = (int)(x * scale);
				y = (int)(y * scale);
			}
#endif
			var found = FindWindowAtPointRecursive((nint)Display.Root.ID, x, y);
			child = found;
			return found != 0;
		}

		private static nint FindWindowAtPointRecursive(nint window, int rootX, int rootY)
		{
			var root = (nint)Display.Root.ID;
			var attr = new XWindowAttributes();

			if (Xlib.XGetWindowAttributes(Display.Handle, window, ref attr) == 0)
				return 0;

			if (!Xlib.XTranslateCoordinates(Display.Handle, root, window, rootX, rootY, out var winX, out var winY, out _))
				return 0;

			if (winX < 0 || winY < 0 || winX >= attr.width + attr.border_width || winY >= attr.height + attr.border_width)
				return 0;

			nint childrenReturn = 0;

			try
			{
				if (Xlib.XQueryTree(Display.Handle, window, out _, out _, out childrenReturn, out var nChildren) != 0 && childrenReturn != 0)
				{
					for (var i = nChildren - 1; i >= 0; i--)
					{
						var c = Marshal.ReadIntPtr(childrenReturn, i * IntPtr.Size);

						if (c == 0)
							continue;

						var found = FindWindowAtPointRecursive(c, rootX, rootY);

						if (found != 0)
							return found;
					}
				}
			}
			finally
			{
				if (childrenReturn != 0)
					_ = Xlib.XFree(childrenReturn);
			}

			return attr.map_state == MapState.IsViewable ? window : 0;
		}
	}
#endif
}
