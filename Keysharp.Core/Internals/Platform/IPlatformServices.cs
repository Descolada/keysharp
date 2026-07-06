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

		/// <summary>At-point query returning the built item; null when nothing is there. Backends whose
		/// at-point lookup already parses the full window payload (Wayland) return it directly instead of
		/// making the caller re-fetch the same window by handle.</summary>
		Keysharp.Internals.Window.WindowInfoBase WindowAt(int x, int y);

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

	/// <summary>Foreign window MUTATE surface (by handle).</summary>
	/// <remarks>
	/// <para><c>Try*</c> methods on this surface use <c>false</c> as a capability result: the operation is not
	/// implemented, is not exposed by the current platform/compositor, or no backend-specific fallback exists.</para>
	/// <para>If an implementation actually attempts a supported native operation and that operation fails, it should
	/// preserve the existing native/runtime error behavior instead of converting the failure to <c>false</c>. The
	/// user-facing window built-ins translate unsupported <c>false</c> results into <c>OSError</c>; internal
	/// best-effort callers may ignore them.</para>
	/// </remarks>
	internal interface IWindowControl
	{
		bool TrySetAlwaysOnTop(nint h, bool onTop);
		bool TryMoveResize(nint h, Rectangle bounds, bool setPos, bool setSize);
		bool TrySetState(nint h, FormWindowState state);
		bool TrySetStyle(nint h, long style);
		bool TrySetExStyle(nint h, long exStyle);
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

		/// <summary>Live PHYSICAL state of a mouse button (VK_LBUTTON/RBUTTON/MBUTTON/XBUTTON1/2), for
		/// GetKeyState(.., "P") when no mouse hook is tracking it — the cross-platform analogue of Win32
		/// GetAsyncKeyState (X11 <c>XQueryPointer</c> mask, macOS <c>CGEventSourceButtonState</c>, Wayland via
		/// the inputd daemon's evdev read). Returns false if this platform cannot answer, so the caller falls
		/// back to the hook-tracked state.</summary>
		bool TryGetPhysicalMouseButtonState(uint vk, out bool down);
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

		/// <summary>Gate a capture against the compositor's authorization (KWin/GNOME keysharp-helper
		/// handshake); returns NotApplicable where capture needs no separate grant. Resolved per-compositor, so
		/// no <c>is …Backend</c> test at the call site.</summary>
		Os.PermissionResult RequestCaptureAuthorization(string operation, bool prompt);
	}

	/// <summary>Click-through image overlays — the single cross-platform overlay primitive. The user-facing
	/// Overlay builtin, and Highlight and ToolTip (on Linux/macOS), all render through this. The backing
	/// (Eto / wlr layer-shell / compositor extension) is decided lazily-with-retry on the first Show, per overlay.</summary>
	internal interface IOverlay
	{
		OverlayKind PreferredKind { get; }

		/// <summary>Whether this platform service can display caller-supplied image overlays.</summary>
		bool SupportsImageOverlay { get; }

		/// <summary>
		/// Create or update a click-through image overlay. OWNERSHIP of <paramref name="image"/> transfers to the
		/// service (the caller hands in a snapshot and must not touch it afterwards — this keeps the hot refresh
		/// path at exactly one bitmap copy). A non-positive width/height uses the image's native size.
		/// </summary>
		bool TryShowImageOverlay(uint id, int x, int y, int width, int height, Bitmap image);

		/// <summary>Move/resize an existing image overlay, reusing its last pixels (repositions in place where the
		/// backing can, else re-applies the stored image at the new geometry).</summary>
		bool TryMoveImageOverlay(uint id, int x, int y, int width, int height);

		/// <summary>Hide and free one image overlay. Idempotent.</summary>
		bool TryHideImageOverlay(uint id);

		/// <summary>Hide and free every image overlay owned by this process.</summary>
		bool TryHideAllImageOverlays();

		/// <summary>Native handle for a toolkit-backed overlay where one exists (Eto/WinForms window or wlr layer
		/// surface), otherwise zero (a compositor-drawn overlay has no client-side window).</summary>
		nint GetImageOverlayHandle(uint id);
	}

	/// <summary>The process clipboard, resolved once like <see cref="IScreen"/>/<see cref="IOverlay"/>. The backend
	/// is chosen at host construction — Windows raw-Win32, a Wayland shell-extension backend (Cinnamon/Muffin, and
	/// any future compositor whose <c>IWaylandBackend</c> exposes clipboard access), or the shared Eto path — so the
	/// clipboard seams (A_Clipboard, ClipboardAll, image clipboard, OnClipboardChange, IsClipboardEmpty) are plain
	/// calls with no per-call <c>is …Backend</c> / <c>if (Cinnamon)</c> test at the call site.</summary>
	internal interface IClipboard
	{
		/// <summary>The clipboard's text (line endings normalized to <c>\n</c>), or "" when there is none.</summary>
		string GetText();

		/// <summary>Set the clipboard to plain text; "" or <c>null</c> clears the clipboard.</summary>
		void SetText(string text);

		/// <summary>Whether the clipboard holds no data in any format.</summary>
		bool IsEmpty { get; }

		/// <summary>The OnClipboardChange event code for the current content: 0 = empty, 1 = text, 2 = other/binary.</summary>
		int ChangeType();

		/// <summary>A private copy of the clipboard's image (the caller owns and disposes it), or <c>null</c> when
		/// the clipboard holds no image.</summary>
		Bitmap GetImage();

		/// <summary>Put an image on the clipboard.</summary>
		void SetImage(Bitmap image);

		/// <summary>Serialize every clipboard format into an opaque blob (the <c>ClipboardAll()</c> save).</summary>
		byte[] CaptureAll();

		/// <summary>Restore a blob produced by <see cref="CaptureAll"/> (the <c>A_Clipboard := ClipboardAll</c> restore).</summary>
		void RestoreAll(Keysharp.Builtins.ClipboardAll clip);

		/// <summary>Subscribe to clipboard-change notifications; <paramref name="onChanged"/> may be invoked on any
		/// thread (the caller marshals to the UI thread). Returns an <see cref="IDisposable"/> to unsubscribe, or
		/// <c>null</c> when this backend has no watchable change source (the caller then simply does not monitor).</summary>
		IDisposable Subscribe(Action onChanged);
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
