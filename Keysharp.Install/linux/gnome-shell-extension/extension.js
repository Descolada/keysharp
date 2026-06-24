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
import Shell from 'gi://Shell';
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

    <!-- Window handle is the stable_sequence uint32 of Meta.Window. -->
    <method name="FocusWindow">
      <arg type="t" direction="in" name="handle"/>
    </method>

    <!-- Pass x = INT32_MIN (-2147483648) to leave position unchanged;
         width/height <= 0 to leave size unchanged. -->
    <method name="MoveResizeWindow">
      <arg type="t" direction="in" name="handle"/>
      <arg type="i" direction="in" name="x"/>
      <arg type="i" direction="in" name="y"/>
      <arg type="i" direction="in" name="width"/>
      <arg type="i" direction="in" name="height"/>
    </method>

    <!-- state: 0 = normal, 1 = minimized, 2 = maximized. -->
    <method name="SetWindowState">
      <arg type="t" direction="in" name="handle"/>
      <arg type="i" direction="in" name="state"/>
    </method>

    <!-- above: true = keep above all others, false = clear keep-above. -->
    <method name="SetWindowAbove">
      <arg type="t" direction="in" name="handle"/>
      <arg type="b" direction="in" name="above"/>
    </method>

    <method name="CloseWindow">
      <arg type="t" direction="in" name="handle"/>
    </method>

    <!-- Mouse simulation via Clutter.VirtualInputDevice. -->
    <method name="SendMouseMoveAbsolute">
      <arg type="i" direction="in" name="x"/>
      <arg type="i" direction="in" name="y"/>
    </method>

    <method name="SendMouseMoveRelative">
      <arg type="i" direction="in" name="dx"/>
      <arg type="i" direction="in" name="dy"/>
    </method>

    <!-- button: 1=left, 2=middle, 3=right (X11 convention). -->
    <method name="SendMouseButton">
      <arg type="u" direction="in" name="button"/>
      <arg type="b" direction="in" name="pressed"/>
    </method>

    <!-- delta in 120-unit wheel increments (positive = up/right).
         vertical: true = vertical scroll, false = horizontal. -->
    <method name="SendMouseScroll">
      <arg type="i" direction="in" name="delta"/>
      <arg type="b" direction="in" name="vertical"/>
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

export default class KeysharpExtension {
    constructor() {
        this._dbusImpl     = null;
        this._busNameId    = 0;
        this._vPointer     = null;
        this._focusId      = null;
        this._winCreatedId = null;
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

    FocusWindow(handle) {
        const win = this._findWindow(handle);
        if (!win) return;
        if (win.minimized)
            win.unminimize();
        Main.activateWindow(win);
    }

    MoveResizeWindow(handle, x, y, width, height) {
        const win = this._findWindow(handle);
        if (!win) return;

        // Read the current frame rect so we can substitute unchanged axes.
        const frame = win.get_frame_rect();
        const nx = (x !== INT32_MIN) ? x : frame.x;
        const ny = (y !== INT32_MIN) ? y : frame.y;
        const nw = (width  > 0) ? width  : frame.width;
        const nh = (height > 0) ? height : frame.height;

        // move_resize_frame is the single Mutter call for both move and resize;
        // win.resize() does not exist as a standalone public API in Mutter/GJS.
        win.move_resize_frame(false, nx, ny, nw, nh);
    }

    SetWindowState(handle, state) {
        const win = this._findWindow(handle);
        if (!win) return;

        if (state === STATE_MINIMIZED) {
            win.minimize();
        } else if (state === STATE_MAXIMIZED) {
            win.unminimize();
            win.maximize(Meta.MaximizeFlags.HORIZONTAL | Meta.MaximizeFlags.VERTICAL);
        } else {
            win.unminimize();
            win.unmaximize(Meta.MaximizeFlags.HORIZONTAL | Meta.MaximizeFlags.VERTICAL);
        }
    }

    SetWindowAbove(handle, above) {
        const win = this._findWindow(handle);
        if (!win) return;

        // Mutter exposes make_above()/unmake_above(); guard with is_above() so a redundant
        // call doesn't throw on compositor versions that are strict about state transitions.
        if (above) {
            if (!win.is_above())
                win.make_above();
        } else {
            if (win.is_above())
                win.unmake_above();
        }
    }

    CloseWindow(handle) {
        const win = this._findWindow(handle);
        if (win)
            win.delete(global.get_current_time());
    }

    SendMouseMoveAbsolute(x, y) {
        if (!this._vPointer) return;
        this._vPointer.notify_absolute_motion(GLib.get_monotonic_time(), x, y);
    }

    SendMouseMoveRelative(dx, dy) {
        if (!this._vPointer) return;
        const [cx, cy] = global.get_pointer();
        this._vPointer.notify_absolute_motion(
            GLib.get_monotonic_time(), cx + dx, cy + dy);
    }

    SendMouseButton(button, pressed) {
        if (!this._vPointer) return;
        this._vPointer.notify_button(
            GLib.get_monotonic_time(),
            button,
            pressed ? Clutter.ButtonState.PRESSED : Clutter.ButtonState.RELEASED);
    }

    SendMouseScroll(delta, vertical) {
        if (!this._vPointer) return;

        const notches = Math.max(1, Math.abs(Math.round(delta / 120)));
        let dir;

        if (vertical)
            dir = delta > 0 ? Clutter.ScrollDirection.UP : Clutter.ScrollDirection.DOWN;
        else
            dir = delta > 0 ? Clutter.ScrollDirection.RIGHT : Clutter.ScrollDirection.LEFT;

        const time = GLib.get_monotonic_time();
        for (let i = 0; i < notches; i++)
            this._vPointer.notify_discrete_scroll(time, dir, Clutter.ScrollSource.WHEEL);
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

    // ----------------------------------------------------------------
    // Private helpers
    // ----------------------------------------------------------------

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
