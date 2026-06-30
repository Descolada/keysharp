namespace Keysharp.Internals
{
	// The capability-service contracts the Platform host exposes. Following IWaylandBackend's proven idiom,
	// methods are Try*/return-bool so a partially-capable backend degrades gracefully rather than throwing.

	internal enum ZOrder { Top, Bottom }

	internal enum OverlayKind { Eto, LayerSurface, Compositor }

	/// <summary>The currently-live keyboard/mouse injection transport. On Linux it can make one sticky forward
	/// transition (inputd → legacy X11/XTEST); on macOS/Windows it is fixed. Kept as a typed value (not a bool)
	/// so additional transports/state can be added without churning consumers.</summary>
	internal enum InputTransport { None, Inputd, LegacyX11, Mac, Windows }

	/// <summary>A handle to one of OUR OWN toolkit windows, resolved from a <see cref="nint"/> by
	/// <see cref="IOwnWindow.TryResolve"/>. Carries what the own-window ops need (the toolkit form) without
	/// leaking the high-level <c>Gui</c> builtin into the platform layer.</summary>
	internal interface IOwnWindowTarget
	{
		nint FormHandle { get; }
	}

	/// <summary>Foreign window READ surface (by handle). The Linux impl internally composes X11 + the active
	/// Wayland backend; routing re-resolves the backend from the handle on each call.</summary>
	internal interface IWindowQuery
	{
		/// <summary>Build the neutral, read-only window for a bare id, as the per-backend <c>WindowInfoBase</c>
		/// subtype: a <c>MacWindowInfo</c> (seeded from the kCGWindow batch) or <c>WaylandWindowInfo</c> (the
		/// compositor payload) where the backend batches, else a lazy <c>WindowInfo</c> (X11/Windows) that fills
		/// per property live on first access.</summary>
		Keysharp.Internals.Window.WindowInfoBase CreateWindow(nint id);

		/// <summary>The active window as a (seeded-where-cheap) WindowInfo; an unspecified item (handle 0) when
		/// there is none.</summary>
		Keysharp.Internals.Window.WindowInfoBase ActiveWindow();

		/// <summary>All top-level windows, top-of-z-order first — seeded where the platform enumerates in one
		/// batch (Wayland/macOS), empty/lazy otherwise (X11/Windows).</summary>
		IReadOnlyList<Keysharp.Internals.Window.WindowInfoBase> Enumerate(bool includeHidden);

		bool TryGetAt(int x, int y, out nint child);

		/// <summary>Direct, non-enumerating lookup of the first top-level window by class name (and, in
		/// title-match-mode 3, exact title) — the platform's native FindWindow. Returns true when the backend
		/// has such a fast path (Windows): <paramref name="handle"/> is the match, or value 0 when the backend
		/// can definitively say no such window exists (so the caller skips the O(N) enumeration entirely).
		/// Returns false where there is no native fast path (X11/Wayland/macOS), and the caller enumerates.</summary>
		bool TryFindWindow(string className, string title, out nint handle);

		bool IsWindow(nint h);
		nint GetForegroundHandle();
		bool TryGetParent(nint h, out nint parent);
		bool TryGetTopLevel(nint h, out nint top);
		bool TryEnumerateChildren(nint h, out IReadOnlyList<nint> children);
		bool TryClientToScreen(nint h, ref Point pt);
		uint GetFocusedControlThread(nint window, out nint control);

		/// <summary>Whether the window belongs in alt-tab/group enumerations (Windows filters out topmost/
		/// tool/owned/shell windows); true everywhere else.</summary>
		bool IncludeInGroups(nint h);

		/// <summary>The window's text lines (the title plus, optionally, hidden child text). Not seeded into the
		/// item's cache because it is a per-call recursive tree-walk, not a cheap batched field — it stays lazy.</summary>
		bool TryGetText(nint h, bool detectHidden, out List<string> text);

		/// <summary>Deepest child window at <see cref="Keysharp.Internals.Window.PointAndHwnd.pt"/> (hit-test used
		/// by ControlFromPoint); mutates <paramref name="pah"/> in place, mirroring the legacy item method.</summary>
		void ChildFindPoint(nint h, Keysharp.Internals.Window.PointAndHwnd pah);

		// Granular single-property reads. These exist so the neutral WindowInfo can read a foreign window's
		// VOLATILE state LIVE per access (an X11/foreign window's Active/Visible/Bounds can change between two
		// reads of the same item — WinWaitActive loops on one held item), exactly as the legacy per-OS items
		// did, instead of paying a full batched snapshot per single-property access. (A Wayland-backend window
		// is served from the item's cached snapshot and never reaches these.)
		string GetTitle(nint h);
		string GetClassName(nint h);
		long GetPid(nint h);
		Rectangle GetBounds(nint h);
		Rectangle GetClientBounds(nint h);
		long GetStyle(nint h);
		long GetExStyle(nint h);
		bool GetActive(nint h);
		bool GetVisible(nint h);
		bool GetEnabled(nint h);
		bool GetHung(nint h);
		bool GetExists(nint h);
		FormWindowState GetWindowState(nint h);
		bool GetAlwaysOnTop(nint h);
		object GetTransparency(nint h);
		object GetTransparentColor(nint h);
		Keysharp.Internals.Window.POINT ClientToScreen(nint h);
	}

	/// <summary>Foreign window MUTATE surface (by handle). Unsupported per-window ops return false; the
	/// caller (WindowInfo) raises OSError.</summary>
	internal interface IWindowControl
	{
		bool TrySetAlwaysOnTop(nint h, bool onTop);
		bool TryMoveResize(nint h, Rectangle bounds, bool setPos, bool setSize);
		bool TrySetState(nint h, FormWindowState state);
		bool TrySetStyle(nint h, long style, long exStyle);
		bool TrySetTransparency(nint h, object alpha);
		bool TryActivate(nint h);
		bool TrySetZOrder(nint h, ZOrder z);
		bool TryClose(nint h);
		bool TryKill(nint h);
		bool TryHide(nint h);
		bool TryShow(nint h);
		bool TryRedraw(nint h);
		bool TryClick(nint h, Point at, uint button, int count);
		bool TrySetTitle(nint h, string title);
		bool TrySetVisible(nint h, bool visible);
		bool TrySetEnabled(nint h, bool enabled);
		bool TrySetTransparentColor(nint h, object color);
	}

	/// <summary>The single resolved foreign-window service, exposing both the read (<see cref="IWindowQuery"/>)
	/// and mutate (<see cref="IWindowControl"/>) surfaces — <c>Platform.Window</c>.</summary>
	internal interface IWindow : IWindowQuery, IWindowControl { }

	/// <summary>OUR OWN windows, via the toolkit (Eto/WinForms). Own-ness is a backend-implemented predicate
	/// (<see cref="IsOwn"/>): X11/Windows = allGuiHwnds membership, macOS = OwnerPid==ProcessId, Wayland =
	/// PID-anchored correlation. The toolkit base reports success; the Wayland override returns false for the
	/// verbs the compositor must own (driving WindowInfo's fallback to the foreign path).</summary>
	internal interface IOwnWindow
	{
		bool IsOwn(nint h);
		bool TryResolve(nint h, out IOwnWindowTarget target);
		bool TrySetAlwaysOnTop(IOwnWindowTarget t, bool onTop);
		bool TryGetAlwaysOnTop(IOwnWindowTarget t, out bool onTop);
		bool TrySetPosition(IOwnWindowTarget t, Point p);
		bool TryGetPosition(IOwnWindowTarget t, out Point p);
		bool TrySetState(IOwnWindowTarget t, FormWindowState state);
		bool TryGetState(IOwnWindowTarget t, out FormWindowState state);
		bool TrySetNoBorder(IOwnWindowTarget t, bool noBorder);
		bool TrySetTransparency(IOwnWindowTarget t, object alpha);
		bool TryGetTransparency(IOwnWindowTarget t, out object alpha);
		bool TrySetClickThrough(IOwnWindowTarget t, bool on);
		bool TrySetAppId(IOwnWindowTarget t, string appId);
	}

	/// <summary>Cursor STATE: position (MouseGetPos), shape (A_Cursor), and the clip-warp positioning the inputd
	/// cursor-clip enforcement needs. NOT mouse-event injection — synthesized mouse input (MouseMove/Click/Send)
	/// is produced by the input/sender subsystem, unified with keyboard, not here.</summary>
	internal interface IMouse
	{
		bool TryGetCursorPos(out int x, out int y);
		string GetCursorShape();
		void SetCursorShape(string ahkName);

		/// <summary>Whether this service can BOTH query the cursor position AND move it — the exact capability
		/// the inputd cursor-clip enforcement needs. Resolves the X11-vs-Wayland question once so callers
		/// (e.g. the hook thread's <c>CanClipCursor</c>) don't re-derive the session shape.</summary>
		bool SupportsCursorClip { get; }

		/// <summary>Warp the cursor to an absolute screen position (the clip-correction counterpart to
		/// <see cref="TryGetCursorPos"/>): X11 <c>XWarpPointer</c> / the Wayland compositor backend.</summary>
		bool TryMoveAbsolute(int x, int y);
	}

	/// <summary>Monitors, work area, screen/window capture.</summary>
	internal interface IScreen
	{
		/// <summary>Usable work area. <paramref name="excludesPanels"/> reports whether panels/struts were
		/// actually subtracted (Keyview derives a titlebar-margin decision from this).</summary>
		bool TryGetWorkArea(int monitorIndex, out Rectangle area, out bool excludesPanels);
		bool TryGetPrimaryBounds(out Rectangle bounds);
		bool TryCaptureRegion(int x, int y, int width, int height, out Bitmap bmp, out double scaleX, out double scaleY);
		bool TryCaptureWindow(nint h, bool includeDecoration, out Bitmap bmp, out double scale);
		bool RequiresAuthorization { get; }

		/// <summary>Gate a capture against the compositor's authorization (KWin/GNOME keysharp-screencap
		/// handshake); returns NotApplicable where capture needs no separate grant. Resolved per-compositor, so
		/// no <c>is …Backend</c> test at the call site.</summary>
		Os.PermissionResult RequestCaptureAuthorization(string operation, bool prompt);
	}

	/// <summary>Click-through outline overlays (highlight) + tooltips. The backing (Eto / wlr layer-shell /
	/// compositor) is decided lazily-with-retry on first Show, not at resolution time.</summary>
	internal interface IOverlay
	{
		OverlayKind PreferredKind { get; }

		/// <summary>Show/update the click-through outline overlay for <paramref name="id"/> using whichever
		/// toolkit-free backing applies (wlr layer-shell surface or compositor-drawn overlay) — the service owns
		/// both. Returns false when <see cref="PreferredKind"/> is <see cref="OverlayKind.Eto"/> (X11/Windows/
		/// macOS), so the caller renders the overlay itself with the toolkit.</summary>
		bool TryShowHighlight(uint id, int x, int y, int width, int height, string colorRRGGBB, int thickness);

		/// <summary>Hide and free the highlight for <paramref name="id"/> (idempotent), whichever backing it used.</summary>
		bool TryHideHighlight(uint id);

		/// <summary>Whether a click-through layer-shell text overlay (Wayland tooltip) is usable — i.e. the
		/// zwlr_layer_shell + Cairo/Pango text stack is available. The probe lives here so the ToolTip builtin
		/// never reaches into <c>WaylandLayerShellClient</c>/<c>CairoText</c> itself.</summary>
		bool SupportsTooltip { get; }

		/// <summary>Show/update the layer-shell tooltip for <paramref name="slot"/> at screen (x,y). The
		/// per-slot surface lifecycle is owned by the service. Returns false (so the caller falls back to the
		/// WinForms tooltip) when unsupported or on a runtime failure.</summary>
		bool TryShowTooltip(int slot, string text, int x, int y);

		/// <summary>Hide and dispose the layer-shell tooltip for <paramref name="slot"/> (idempotent).</summary>
		bool TryHideTooltip(int slot);
	}

	/// <summary>Window lifecycle/state events. Owns the per-platform window-event backend selection and
	/// exposes the chosen one.</summary>
	internal interface IWindowEvents
	{
		Keysharp.Internals.Window.IWindowEventBackend Backend { get; }
	}

	/// <summary>Session control (logout/shutdown/reboot), DE-keyed on Linux. <paramref name="flags"/> follows
	/// the AHK Shutdown codes (1=shutdown, 2=reboot, 4=force, 8=power-down, 0=logoff).</summary>
	internal interface ISession
	{
		bool ExitProgram(uint flags, uint reason);
	}

	/// <summary>Global hotkey registration + hotkey-message posting. MUST execute on the grab-owning hook
	/// thread (X11 XGrabKey is bound to the [ThreadStatic] connection that established it).</summary>
	internal interface IHotkeys
	{
		bool Register(nint hWnd, uint id, KeyModifiers modifiers, uint vk);
		bool Unregister(nint hWnd, uint id);
		bool PostHotkeyMessage(nint hWnd, uint wParam, uint lParam);
	}

	/// <summary>Platform-neutral view of the keyboard/mouse injection transport. The fallback latch itself lives
	/// in <c>KeysharpInputdManager</c> and the senders live on the hook thread; this exposes the transport in a
	/// platform-neutral way so consumers (e.g. <c>KeyCodes.VkSc</c> picking the evdev-vs-XKeycode scancode space)
	/// don't reach into the Linux inputd internals.</summary>
	internal interface IInput
	{
		/// <summary>The live injection transport — read by the scancode mapper to choose evdev (inputd) vs
		/// XKeycode (legacy X11) tables. Fixed on macOS/Windows.</summary>
		InputTransport ActiveTransport { get; }

		/// <summary>The intended sticky one-way inputd → legacy-X11 fallback transition. (Today the latch is
		/// flipped directly via <c>KeysharpInputdManager.ActivateLegacyX11Fallback</c>; this is the platform-neutral
		/// surface for it.)</summary>
		void Demote();
	}
}
