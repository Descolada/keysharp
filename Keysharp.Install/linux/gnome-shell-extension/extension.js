// GNOME Shell extension for Keysharp integration.
// Exposes window management and mouse simulation via D-Bus so that
// Keysharp can operate on GNOME Wayland where foreign-client access
// to compositor state is normally forbidden.
//
// Requires GNOME Shell 45+ (ESM extension format).
// D-Bus service name : io.github.keysharp.GnomeShell
// Object path        : /io/github/keysharp/GnomeShell
// Interface          : io.github.keysharp.GnomeShell1

import GLib from 'gi://GLib';
import Gio from 'gi://Gio';
import Meta from 'gi://Meta';
import Clutter from 'gi://Clutter';
import Cogl from 'gi://Cogl';
import GdkPixbuf from 'gi://GdkPixbuf';
import Shell from 'gi://Shell';
import St from 'gi://St';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';

const SERVICE_NAME = 'io.github.keysharp.GnomeShell';
const OBJECT_PATH  = '/io/github/keysharp/GnomeShell';

const DBUS_IFACE_XML = `
<node>
  <interface name="io.github.keysharp.GnomeShell1">

    <!-- Returns JSON: { ok, windows: [ { id, title, appId, pid,
         frame:{x,y,width,height}, client:{x,y,width,height},
         active, minimized, maximized, visible } ... ] }
         Windows are ordered bottom-to-top (index 0 = lowest z-order). -->
    <method name="GetWindowList">
      <arg type="b" direction="in"  name="includeHidden"/>
      <arg type="s" direction="out" name="json"/>
    </method>

    <!-- Returns JSON: { ok, window: { ... } | null } -->
    <method name="GetActiveWindow">
      <arg type="s" direction="out" name="json"/>
    </method>

    <!-- Global pointer position in compositor coordinates. -->
    <method name="GetCursorPosition">
      <arg type="i" direction="out" name="x"/>
      <arg type="i" direction="out" name="y"/>
    </method>

    <!-- Work area (primary monitor minus panels/struts), in screen coordinates. -->
    <method name="GetWorkArea">
      <arg type="i" direction="out" name="x"/>
      <arg type="i" direction="out" name="y"/>
      <arg type="i" direction="out" name="width"/>
      <arg type="i" direction="out" name="height"/>
    </method>

    <!-- Register this client as an overlay owner. ownerKey = "pid:starttime"; busName = the caller's
         unique D-Bus name. Lets the shell reap the caller's overlays when its process/connection dies. -->
    <method name="RegisterHighlightOwner">
      <arg type="s" direction="in" name="ownerKey"/>
      <arg type="s" direction="in" name="busName"/>
      <arg type="b" direction="out" name="ok"/>
    </method>

    <!-- Window handle is the stable_sequence uint32 of Meta.Window. ok = false when no such window. -->
    <method name="FocusWindow">
      <arg type="t" direction="in" name="handle"/>
      <arg type="b" direction="out" name="ok"/>
    </method>

    <!-- Raise the window to the top of the stack. -->
    <method name="RaiseWindow">
      <arg type="t" direction="in" name="handle"/>
      <arg type="b" direction="out" name="ok"/>
    </method>

    <!-- Lower the window to the bottom of the stack. -->
    <method name="LowerWindow">
      <arg type="t" direction="in" name="handle"/>
      <arg type="b" direction="out" name="ok"/>
    </method>

    <!-- Pass x = INT32_MIN (-2147483648) to leave position unchanged;
         width/height <= 0 to leave size unchanged. -->
    <method name="MoveResizeWindow">
      <arg type="t" direction="in" name="handle"/>
      <arg type="i" direction="in" name="x"/>
      <arg type="i" direction="in" name="y"/>
      <arg type="i" direction="in" name="width"/>
      <arg type="i" direction="in" name="height"/>
      <arg type="b" direction="out" name="ok"/>
    </method>

    <!-- state: 0 = normal, 1 = minimized, 2 = maximized. -->
    <method name="SetWindowState">
      <arg type="t" direction="in" name="handle"/>
      <arg type="i" direction="in" name="state"/>
      <arg type="b" direction="out" name="ok"/>
    </method>

    <!-- above: true = keep above all others, false = clear keep-above. -->
    <method name="SetWindowAbove">
      <arg type="t" direction="in" name="handle"/>
      <arg type="b" direction="in" name="above"/>
      <arg type="b" direction="out" name="ok"/>
    </method>

    <!-- decorated: true = show the titlebar/frame, false = hide it (the GNOME counterpart of KWin's
         noBorder, used by borderless overlays and WinSetStyle -WS_CAPTION). -->
    <method name="SetWindowDecorated">
      <arg type="t" direction="in" name="handle"/>
      <arg type="b" direction="in" name="decorated"/>
      <arg type="b" direction="out" name="ok"/>
    </method>

    <!-- opacity: 0 (fully transparent) .. 255 (fully opaque). -->
    <method name="SetWindowOpacity">
      <arg type="t" direction="in" name="handle"/>
      <arg type="i" direction="in" name="opacity"/>
      <arg type="b" direction="out" name="ok"/>
    </method>

    <method name="CloseWindow">
      <arg type="t" direction="in" name="handle"/>
      <arg type="b" direction="out" name="ok"/>
    </method>

    <!-- Force-kill the window's client, falling back to a graceful close. -->
    <method name="KillWindow">
      <arg type="t" direction="in" name="handle"/>
      <arg type="b" direction="out" name="ok"/>
    </method>

    <!-- Mouse simulation via Clutter.VirtualInputDevice. ok = false when no virtual pointer exists. -->
    <method name="SendMouseMoveAbsolute">
      <arg type="i" direction="in" name="x"/>
      <arg type="i" direction="in" name="y"/>
      <arg type="b" direction="out" name="ok"/>
    </method>

    <method name="SendMouseMoveRelative">
      <arg type="i" direction="in" name="dx"/>
      <arg type="i" direction="in" name="dy"/>
      <arg type="b" direction="out" name="ok"/>
    </method>

    <!-- button: 1=left, 2=middle, 3=right (X11 convention). -->
    <method name="SendMouseButton">
      <arg type="u" direction="in" name="button"/>
      <arg type="b" direction="in" name="pressed"/>
      <arg type="b" direction="out" name="ok"/>
    </method>

    <!-- delta in 120-unit wheel increments (positive = up/right).
         vertical: true = vertical scroll, false = horizontal. -->
    <method name="SendMouseScroll">
      <arg type="i" direction="in" name="delta"/>
      <arg type="b" direction="in" name="vertical"/>
      <arg type="b" direction="out" name="ok"/>
    </method>

    <!-- Capture a screen region and return raw PNG bytes. No flash, no
         file is written. Runs entirely inside the compositor process.
         Returns an empty array on failure. -->
    <method name="CaptureArea">
      <arg type="i" direction="in"  name="x"/>
      <arg type="i" direction="in"  name="y"/>
      <arg type="i" direction="in"  name="width"/>
      <arg type="i" direction="in"  name="height"/>
      <arg type="ay" direction="out" name="pngData"/>
    </method>

    <!-- Capture a single window's full contents (handle = stable_sequence, as in
         GetWindowList) and return raw PNG bytes. Reads the window actor's own
         backing buffer via meta_window_actor_get_image, so the result is correct
         even when the window is occluded by other windows. No flash, no file is
         left behind. Returns an empty array on failure (e.g. unknown handle or a
         minimized window with no live texture). -->
    <method name="CaptureWindow">
      <arg type="t"  direction="in"  name="handle"/>
      <arg type="ay" direction="out" name="pngData"/>
    </method>

    <!-- Draw (or update) a click-through rectangle-outline overlay on the compositor stage — the
         GNOME counterpart of the wlr-layer-shell highlight Keysharp uses on KWin/wlroots (GNOME has
         no layer-shell, so the overlay has to live inside the shell process). id is a caller-chosen
         token so the same overlay can be moved/resized or hidden later; x/y/width/height are in global
         compositor coordinates; color is an "RRGGBB" hex string; thickness is the outline width in px.
         The overlay is non-reactive and excluded from the shell input region, so it never eats clicks. -->
    <method name="ShowHighlight">
      <arg type="u" direction="in" name="id"/>
      <arg type="s" direction="in" name="ownerKey"/>
      <arg type="s" direction="in" name="busName"/>
      <arg type="i" direction="in" name="x"/>
      <arg type="i" direction="in" name="y"/>
      <arg type="i" direction="in" name="width"/>
      <arg type="i" direction="in" name="height"/>
      <arg type="s" direction="in" name="color"/>
      <arg type="i" direction="in" name="thickness"/>
      <arg type="b" direction="out" name="ok"/>
    </method>

    <!-- Remove the overlay previously created with the given id. Unknown ids are ignored. -->
    <method name="HideHighlight">
      <arg type="u" direction="in" name="id"/>
      <arg type="s" direction="in" name="ownerKey"/>
      <arg type="s" direction="in" name="busName"/>
      <arg type="b" direction="out" name="ok"/>
    </method>

    <method name="ShowImageOverlay">
      <arg type="u" direction="in" name="id"/>
      <arg type="s" direction="in" name="ownerKey"/>
      <arg type="s" direction="in" name="busName"/>
      <arg type="i" direction="in" name="x"/>
      <arg type="i" direction="in" name="y"/>
      <arg type="i" direction="in" name="width"/>
      <arg type="i" direction="in" name="height"/>
      <arg type="ay" direction="in" name="pngData"/>
      <arg type="b" direction="out" name="ok"/>
    </method>

    <method name="MoveImageOverlay">
      <arg type="u" direction="in" name="id"/>
      <arg type="s" direction="in" name="ownerKey"/>
      <arg type="s" direction="in" name="busName"/>
      <arg type="i" direction="in" name="x"/>
      <arg type="i" direction="in" name="y"/>
      <arg type="i" direction="in" name="width"/>
      <arg type="i" direction="in" name="height"/>
      <arg type="b" direction="out" name="ok"/>
    </method>

    <method name="HideImageOverlay">
      <arg type="u" direction="in" name="id"/>
      <arg type="s" direction="in" name="ownerKey"/>
      <arg type="s" direction="in" name="busName"/>
      <arg type="b" direction="out" name="ok"/>
    </method>

    <!-- True when the shell can draw a click-through tooltip on the caller's behalf. -->
    <method name="SupportsTooltip">
      <arg type="b" direction="out" name="ok"/>
    </method>

    <!-- Draw or update a click-through tooltip. slot identifies it per owner; empty text clears it.
         x/y are global compositor coordinates. -->
    <method name="ShowTooltip">
      <arg type="i" direction="in" name="slot"/>
      <arg type="s" direction="in" name="ownerKey"/>
      <arg type="s" direction="in" name="busName"/>
      <arg type="s" direction="in" name="text"/>
      <arg type="i" direction="in" name="x"/>
      <arg type="i" direction="in" name="y"/>
      <arg type="b" direction="out" name="ok"/>
    </method>

    <!-- Remove the tooltip in the given slot for this owner. -->
    <method name="HideTooltip">
      <arg type="i" direction="in" name="slot"/>
      <arg type="s" direction="in" name="ownerKey"/>
      <arg type="s" direction="in" name="busName"/>
      <arg type="b" direction="out" name="ok"/>
    </method>

    <!-- Emitted whenever the focused window changes. Carries the same
         JSON as GetActiveWindow. -->
    <signal name="ActiveWindowChanged">
      <arg type="s" name="json"/>
    </signal>

    <!-- Generic window-event stream consumed by Keysharp's WinEvent.
         type is one of: create, close, active, title, minimize, restore, move.
         json is the affected window's info (the same shape as GetActiveWindow's
         window object); for "close" it carries at least { id }. -->
    <signal name="WindowEvent">
      <arg type="s" name="type"/>
      <arg type="s" name="json"/>
    </signal>

  </interface>
</node>`;

// FormWindowState values (mirrors the C# enum sent over the wire).
const STATE_NORMAL    = 0;
const STATE_MINIMIZED = 1;
const STATE_MAXIMIZED = 2;

// Sentinel used by MoveResizeWindow to mean "don't change this axis".
const INT32_MIN = -2147483648;

// Grace period after a client's D-Bus name drops before its overlays are reaped, so a client that
// merely reconnected (new unique name, then re-registers) does not lose its overlays. See
// _handleOverlayConnectionLost. Mirrors the Cinnamon extension.
const OVERLAY_RECONNECT_GRACE_MS = 2000;

export default class KeysharpExtension {
    constructor() {
        this._dbusImpl     = null;
        this._busNameId    = 0;
        this._vPointer     = null;
        this._focusId      = null;
        this._winCreatedId = null;
        // Overlays are keyed by "<ownerKey>:<id>" (see _overlayKey) so ids never collide across clients.
        // Each entry carries its owner so a dead client's overlays can be reaped:
        //   highlight  entry = { edges:[St.Widget...], ownerKey, ownerPid, ownerStartTime, busName }
        //   image      entry = { actor:Clutter.Actor,  ownerKey, ownerPid, ownerStartTime, busName }
        //   tooltip    entry = { actor:St.Label,       ownerKey, ownerPid, ownerStartTime, busName }
        this._highlights   = new Map();
        this._imageOverlays = new Map();
        this._tooltips     = new Map();
        // Owner-death cleanup: subscription id for NameOwnerChanged, the periodic /proc liveness sweep
        // source id (0 = idle), and per-owner reconnect-grace timers keyed by ownerKey.
        this._overlayNameWatchId = 0;
        this._overlayCleanupId = 0;
        this._overlayReconnectTimers = new Map();
    }

    enable() {
        // Virtual pointer for mouse simulation.
        try {
            const seat = Clutter.get_default_backend().get_default_seat();
            this._vPointer = seat.create_virtual_device(
                Clutter.InputDeviceType.POINTER_DEVICE);
        } catch (e) {
            logError(e, 'Keysharp: could not create virtual pointer device');
        }

        // Export the D-Bus object first, then own the well-known name.
        // NOTE: Gio.DBus.session is a Gio.DBusConnection — it has no own_name()
        // method. The correct API is the free function Gio.bus_own_name().
        this._dbusImpl = Gio.DBusExportedObject.wrapJSObject(DBUS_IFACE_XML, this);
        this._dbusImpl.export(Gio.DBus.session, OBJECT_PATH);

        // Watch for client D-Bus connection names dropping off the bus. When one that owns overlays
        // disappears, _handleNameOwnerChanged arms a short reconnect grace timer and then reaps them.
        this._overlayNameWatchId = Gio.DBus.session.signal_subscribe(
            'org.freedesktop.DBus',
            'org.freedesktop.DBus',
            'NameOwnerChanged',
            '/org/freedesktop/DBus',
            null,
            Gio.DBusSignalFlags.NONE,
            (_conn, _sender, _path, _iface, _signal, parameters) => this._handleNameOwnerChanged(parameters));

        this._busNameId = Gio.bus_own_name(
            Gio.BusType.SESSION,
            SERVICE_NAME,
            Gio.BusNameOwnerFlags.NONE,
            null,   // bus_acquired_closure  (not needed; object is already exported)
            null,   // name_acquired_closure
            null);  // name_lost_closure

        // Emit ActiveWindowChanged (back-compat) and the generic WindowEvent('active')
        // whenever the compositor focus changes.
        this._focusId = global.display.connect('notify::focus-window', () => {
            this._emitActiveWindowChanged();
            const win = global.display.get_focus_window();
            if (win && this._isTrackedWindow(win))
                this._emitWindowEvent('active', win);
        });

        // Emit WindowEvent('create') for newly mapped windows and hook their per-window
        // signals (title/minimize/move/close).
        this._winCreatedId = global.display.connect('window-created', (_disp, win) => {
            if (!win || !this._isTrackedWindow(win))
                return;
            this._hookWindow(win);
            this._emitWindowEvent('create', win);
        });

        // Hook windows that already exist at enable time, without re-emitting create.
        for (const actor of global.get_window_actors()) {
            const win = actor.get_meta_window();
            if (win && this._isTrackedWindow(win))
                this._hookWindow(win);
        }
    }

    disable() {
        // Tear down any overlays still on screen, plus the owner-death cleanup machinery.
        for (const key of [...this._highlights.keys()])
            this._removeHighlightByKey(key);

        for (const key of [...this._imageOverlays.keys()])
            this._removeImageOverlayByKey(key);

        for (const key of [...this._tooltips.keys()])
            this._removeTooltipByKey(key);

        this._stopOverlayCleanupTimer();
        this._cancelAllOverlayReconnectTimers();

        if (this._overlayNameWatchId !== 0) {
            try { Gio.DBus.session.signal_unsubscribe(this._overlayNameWatchId); } catch (_e) {}
            this._overlayNameWatchId = 0;
        }

        if (this._winCreatedId !== null) {
            global.display.disconnect(this._winCreatedId);
            this._winCreatedId = null;
        }

        for (const actor of global.get_window_actors()) {
            const win = actor.get_meta_window();
            if (win)
                this._unhookWindow(win);
        }

        if (this._focusId !== null) {
            global.display.disconnect(this._focusId);
            this._focusId = null;
        }

        if (this._busNameId !== 0) {
            Gio.bus_unown_name(this._busNameId);
            this._busNameId = 0;
        }

        if (this._dbusImpl !== null) {
            this._dbusImpl.unexport();
            this._dbusImpl = null;
        }

        this._vPointer = null;
    }

    // ----------------------------------------------------------------
    // D-Bus method implementations
    // ----------------------------------------------------------------

    GetWindowList(includeHidden) {
        try {
            const actors  = global.get_window_actors();
            const windows = [];

            for (let i = 0; i < actors.length; i++) {
                const win = actors[i].get_meta_window();
                if (!win || !this._isTrackedWindow(win))
                    continue;
                if (!includeHidden && win.minimized)
                    continue;
                windows.push(this._windowInfo(win));
            }

            return JSON.stringify({ok: true, windows});
        } catch (e) {
            return JSON.stringify({ok: false, error: String(e), windows: []});
        }
    }

    GetActiveWindow() {
        try {
            const win = global.display.get_focus_window();
            return JSON.stringify({
                ok: true,
                window: (win && this._isTrackedWindow(win))
                    ? this._windowInfo(win)
                    : null,
            });
        } catch (e) {
            return JSON.stringify({ok: false, window: null});
        }
    }

    // Two out-parameters map to a two-element array in GJS D-Bus dispatch.
    GetCursorPosition() {
        const [x, y] = global.get_pointer();
        return [Math.round(x), Math.round(y)];
    }

    GetWorkArea() {
        // Work area of the primary monitor, with panels/struts already subtracted by Mutter — the
        // panel-excluded usable area a foreign Wayland client cannot compute for itself.
        const idx = Main.layoutManager.primaryIndex;
        const wa = Main.layoutManager.getWorkAreaForMonitor(idx);
        return [Math.round(wa.x), Math.round(wa.y), Math.round(wa.width), Math.round(wa.height)];
    }

    FocusWindow(handle) {
        const win = this._findWindow(handle);
        if (!win) return false;
        if (win.minimized)
            win.unminimize();
        Main.activateWindow(win);
        return true;
    }

    RaiseWindow(handle) {
        const win = this._findWindow(handle);
        if (!win)
            return false;

        try {
            if (typeof win.raise === 'function') {
                win.raise();
                return true;
            }
        } catch (_e) {
        }

        // Mutter versions without a public raise(): activating brings it to the front.
        return this.FocusWindow(handle);
    }

    LowerWindow(handle) {
        const win = this._findWindow(handle);
        if (!win)
            return false;

        try {
            if (typeof win.lower_with_transients === 'function') {
                win.lower_with_transients();
                return true;
            }
        } catch (_e) {
        }

        try {
            if (typeof win.lower === 'function') {
                win.lower();
                return true;
            }
        } catch (_e) {
        }

        return false;
    }

    MoveResizeWindow(handle, x, y, width, height) {
        const win = this._findWindow(handle);
        if (!win) return false;

        // Read the current frame rect so we can substitute unchanged axes.
        const frame = win.get_frame_rect();
        const nx = (x !== INT32_MIN) ? x : frame.x;
        const ny = (y !== INT32_MIN) ? y : frame.y;
        const nw = (width  > 0) ? width  : frame.width;
        const nh = (height > 0) ? height : frame.height;

        // move_resize_frame is the single Mutter call for both move and resize;
        // win.resize() does not exist as a standalone public API in Mutter/GJS.
        win.move_resize_frame(false, nx, ny, nw, nh);
        return true;
    }

    SetWindowState(handle, state) {
        const win = this._findWindow(handle);
        if (!win) return false;

        if (state === STATE_MINIMIZED) {
            win.minimize();
        } else if (state === STATE_MAXIMIZED) {
            win.unminimize();
            win.maximize(Meta.MaximizeFlags.HORIZONTAL | Meta.MaximizeFlags.VERTICAL);
        } else {
            win.unminimize();
            win.unmaximize(Meta.MaximizeFlags.HORIZONTAL | Meta.MaximizeFlags.VERTICAL);
        }
        return true;
    }

    SetWindowAbove(handle, above) {
        const win = this._findWindow(handle);
        if (!win) return false;

        // Mutter exposes make_above()/unmake_above(); guard with is_above() so a redundant
        // call doesn't throw on compositor versions that are strict about state transitions.
        if (above) {
            if (!win.is_above())
                win.make_above();
        } else {
            if (win.is_above())
                win.unmake_above();
        }
        return true;
    }

    CloseWindow(handle) {
        const win = this._findWindow(handle);
        if (!win)
            return false;
        win.delete(global.get_current_time());
        return true;
    }

    KillWindow(handle) {
        const win = this._findWindow(handle);
        if (!win)
            return false;

        try {
            if (typeof win.kill === 'function') {
                win.kill();
                return true;
            }
        } catch (_e) {
        }

        // No kill() on this Mutter version: fall back to a graceful close.
        try {
            win.delete(global.get_current_time());
            return true;
        } catch (_e) {
            return false;
        }
    }

    // Hide/show a window's titlebar — the GNOME counterpart of KWin's noBorder.
    //
    // Mutter does not server-side-decorate Wayland clients (unlike KWin), so a borderless overlay made
    // with GTK decorated=false already has no titlebar here and this is a no-op for it. There is also no
    // public Mutter API to toggle decorations for a Wayland client (its decorations are client-side, drawn
    // by the app itself), so the only case we can act on is an XWayland (X11) window, whose Motif decoration
    // hint Mutter honours. We set that hint via xprop when available; otherwise we return gracefully so the
    // caller treats it as best-effort.
    SetWindowDecorated(handle, decorated) {
        const win = this._findWindow(handle);
        if (!win)
            return false;

        try {
            const isX11 = (typeof win.get_client_type === 'function')
                ? (win.get_client_type() === Meta.WindowClientType.X11)
                : (typeof win.get_xwindow === 'function' && win.get_xwindow() !== 0);

            // Wayland client: decorations are client-side; nothing the compositor can remove. The window
            // exists and there is nothing to do, so this is best-effort success, not a failure.
            if (!isX11 || typeof win.get_xwindow !== 'function')
                return true;

            const xid = win.get_xwindow();
            if (!xid)
                return true;

            // _MOTIF_WM_HINTS: flags=2 (MWM_HINTS_DECORATIONS), decorations field 0 = none / 1 = all.
            const decor = decorated ? 1 : 0;
            GLib.spawn_command_line_async(
                `xprop -id ${xid} -f _MOTIF_WM_HINTS 32c ` +
                `-set _MOTIF_WM_HINTS "2, 0, ${decor}, 0, 0"`);
            return true;
        } catch (e) {
            logError(e, 'Keysharp: SetWindowDecorated failed');
            return false;
        }
    }

    // Set the window's opacity by setting it on its compositor actor. opacity is 0 (transparent)..255 (opaque).
    SetWindowOpacity(handle, opacity) {
        const win = this._findWindow(handle);
        if (!win)
            return false;

        const value = Math.max(0, Math.min(255, Math.round(Number(opacity))));

        try {
            const actor = (typeof win.get_compositor_private === 'function')
                ? win.get_compositor_private()
                : null;

            if (!actor)
                return false;

            if (typeof actor.set_opacity === 'function')
                actor.set_opacity(value);
            else
                actor.opacity = value;

            return true;
        } catch (_e) {
            return false;
        }
    }

    SendMouseMoveAbsolute(x, y) {
        if (!this._vPointer) return false;
        this._vPointer.notify_absolute_motion(GLib.get_monotonic_time(), x, y);
        return true;
    }

    SendMouseMoveRelative(dx, dy) {
        if (!this._vPointer) return false;
        const [cx, cy] = global.get_pointer();
        this._vPointer.notify_absolute_motion(
            GLib.get_monotonic_time(), cx + dx, cy + dy);
        return true;
    }

    SendMouseButton(button, pressed) {
        if (!this._vPointer) return false;
        this._vPointer.notify_button(
            GLib.get_monotonic_time(),
            button,
            pressed ? Clutter.ButtonState.PRESSED : Clutter.ButtonState.RELEASED);
        return true;
    }

    SendMouseScroll(delta, vertical) {
        if (!this._vPointer) return false;

        const notches = Math.max(1, Math.abs(Math.round(delta / 120)));
        let dir;

        if (vertical)
            dir = delta > 0 ? Clutter.ScrollDirection.UP : Clutter.ScrollDirection.DOWN;
        else
            dir = delta > 0 ? Clutter.ScrollDirection.RIGHT : Clutter.ScrollDirection.LEFT;

        const time = GLib.get_monotonic_time();
        for (let i = 0; i < notches; i++)
            this._vPointer.notify_discrete_scroll(time, dir, Clutter.ScrollSource.WHEEL);
        return true;
    }

    // Capture a screen region without any visible effect and return PNG bytes.
    // Runs entirely inside the compositor process via Shell.Screenshot.
    // Trust is enforced by keysharp-screencap's own trust gate before it ever
    // calls this method; the D-Bus session bus already restricts callers to
    // the same user, so no additional per-call gate is needed here.
    // GJS wrapJSObject dispatches methods with out-args by calling the handler
    // with only the in-parameters and using the return value as the response —
    // the invocation is not passed. Return the Uint8Array directly.
    CaptureArea(x, y, width, height) {
        if (width <= 0 || height <= 0)
            return new Uint8Array(0);

        try {
            const screenshot = new Shell.Screenshot();
            const stream = Gio.MemoryOutputStream.new_resizable();
            let captureError = null;
            let done = false;

            screenshot.screenshot_area(x, y, width, height, stream, (_obj, result) => {
                try {
                    screenshot.screenshot_area_finish(result);
                } catch (e) {
                    captureError = e;
                }
                done = true;
            });

            // Pump the default GLib main context until the screenshot callback
            // fires. iteration(true) blocks until at least one source dispatches,
            // so this returns as soon as the compositor delivers the frame.
            const ctx = GLib.MainContext.default();
            while (!done)
                ctx.iteration(true);

            if (captureError) {
                logError(captureError, 'Keysharp: CaptureArea screenshot failed');
                return new Uint8Array(0);
            }

            stream.close(null);
            return new Uint8Array(stream.steal_as_bytes().get_data());
        } catch (e) {
            logError(e, 'Keysharp: CaptureArea failed');
            return new Uint8Array(0);
        }
    }

    // Capture a single window's full contents and return PNG bytes. Unlike CaptureArea (which images
    // the composited screen and so loses anything behind other windows), this reads the window actor's
    // own backing buffer through meta_window_actor_get_image, so an occluded window still captures
    // correctly. A minimized window has no live texture and yields an empty result (the C# caller then
    // falls back to a rectangle grab).
    CaptureWindow(handle) {
        let file = null;
        try {
            const win = this._findWindow(handle);
            if (!win)
                return new Uint8Array(0);

            const actor = win.get_compositor_private();
            if (!actor || typeof actor.get_image !== 'function')
                return new Uint8Array(0);

            // clip = null → the whole actor. Returns a cairo ImageSurface of the window's buffer.
            let surface = null;
            try {
                surface = actor.get_image(null);
            } catch (e) {
                logError(e, 'Keysharp: meta_window_actor_get_image failed');
                return new Uint8Array(0);
            }

            if (!surface)
                return new Uint8Array(0);

            // GJS cairo can write a surface to a PNG *file* but not to a memory stream, so round-trip
            // through a private temp file that we create, read and delete entirely within the shell.
            const stream = Gio.File.new_tmp('keysharp-winshot-XXXXXX.png');
            file = stream[0];
            stream[1].close(null);
            const path = file.get_path();

            surface.flush();
            surface.writeToPNG(path);

            const [ok, bytes] = GLib.file_get_contents(path);
            if (!ok || !bytes || bytes.length === 0)
                return new Uint8Array(0);

            return bytes instanceof Uint8Array ? bytes : new Uint8Array(bytes);
        } catch (e) {
            logError(e, 'Keysharp: CaptureWindow failed');
            return new Uint8Array(0);
        } finally {
            if (file !== null) {
                try { file.delete(null); } catch (_e) {}
            }
        }
    }

    // Register a client as an overlay owner. The client re-registers whenever its D-Bus connection is
    // (re)established; this re-stamps the current busName onto any overlays it already owns and cancels a
    // pending reconnect-grace deletion, so a client that merely reconnected keeps its overlays.
    RegisterHighlightOwner(ownerKey, busName) {
        const owner = this._parseOverlayOwner(ownerKey);
        const bus = String(busName || '');

        this._cancelOverlayReconnectTimer(owner.key);

        for (const entry of this._highlights.values()) {
            if (entry.ownerKey === owner.key) {
                entry.ownerPid = owner.pid;
                entry.ownerStartTime = owner.startTime;
                entry.busName = bus;
            }
        }

        for (const entry of this._imageOverlays.values()) {
            if (entry.ownerKey === owner.key) {
                entry.ownerPid = owner.pid;
                entry.ownerStartTime = owner.startTime;
                entry.busName = bus;
            }
        }

        for (const entry of this._tooltips.values()) {
            if (entry.ownerKey === owner.key) {
                entry.ownerPid = owner.pid;
                entry.ownerStartTime = owner.startTime;
                entry.busName = bus;
            }
        }

        return true;
    }

    // Draw or update a click-through rectangle-outline overlay. GNOME has no wlr-layer-shell, so
    // instead of a layer surface we add four edge actors to the shell's top chrome. Two things make
    // them click-through: reactive:false (they never grab events) and addTopChrome(..., {
    // affectsInputRegion: false }) (they are excluded from the shell's input region, so clicks fall
    // through to the window beneath — the compositor-side equivalent of the layer surface's empty
    // input region on KWin). Four edges (rather than one filled rect) keep the centre visually clear.
    // The overlay is tagged with its owner (see _overlayKey) so it can be reaped if the client dies.
    ShowHighlight(id, ownerKey, busName, x, y, width, height, color, thickness) {
        try {
            const owner = this._parseOverlayOwner(ownerKey);
            const key = this._overlayKey(id, owner.key);
            const bus = String(busName || '');

            this._cancelOverlayReconnectTimer(owner.key);
            this._removeHighlightByKey(key);

            if (width < 1 || height < 1) {
                if (!this._hasAnyOverlays())
                    this._stopOverlayCleanupTimer();
                return true;
            }

            const t   = Math.max(1, thickness);
            const css = `background-color: #${color};`;

            const rects = [
                [x,             y,              width,             t],                          // top
                [x,             y + height - t, width,             t],                          // bottom
                [x,             y + t,          t,                 Math.max(0, height - 2 * t)],// left
                [x + width - t, y + t,          t,                 Math.max(0, height - 2 * t)],// right
            ];

            const edges = [];

            for (const [ex, ey, ew, eh] of rects) {
                if (ew < 1 || eh < 1)
                    continue;

                const a = new St.Widget({ reactive: false, style: css });
                a.set_position(ex, ey);
                a.set_size(ew, eh);
                Main.layoutManager.addTopChrome(a, { affectsInputRegion: false });
                edges.push(a);
            }

            this._highlights.set(key, {
                edges: edges,
                ownerKey: owner.key,
                ownerPid: owner.pid,
                ownerStartTime: owner.startTime,
                busName: bus,
            });
            this._ensureOverlayCleanupTimer();
            return true;
        } catch (e) {
            logError(e, 'Keysharp: ShowHighlight failed');
            return false;
        }
    }

    HideHighlight(id, ownerKey, _busName) {
        const owner = this._parseOverlayOwner(ownerKey);
        this._removeHighlightByKey(this._overlayKey(id, owner.key));

        if (!this._hasOverlaysForOwner(owner.key))
            this._cancelOverlayReconnectTimer(owner.key);

        if (!this._hasAnyOverlays())
            this._stopOverlayCleanupTimer();

        return true;
    }

    ShowImageOverlay(id, ownerKey, busName, x, y, width, height, pngData) {
        try {
            const owner = this._parseOverlayOwner(ownerKey);
            const key = this._overlayKey(id, owner.key);
            const bus = String(busName || '');

            this._cancelOverlayReconnectTimer(owner.key);
            this._removeImageOverlayByKey(key);

            // Empty pixels = a clear request, which the removal above already satisfied - report success.
            if (!pngData || pngData.length === 0 || width < 1 || height < 1) {
                if (!this._hasAnyOverlays())
                    this._stopOverlayCleanupTimer();
                return true;
            }

            const actor = this._createImageActor(pngData, x, y, width, height);
            Main.layoutManager.addTopChrome(actor, { affectsInputRegion: false });
            this._imageOverlays.set(key, {
                actor: actor,
                ownerKey: owner.key,
                ownerPid: owner.pid,
                ownerStartTime: owner.startTime,
                busName: bus,
            });
            this._ensureOverlayCleanupTimer();
            return true;
        } catch (e) {
            logError(e, 'Keysharp: ShowImageOverlay failed');
            return false;
        }
    }

    MoveImageOverlay(id, ownerKey, _busName, x, y, width, height) {
        try {
            const owner = this._parseOverlayOwner(ownerKey);
            const entry = this._imageOverlays.get(this._overlayKey(id, owner.key));

            // No live actor for this id: report failure so the caller re-sends the pixels via ShowImageOverlay.
            if (!entry || !entry.actor)
                return false;

            entry.actor.set_position(x, y);
            entry.actor.set_size(width, height);
            return true;
        } catch (e) {
            logError(e, 'Keysharp: MoveImageOverlay failed');
            return false;
        }
    }

    HideImageOverlay(id, ownerKey, _busName) {
        const owner = this._parseOverlayOwner(ownerKey);
        this._removeImageOverlayByKey(this._overlayKey(id, owner.key));

        if (!this._hasOverlaysForOwner(owner.key))
            this._cancelOverlayReconnectTimer(owner.key);

        if (!this._hasAnyOverlays())
            this._stopOverlayCleanupTimer();

        return true;
    }

    SupportsTooltip() {
        return true;
    }

    // Draw or update a click-through tooltip label. slot identifies it per owner; empty text clears it.
    // Click-through and owner-tagged like the other overlays (see ShowHighlight for the mechanism).
    ShowTooltip(slot, ownerKey, busName, text, x, y) {
        try {
            const owner = this._parseOverlayOwner(ownerKey);
            const key = this._overlayKey(slot, owner.key);
            const bus = String(busName || '');
            const labelText = String(text || '');

            this._cancelOverlayReconnectTimer(owner.key);
            this._removeTooltipByKey(key);

            if (!labelText) {
                if (!this._hasAnyOverlays())
                    this._stopOverlayCleanupTimer();
                return true;
            }

            const actor = new St.Label({
                reactive: false,
                text: labelText,
                style: 'background-color: #ffffe1; color: #000000; border: 1px solid #000000; padding: 6px; font-size: 10pt; font-family: sans-serif;',
            });
            actor.set_position(x, y);
            Main.layoutManager.addTopChrome(actor, { affectsInputRegion: false });

            this._tooltips.set(key, {
                actor: actor,
                ownerKey: owner.key,
                ownerPid: owner.pid,
                ownerStartTime: owner.startTime,
                busName: bus,
            });
            this._ensureOverlayCleanupTimer();
            return true;
        } catch (e) {
            logError(e, 'Keysharp: ShowTooltip failed');
            return false;
        }
    }

    HideTooltip(slot, ownerKey, _busName) {
        const owner = this._parseOverlayOwner(ownerKey);
        this._removeTooltipByKey(this._overlayKey(slot, owner.key));

        if (!this._hasOverlaysForOwner(owner.key))
            this._cancelOverlayReconnectTimer(owner.key);

        if (!this._hasAnyOverlays())
            this._stopOverlayCleanupTimer();

        return true;
    }

    // ----------------------------------------------------------------
    // Private helpers
    // ----------------------------------------------------------------

    _removeHighlightByKey(key) {
        const entry = this._highlights.get(key);
        if (!entry)
            return;

        for (const a of (entry.edges || [])) {
            try { Main.layoutManager.removeChrome(a); } catch (_e) {}
            try { a.destroy(); } catch (_e) {}
        }

        this._highlights.delete(key);
    }

    _removeImageOverlayByKey(key) {
        const entry = this._imageOverlays.get(key);
        if (!entry)
            return;

        try { Main.layoutManager.removeChrome(entry.actor); } catch (_e) {}
        try { entry.actor.destroy(); } catch (_e) {}
        this._imageOverlays.delete(key);
    }

    _removeTooltipByKey(key) {
        const entry = this._tooltips.get(key);
        if (!entry)
            return;

        try { Main.layoutManager.removeChrome(entry.actor); } catch (_e) {}
        try { entry.actor.destroy(); } catch (_e) {}
        this._tooltips.delete(key);
    }

    // ---- overlay ownership: keying + owner parsing -------------------------------

    // Namespaces an overlay id under its owner so two clients using the same id never collide.
    _overlayKey(id, ownerKey) {
        return `${ownerKey}:${id}`;
    }

    // ownerKey is "<pid>:<starttime>" (see WaylandOverlayOwner on the C# side).
    _parseOverlayOwner(ownerKey) {
        const key = String(ownerKey || '');
        const parts = key.split(':');
        const pid = parts.length > 0 ? Number(parts[0]) : 0;
        const startTime = parts.length > 1 ? parts[1] : '';
        return {
            key: key,
            pid: Number.isFinite(pid) ? Math.round(pid) : 0,
            startTime: startTime,
        };
    }

    // True unless the owning process is provably gone. Reads field 22 (starttime) of /proc/<pid>/stat
    // and requires it to match the value captured when the overlay was created, so a reused pid (a
    // different process now holding <pid>) is treated as dead. Fails open on any read/parse error.
    _overlayOwnerAlive(pid, startTime) {
        if (!pid || pid <= 0)
            return true;

        const statPath = `/proc/${pid}/stat`;

        if (!GLib.file_test(statPath, GLib.FileTest.EXISTS))
            return false;

        if (!startTime)
            return true;

        try {
            const [ok, bytes] = GLib.file_get_contents(statPath);
            if (!ok)
                return true;

            const stat = new TextDecoder().decode(bytes);
            const end = stat.lastIndexOf(')');

            if (end < 0 || end + 2 >= stat.length)
                return true;

            const fields = stat.substring(end + 2).trim().split(/\s+/);
            return fields.length > 19 && fields[19] === startTime;
        } catch (_e) {
            return true;
        }
    }

    // ---- overlay ownership: reconnect grace timers -------------------------------

    // Called when a client's D-Bus name vanishes. If the owning process is already dead, reap now;
    // otherwise wait OVERLAY_RECONNECT_GRACE_MS and reap only if it stayed dead or never came back under
    // a new bus name (a reconnecting client re-registers, replacing busName -> _ownerHasReplacementBus).
    _handleOverlayConnectionLost(ownerKey, lostBusName, sampleEntry) {
        if (!this._overlayOwnerAlive(sampleEntry.ownerPid, sampleEntry.ownerStartTime)) {
            this._removeOwnerOverlays(ownerKey);
            return;
        }

        this._cancelOverlayReconnectTimer(ownerKey);
        const timerId = GLib.timeout_add(GLib.PRIORITY_DEFAULT, OVERLAY_RECONNECT_GRACE_MS, () => {
            this._overlayReconnectTimers.delete(ownerKey);

            const entry = this._firstOverlayForOwner(ownerKey);

            if (!entry)
                return GLib.SOURCE_REMOVE;

            if (!this._overlayOwnerAlive(entry.ownerPid, entry.ownerStartTime)
                || !this._ownerHasReplacementBus(ownerKey, lostBusName))
                this._removeOwnerOverlays(ownerKey);

            return GLib.SOURCE_REMOVE;
        });
        this._overlayReconnectTimers.set(ownerKey, timerId);
    }

    _cancelOverlayReconnectTimer(ownerKey) {
        const timerId = this._overlayReconnectTimers.get(ownerKey);

        if (!timerId)
            return;

        try { GLib.source_remove(timerId); } catch (_e) {}
        this._overlayReconnectTimers.delete(ownerKey);
    }

    _cancelAllOverlayReconnectTimers() {
        for (const timerId of this._overlayReconnectTimers.values()) {
            try { GLib.source_remove(timerId); } catch (_e) {}
        }

        this._overlayReconnectTimers.clear();
    }

    _ownerHasReplacementBus(ownerKey, lostBusName) {
        for (const entry of this._highlights.values()) {
            if (entry.ownerKey === ownerKey && entry.busName && entry.busName !== lostBusName)
                return true;
        }

        for (const entry of this._imageOverlays.values()) {
            if (entry.ownerKey === ownerKey && entry.busName && entry.busName !== lostBusName)
                return true;
        }

        for (const entry of this._tooltips.values()) {
            if (entry.ownerKey === ownerKey && entry.busName && entry.busName !== lostBusName)
                return true;
        }

        return false;
    }

    // ---- overlay ownership: periodic /proc liveness sweep ------------------------

    // Safety net for a client that dies without its D-Bus name-drop reaching us: a 2s sweep reaps any
    // overlay whose owning process is gone. Self-terminating once no overlays remain.
    _ensureOverlayCleanupTimer() {
        if (this._overlayCleanupId !== 0)
            return;

        this._overlayCleanupId = GLib.timeout_add_seconds(GLib.PRIORITY_DEFAULT, 2, () => {
            this._cleanupDeadOverlays();

            if (!this._hasAnyOverlays()) {
                this._overlayCleanupId = 0;
                return GLib.SOURCE_REMOVE;
            }

            return GLib.SOURCE_CONTINUE;
        });
    }

    _stopOverlayCleanupTimer() {
        if (this._overlayCleanupId === 0)
            return;

        try { GLib.source_remove(this._overlayCleanupId); } catch (_e) {}
        this._overlayCleanupId = 0;
    }

    _cleanupDeadOverlays() {
        const owners = new Map();

        for (const entry of this._highlights.values()) {
            if (!this._overlayOwnerAlive(entry.ownerPid, entry.ownerStartTime))
                owners.set(entry.ownerKey, true);
        }

        for (const entry of this._imageOverlays.values()) {
            if (!this._overlayOwnerAlive(entry.ownerPid, entry.ownerStartTime))
                owners.set(entry.ownerKey, true);
        }

        for (const entry of this._tooltips.values()) {
            if (!this._overlayOwnerAlive(entry.ownerPid, entry.ownerStartTime))
                owners.set(entry.ownerKey, true);
        }

        for (const ownerKey of owners.keys())
            this._removeOwnerOverlays(ownerKey);
    }

    // ---- overlay ownership: NameOwnerChanged watch -------------------------------

    _handleNameOwnerChanged(parameters) {
        let name, oldOwner, newOwner;

        try {
            [name, oldOwner, newOwner] = parameters.deep_unpack();
        } catch (_e) {
            return;
        }

        // Only react to a name being released (had an owner, now has none).
        if (!name || !oldOwner || newOwner)
            return;

        const owners = new Map();

        for (const entry of this._highlights.values()) {
            if (entry.busName === name && !owners.has(entry.ownerKey))
                owners.set(entry.ownerKey, entry);
        }

        for (const entry of this._imageOverlays.values()) {
            if (entry.busName === name && !owners.has(entry.ownerKey))
                owners.set(entry.ownerKey, entry);
        }

        for (const entry of this._tooltips.values()) {
            if (entry.busName === name && !owners.has(entry.ownerKey))
                owners.set(entry.ownerKey, entry);
        }

        for (const [ownerKey, entry] of owners.entries())
            this._handleOverlayConnectionLost(ownerKey, name, entry);
    }

    // ---- overlay ownership: per-owner queries + removal --------------------------

    _firstOverlayForOwner(ownerKey) {
        for (const entry of this._highlights.values())
            if (entry.ownerKey === ownerKey)
                return entry;

        for (const entry of this._imageOverlays.values())
            if (entry.ownerKey === ownerKey)
                return entry;

        for (const entry of this._tooltips.values())
            if (entry.ownerKey === ownerKey)
                return entry;

        return null;
    }

    _hasOverlaysForOwner(ownerKey) {
        return this._firstOverlayForOwner(ownerKey) !== null;
    }

    _hasAnyOverlays() {
        return this._highlights.size !== 0 || this._imageOverlays.size !== 0 || this._tooltips.size !== 0;
    }

    _removeOwnerOverlays(ownerKey) {
        for (const [key, entry] of [...this._highlights.entries()]) {
            if (entry.ownerKey === ownerKey)
                this._removeHighlightByKey(key);
        }

        for (const [key, entry] of [...this._imageOverlays.entries()]) {
            if (entry.ownerKey === ownerKey)
                this._removeImageOverlayByKey(key);
        }

        for (const [key, entry] of [...this._tooltips.entries()]) {
            if (entry.ownerKey === ownerKey)
                this._removeTooltipByKey(key);
        }

        this._cancelOverlayReconnectTimer(ownerKey);

        if (!this._hasAnyOverlays())
            this._stopOverlayCleanupTimer();
    }

    _createImageActor(pngData, x, y, width, height) {
        const loader = GdkPixbuf.PixbufLoader.new();
        loader.write(pngData);
        loader.close();
        const pixbuf = loader.get_pixbuf();

        if (!pixbuf)
            throw new Error('Could not decode PNG overlay image.');

        const content = new Clutter.Image();
        const pixels = pixbuf.get_pixels();
        const format = pixbuf.get_has_alpha() ? Cogl.PixelFormat.RGBA_8888 : Cogl.PixelFormat.RGB_888;
        content.set_data(pixels, format, pixbuf.get_width(), pixbuf.get_height(), pixbuf.get_rowstride());

        const actor = new Clutter.Actor({ reactive: false });
        actor.set_content(content);
        actor.set_position(x, y);
        actor.set_size(width, height);
        return actor;
    }

    _emitActiveWindowChanged() {
        if (!this._dbusImpl) return;
        try {
            const json = this.GetActiveWindow();
            this._dbusImpl.emit_signal(
                'ActiveWindowChanged',
                new GLib.Variant('(s)', [json]));
        } catch (_e) {
            // best-effort; never throw inside a signal handler
        }
    }

    // Emit a generic WindowEvent for the given window, resolving its full info JSON
    // (falling back to a bare { id } if the window is mid-teardown).
    _emitWindowEvent(type, win) {
        if (!win) return;
        let json;
        try {
            json = JSON.stringify(this._windowInfo(win));
        } catch (_e) {
            try { json = JSON.stringify({id: String(win.get_stable_sequence())}); }
            catch (_e2) { return; }
        }
        this._emitWindowEventRaw(type, json);
    }

    _emitWindowEventRaw(type, json) {
        if (!this._dbusImpl) return;
        try {
            this._dbusImpl.emit_signal(
                'WindowEvent',
                new GLib.Variant('(ss)', [type, json]));
        } catch (_e) {
            // best-effort; never throw inside a signal handler
        }
    }

    // Connect per-window signals once. Handler ids are stashed on the window so they
    // can be disconnected in _unhookWindow / on 'unmanaged'.
    _hookWindow(win) {
        if (!win || win._keysharpHooked)
            return;
        win._keysharpHooked = true;

        const ids = [];
        ids.push(win.connect('notify::title', () => this._emitWindowEvent('title', win)));
        ids.push(win.connect('notify::minimized',
            () => this._emitWindowEvent(win.minimized ? 'minimize' : 'restore', win)));
        ids.push(win.connect('position-changed', () => this._emitWindowEvent('move', win)));
        ids.push(win.connect('size-changed',     () => this._emitWindowEvent('move', win)));
        ids.push(win.connect('unmanaged', () => {
            try {
                this._emitWindowEventRaw('close',
                    JSON.stringify({id: String(win.get_stable_sequence())}));
            } catch (_e) {}
            this._unhookWindow(win);
        }));
        win._keysharpHandlerIds = ids;
    }

    _unhookWindow(win) {
        if (!win || !win._keysharpHooked)
            return;
        for (const id of (win._keysharpHandlerIds || [])) {
            try { win.disconnect(id); } catch (_e) {}
        }
        win._keysharpHandlerIds = null;
        win._keysharpHooked = false;
    }

    _isTrackedWindow(win) {
        switch (win.window_type) {
        case Meta.WindowType.NORMAL:
        case Meta.WindowType.DIALOG:
        case Meta.WindowType.MODAL_DIALOG:
        case Meta.WindowType.UTILITY:
            return true;
        default:
            return false;
        }
    }

    _windowInfo(win) {
        const frame = win.get_frame_rect();

        // get_maximized() was removed in Mutter 50; use the individual boolean
        // GObject properties instead, which are stable across all versions.
        const maximized = !!(win.maximized_horizontally && win.maximized_vertically);

        // Use frame_rect for both frame and client geometry. get_buffer_rect() extends
        // outside the visible window to include compositor shadow/glow effects, so using
        // it as the client origin produces a ~20 px negative offset in coordinate
        // transforms. On GNOME Wayland, apps use client-side decorations and their
        // entire surface is the frame rect, so frame == client for coordinate purposes.
        return {
            id:        String(win.get_stable_sequence()),
            title:     win.get_title()            ?? '',
            appId:     win.get_wm_class()         ?? win.get_wm_class_instance() ?? '',
            pid:       win.get_pid(),
            frame:     {x: frame.x, y: frame.y, width: frame.width, height: frame.height},
            client:    {x: frame.x, y: frame.y, width: frame.width, height: frame.height},
            active:    !!win.appears_focused,
            minimized: !!win.minimized,
            maximized,
            visible:   !win.minimized,
            alwaysOnTop: !!win.is_above(),
            decorated:   !!win.decorated,
        };
    }

    _findWindow(handle) {
        const seq = Number(handle);
        for (const actor of global.get_window_actors()) {
            const win = actor.get_meta_window();
            if (win && win.get_stable_sequence() === seq)
                return win;
        }
        return null;
    }
}
