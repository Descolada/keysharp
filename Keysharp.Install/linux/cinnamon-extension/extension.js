// Cinnamon extension for Keysharp integration.
// Exposes window management, window events and mouse simulation via D-Bus
// so Keysharp can use compositor-owned state on Cinnamon Wayland.
//
// D-Bus service name : io.github.keysharp.CinnamonShell
// Object path        : /io/github/keysharp/CinnamonShell
// Interface          : io.github.keysharp.CinnamonShell1

const Clutter = imports.gi.Clutter;
const Cogl = imports.gi.Cogl;
const Gio = imports.gi.Gio;
const GdkPixbuf = imports.gi.GdkPixbuf;
const GLib = imports.gi.GLib;
const Meta = imports.gi.Meta;
const St = imports.gi.St;
const Main = imports.ui.main;
const ByteArray = imports.byteArray;

const SERVICE_NAME = 'io.github.keysharp.CinnamonShell';
const OBJECT_PATH = '/io/github/keysharp/CinnamonShell';

const DBUS_IFACE_XML =
`<node>
  <interface name="io.github.keysharp.CinnamonShell1">
    <method name="GetWindowList">
      <arg type="b" direction="in" name="includeHidden"/>
      <arg type="s" direction="out" name="json"/>
    </method>

    <method name="GetActiveWindow">
      <arg type="s" direction="out" name="json"/>
    </method>

    <method name="GetCursorPosition">
      <arg type="i" direction="out" name="x"/>
      <arg type="i" direction="out" name="y"/>
    </method>

    <method name="GetWorkArea">
      <arg type="i" direction="out" name="x"/>
      <arg type="i" direction="out" name="y"/>
      <arg type="i" direction="out" name="width"/>
      <arg type="i" direction="out" name="height"/>
    </method>

    <method name="FocusWindow">
      <arg type="t" direction="in" name="handle"/>
      <arg type="b" direction="out" name="ok"/>
    </method>

    <method name="RaiseWindow">
      <arg type="t" direction="in" name="handle"/>
      <arg type="b" direction="out" name="ok"/>
    </method>

    <method name="LowerWindow">
      <arg type="t" direction="in" name="handle"/>
      <arg type="b" direction="out" name="ok"/>
    </method>

    <method name="MoveResizeWindow">
      <arg type="t" direction="in" name="handle"/>
      <arg type="i" direction="in" name="x"/>
      <arg type="i" direction="in" name="y"/>
      <arg type="i" direction="in" name="width"/>
      <arg type="i" direction="in" name="height"/>
      <arg type="b" direction="out" name="ok"/>
    </method>

    <method name="SetWindowState">
      <arg type="t" direction="in" name="handle"/>
      <arg type="i" direction="in" name="state"/>
      <arg type="b" direction="out" name="ok"/>
    </method>

    <method name="SetWindowAbove">
      <arg type="t" direction="in" name="handle"/>
      <arg type="b" direction="in" name="above"/>
      <arg type="b" direction="out" name="ok"/>
    </method>

    <method name="SetWindowDecorated">
      <arg type="t" direction="in" name="handle"/>
      <arg type="b" direction="in" name="decorated"/>
      <arg type="b" direction="out" name="ok"/>
    </method>

    <method name="SetWindowOpacity">
      <arg type="t" direction="in" name="handle"/>
      <arg type="i" direction="in" name="opacity"/>
      <arg type="b" direction="out" name="ok"/>
    </method>

    <method name="CloseWindow">
      <arg type="t" direction="in" name="handle"/>
      <arg type="b" direction="out" name="ok"/>
    </method>

    <method name="KillWindow">
      <arg type="t" direction="in" name="handle"/>
      <arg type="b" direction="out" name="ok"/>
    </method>

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

    <method name="SendMouseButton">
      <arg type="u" direction="in" name="button"/>
      <arg type="b" direction="in" name="pressed"/>
      <arg type="b" direction="out" name="ok"/>
    </method>

    <method name="SendMouseScroll">
      <arg type="i" direction="in" name="delta"/>
      <arg type="b" direction="in" name="vertical"/>
      <arg type="b" direction="out" name="ok"/>
    </method>

    <method name="RegisterHighlightOwner">
      <arg type="s" direction="in" name="ownerKey"/>
      <arg type="s" direction="in" name="busName"/>
      <arg type="b" direction="out" name="ok"/>
    </method>

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

    <method name="SupportsTooltip">
      <arg type="b" direction="out" name="ok"/>
    </method>

    <method name="ShowTooltip">
      <arg type="i" direction="in" name="slot"/>
      <arg type="s" direction="in" name="ownerKey"/>
      <arg type="s" direction="in" name="busName"/>
      <arg type="s" direction="in" name="text"/>
      <arg type="i" direction="in" name="x"/>
      <arg type="i" direction="in" name="y"/>
      <arg type="b" direction="out" name="ok"/>
    </method>

    <method name="HideTooltip">
      <arg type="i" direction="in" name="slot"/>
      <arg type="s" direction="in" name="ownerKey"/>
      <arg type="s" direction="in" name="busName"/>
      <arg type="b" direction="out" name="ok"/>
    </method>

    <signal name="ActiveWindowChanged">
      <arg type="s" name="json"/>
    </signal>

    <signal name="WindowEvent">
      <arg type="s" name="type"/>
      <arg type="s" name="json"/>
    </signal>
  </interface>
</node>`;

const STATE_MINIMIZED = 1;
const STATE_MAXIMIZED = 2;
const INT32_MIN = -2147483648;
const OVERLAY_RECONNECT_GRACE_MS = 2000;

let extension = null;

function init(_metadata) {
}

function enable() {
    extension = new KeysharpExtension();
    extension.enable();
}

function disable() {
    if (extension !== null) {
        extension.disable();
        extension = null;
    }
}

class KeysharpExtension {
    constructor() {
        this._dbusImpl = null;
        this._busNameId = 0;
        this._vPointer = null;
        this._focusId = null;
        this._winCreatedId = null;
        this._windowSignalIds = new Map();
        this._highlights = new Map();
        this._imageOverlays = new Map();
        this._tooltips = new Map();
        this._overlayCleanupId = 0;
        this._overlayNameWatchId = 0;
        this._overlayReconnectTimers = new Map();
    }

    enable() {
        try {
            const seat = Clutter.get_default_backend().get_default_seat();
            this._vPointer = seat.create_virtual_device(Clutter.InputDeviceType.POINTER_DEVICE);
        } catch (e) {
            global.logError(e, 'Keysharp: could not create Cinnamon virtual pointer device');
        }

        this._dbusImpl = Gio.DBusExportedObject.wrapJSObject(DBUS_IFACE_XML, this);
        this._dbusImpl.export(Gio.DBus.session, OBJECT_PATH);
        this._overlayNameWatchId = Gio.DBus.session.signal_subscribe(
            'org.freedesktop.DBus',
            'org.freedesktop.DBus',
            'NameOwnerChanged',
            '/org/freedesktop/DBus',
            null,
            Gio.DBusSignalFlags.NONE,
            (_connection, _sender, _path, _iface, _signal, parameters) => this._handleNameOwnerChanged(parameters));

        try {
            this._busNameId = Gio.bus_own_name(
                Gio.BusType.SESSION,
                SERVICE_NAME,
                Gio.BusNameOwnerFlags.NONE,
                null,
                null,
                null);
        } catch (e) {
            global.logError(e, 'Keysharp: could not own Cinnamon D-Bus name');
        }

        this._focusId = global.display.connect('notify::focus-window', () => {
            this._emitActiveWindowChanged();
            const win = global.display.get_focus_window();
            if (win && this._isTrackedWindow(win))
                this._emitWindowEvent('active', win);
        });

        this._winCreatedId = global.display.connect('window-created', (_display, win) => {
            if (!win || !this._isTrackedWindow(win))
                return;

            this._hookWindow(win);
            this._emitWindowEvent('create', win);
        });

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

        if (this._focusId !== null) {
            global.display.disconnect(this._focusId);
            this._focusId = null;
        }

        for (const actor of global.get_window_actors()) {
            const win = actor.get_meta_window();
            if (win)
                this._unhookWindow(win);
        }

        for (const id of Array.from(this._highlights.keys()))
            this._removeHighlightByKey(id);

        for (const id of Array.from(this._imageOverlays.keys()))
            this._removeImageOverlayByKey(id);

        for (const id of Array.from(this._tooltips.keys()))
            this._removeTooltipByKey(id);

        this._stopOverlayCleanupTimer();
        this._cancelAllOverlayReconnectTimers();

        if (this._overlayNameWatchId !== 0) {
            try { Gio.DBus.session.signal_unsubscribe(this._overlayNameWatchId); } catch (_e) {}
            this._overlayNameWatchId = 0;
        }

        if (this._busNameId !== 0) {
            try { Gio.bus_unown_name(this._busNameId); } catch (_e) {}
            this._busNameId = 0;
        }

        if (this._dbusImpl !== null) {
            try { this._dbusImpl.unexport(); } catch (_e) {}
            this._dbusImpl = null;
        }

        this._vPointer = null;
    }

    GetWindowList(includeHidden) {
        try {
            const windows = [];

            for (const actor of global.get_window_actors()) {
                const win = actor.get_meta_window();
                if (!win || !this._isTrackedWindow(win))
                    continue;
                if (!includeHidden && win.minimized)
                    continue;

                windows.push(this._windowInfo(win));
            }

            return JSON.stringify({ok: true, windows: windows});
        } catch (e) {
            return JSON.stringify({ok: false, error: String(e), windows: []});
        }
    }

    GetActiveWindow() {
        try {
            const win = global.display.get_focus_window();
            return JSON.stringify({
                ok: true,
                window: (win && this._isTrackedWindow(win)) ? this._windowInfo(win) : null
            });
        } catch (e) {
            return JSON.stringify({ok: false, window: null});
        }
    }

    GetCursorPosition() {
        const point = global.get_pointer();
        return [Math.round(point[0]), Math.round(point[1])];
    }

    GetWorkArea() {
        try {
            const ws = global.workspace_manager.get_active_workspace();
            const monitorIndex = Main.layoutManager.primaryIndex;
            const area = ws.get_work_area_for_monitor(monitorIndex);
            return [
                Math.round(area.x),
                Math.round(area.y),
                Math.round(area.width),
                Math.round(area.height)
            ];
        } catch (e) {
            const monitor = Main.layoutManager.primaryMonitor;
            if (!monitor)
                return [0, 0, 0, 0];
            return [
                Math.round(monitor.x),
                Math.round(monitor.y),
                Math.round(monitor.width),
                Math.round(monitor.height)
            ];
        }
    }

    FocusWindow(handle) {
        const win = this._findWindow(handle);
        if (!win)
            return false;

        if (win.minimized)
            win.unminimize();

        try {
            Main.activateWindow(win);
            return true;
        } catch (_e) {
            try {
                win.activate(global.get_current_time());
                return true;
            } catch (_e2) {
                return false;
            }
        }
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
        if (!win)
            return false;

        const frame = win.get_frame_rect();
        const nx = (x !== INT32_MIN) ? x : frame.x;
        const ny = (y !== INT32_MIN) ? y : frame.y;
        const nw = (width > 0) ? width : frame.width;
        const nh = (height > 0) ? height : frame.height;

        try {
            win.move_resize_frame(false, nx, ny, nw, nh);
            return true;
        } catch (_e) {
            return false;
        }
    }

    SetWindowState(handle, state) {
        const win = this._findWindow(handle);
        if (!win)
            return false;

        try {
            if (state === STATE_MINIMIZED) {
                win.minimize();
            } else if (state === STATE_MAXIMIZED) {
                if (win.minimized)
                    win.unminimize();
                win.maximize(Meta.MaximizeFlags.BOTH);
            } else {
                if (win.minimized)
                    win.unminimize();
                win.unmaximize(Meta.MaximizeFlags.BOTH);
            }
            return true;
        } catch (_e) {
            return false;
        }
    }

    SetWindowAbove(handle, above) {
        const win = this._findWindow(handle);
        if (!win)
            return false;

        try {
            if (above) {
                if (!win.is_above())
                    win.make_above();
            } else if (win.is_above()) {
                win.unmake_above();
            }
            return true;
        } catch (_e) {
            return false;
        }
    }

    SetWindowDecorated(handle, decorated) {
        const win = this._findWindow(handle);
        if (!win)
            return false;

        try {
            win.decorated = decorated;
            return true;
        } catch (_e) {
            return false;
        }
    }

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

    CloseWindow(handle) {
        const win = this._findWindow(handle);
        if (!win)
            return false;

        try {
            win.delete(global.get_current_time());
            return true;
        } catch (_e) {
            return false;
        }
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

        try {
            win.delete(global.get_current_time());
            return true;
        } catch (_e) {
            return false;
        }
    }

    SendMouseMoveAbsolute(x, y) {
        if (!this._vPointer)
            return false;

        try {
            this._vPointer.notify_absolute_motion(GLib.get_monotonic_time(), x, y);
            return true;
        } catch (_e) {
            return false;
        }
    }

    SendMouseMoveRelative(dx, dy) {
        if (!this._vPointer)
            return false;

        try {
            const point = global.get_pointer();
            this._vPointer.notify_absolute_motion(
                GLib.get_monotonic_time(),
                point[0] + dx,
                point[1] + dy);
            return true;
        } catch (_e) {
            return false;
        }
    }

    SendMouseButton(button, pressed) {
        if (!this._vPointer)
            return false;

        try {
            this._vPointer.notify_button(
                GLib.get_monotonic_time(),
                button,
                pressed ? Clutter.ButtonState.PRESSED : Clutter.ButtonState.RELEASED);
            return true;
        } catch (_e) {
            return false;
        }
    }

    SendMouseScroll(delta, vertical) {
        if (!this._vPointer)
            return false;

        try {
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
        } catch (_e) {
            return false;
        }
    }

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

        for (const entry of this._tooltips.values()) {
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

        return true;
    }

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

            const t = Math.max(1, Math.round(Number(thickness)));
            const css = `background-color: #${color};`;
            const rects = [
                [x,             y,              width,             t],
                [x,             y + height - t, width,             t],
                [x,             y + t,          t,                 Math.max(0, height - 2 * t)],
                [x + width - t, y + t,          t,                 Math.max(0, height - 2 * t)],
            ];
            const edges = [];

            for (const [ex, ey, ew, eh] of rects) {
                if (ew < 1 || eh < 1)
                    continue;

                const actor = new St.Widget({ reactive: false, style: css });
                actor.set_position(ex, ey);
                actor.set_size(ew, eh);
                Main.layoutManager.addChrome(actor, {
                    visibleInFullscreen: true,
                    affectsStruts: false,
                    affectsInputRegion: false
                });
                edges.push(actor);
            }

            this._highlights.set(key, {
                edges: edges,
                ownerKey: owner.key,
                ownerPid: owner.pid,
                ownerStartTime: owner.startTime,
                busName: bus
            });
            this._ensureOverlayCleanupTimer();
            return true;
        } catch (e) {
            global.logError(e, 'Keysharp: ShowHighlight failed');
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

            if (!pngData || pngData.length === 0 || width < 1 || height < 1) {
                if (!this._hasAnyOverlays())
                    this._stopOverlayCleanupTimer();
                return true;
            }

            const actor = this._createImageActor(pngData, x, y, width, height);
            Main.layoutManager.addChrome(actor, {
                visibleInFullscreen: true,
                affectsStruts: false,
                affectsInputRegion: false
            });

            this._imageOverlays.set(key, {
                actor: actor,
                ownerKey: owner.key,
                ownerPid: owner.pid,
                ownerStartTime: owner.startTime,
                busName: bus
            });
            this._ensureOverlayCleanupTimer();
            return true;
        } catch (e) {
            global.logError(e, 'Keysharp: ShowImageOverlay failed');
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
            global.logError(e, 'Keysharp: MoveImageOverlay failed');
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
                style: 'background-color: #ffffe1; color: #000000; border: 1px solid #000000; padding: 6px; font-size: 10pt; font-family: sans-serif;'
            });
            actor.set_position(x, y);
            Main.layoutManager.addChrome(actor, {
                visibleInFullscreen: true,
                affectsStruts: false,
                affectsInputRegion: false
            });

            this._tooltips.set(key, {
                actor: actor,
                ownerKey: owner.key,
                ownerPid: owner.pid,
                ownerStartTime: owner.startTime,
                busName: bus
            });
            this._ensureOverlayCleanupTimer();
            return true;
        } catch (e) {
            global.logError(e, 'Keysharp: ShowTooltip failed');
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

    _overlayKey(id, ownerKey) {
        return `${ownerKey}:${id}`;
    }

    _parseOverlayOwner(ownerKey) {
        const key = String(ownerKey || '');
        const parts = key.split(':');
        const pid = parts.length > 0 ? Number(parts[0]) : 0;
        const startTime = parts.length > 1 ? parts[1] : '';
        return {
            key: key,
            pid: Number.isFinite(pid) ? Math.round(pid) : 0,
            startTime: startTime
        };
    }

    _removeHighlightByKey(key) {
        const entry = this._highlights.get(key);
        if (!entry)
            return;

        for (const actor of entry.edges) {
            try { Main.layoutManager.removeChrome(actor); } catch (_e) {}
            try { actor.destroy(); } catch (_e) {}
        }

        this._highlights.delete(key);
    }

    _removeTooltipByKey(key) {
        const entry = this._tooltips.get(key);
        if (!entry)
            return;

        try { Main.layoutManager.removeChrome(entry.actor); } catch (_e) {}
        try { entry.actor.destroy(); } catch (_e) {}
        this._tooltips.delete(key);
    }

    _removeImageOverlayByKey(key) {
        const entry = this._imageOverlays.get(key);
        if (!entry)
            return;

        try { Main.layoutManager.removeChrome(entry.actor); } catch (_e) {}
        try { entry.actor.destroy(); } catch (_e) {}
        this._imageOverlays.delete(key);
    }

    _removeOwnerOverlays(ownerKey) {
        for (const [key, entry] of Array.from(this._highlights.entries())) {
            if (entry.ownerKey === ownerKey)
                this._removeHighlightByKey(key);
        }

        for (const [key, entry] of Array.from(this._imageOverlays.entries())) {
            if (entry.ownerKey === ownerKey)
                this._removeImageOverlayByKey(key);
        }

        for (const [key, entry] of Array.from(this._tooltips.entries())) {
            if (entry.ownerKey === ownerKey)
                this._removeTooltipByKey(key);
        }

        this._cancelOverlayReconnectTimer(ownerKey);

        if (!this._hasAnyOverlays())
            this._stopOverlayCleanupTimer();
    }

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

        for (const entry of this._tooltips.values()) {
            if (!this._overlayOwnerAlive(entry.ownerPid, entry.ownerStartTime))
                owners.set(entry.ownerKey, true);
        }

        for (const entry of this._imageOverlays.values()) {
            if (!this._overlayOwnerAlive(entry.ownerPid, entry.ownerStartTime))
                owners.set(entry.ownerKey, true);
        }

        for (const ownerKey of owners.keys())
            this._removeOwnerOverlays(ownerKey);
    }

    _handleNameOwnerChanged(parameters) {
        let name, oldOwner, newOwner;

        try {
            [name, oldOwner, newOwner] = parameters.deep_unpack();
        } catch (_e) {
            return;
        }

        if (!name || !oldOwner || newOwner)
            return;

        const owners = new Map();

        for (const entry of this._highlights.values()) {
            if (entry.busName === name && !owners.has(entry.ownerKey))
                owners.set(entry.ownerKey, entry);
        }

        for (const entry of this._tooltips.values()) {
            if (entry.busName === name && !owners.has(entry.ownerKey))
                owners.set(entry.ownerKey, entry);
        }

        for (const entry of this._imageOverlays.values()) {
            if (entry.busName === name && !owners.has(entry.ownerKey))
                owners.set(entry.ownerKey, entry);
        }

        for (const [ownerKey, entry] of owners.entries())
            this._handleOverlayConnectionLost(ownerKey, name, entry);
    }

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

    _firstOverlayForOwner(ownerKey) {
        for (const entry of this._highlights.values())
            if (entry.ownerKey === ownerKey)
                return entry;

        for (const entry of this._tooltips.values())
            if (entry.ownerKey === ownerKey)
                return entry;

        for (const entry of this._imageOverlays.values())
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

    _ownerHasReplacementBus(ownerKey, lostBusName) {
        for (const entry of this._highlights.values()) {
            if (entry.ownerKey === ownerKey && entry.busName && entry.busName !== lostBusName)
                return true;
        }

        for (const entry of this._tooltips.values()) {
            if (entry.ownerKey === ownerKey && entry.busName && entry.busName !== lostBusName)
                return true;
        }

        for (const entry of this._imageOverlays.values()) {
            if (entry.ownerKey === ownerKey && entry.busName && entry.busName !== lostBusName)
                return true;
        }

        return false;
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

    _overlayOwnerAlive(pid, startTime) {
        if (!pid || pid <= 0)
            return true;

        const statPath = `/proc/${pid}/stat`;

        if (!GLib.file_test(statPath, GLib.FileTest.EXISTS))
            return false;

        if (!startTime)
            return true;

        try {
            const [, bytes] = GLib.file_get_contents(statPath);
            const stat = ByteArray.toString(bytes);
            const end = stat.lastIndexOf(')');

            if (end < 0 || end + 2 >= stat.length)
                return true;

            const fields = stat.substring(end + 2).trim().split(/\s+/);
            return fields.length > 19 && fields[19] === startTime;
        } catch (_e) {
            return true;
        }
    }

    _emitActiveWindowChanged() {
        if (!this._dbusImpl)
            return;

        try {
            this._dbusImpl.emit_signal(
                'ActiveWindowChanged',
                new GLib.Variant('(s)', [this.GetActiveWindow()]));
        } catch (_e) {
        }
    }

    _emitWindowEvent(type, win) {
        if (!win)
            return;

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
        if (!this._dbusImpl)
            return;

        try {
            this._dbusImpl.emit_signal('WindowEvent', new GLib.Variant('(ss)', [type, json]));
        } catch (_e) {
        }
    }

    _hookWindow(win) {
        if (!win)
            return;

        const seq = win.get_stable_sequence();
        if (this._windowSignalIds.has(seq))
            return;

        const ids = [];
        ids.push(win.connect('notify::title', () => this._emitWindowEvent('title', win)));
        ids.push(win.connect('notify::minimized', () => this._emitWindowEvent(win.minimized ? 'minimize' : 'restore', win)));
        ids.push(win.connect('position-changed', () => this._emitWindowEvent('move', win)));
        ids.push(win.connect('size-changed', () => this._emitWindowEvent('move', win)));
        ids.push(win.connect('unmanaged', () => {
            this._emitWindowEventRaw('close', JSON.stringify({id: String(seq)}));
            this._unhookWindow(win);
        }));

        this._windowSignalIds.set(seq, ids);
    }

    _unhookWindow(win) {
        if (!win)
            return;

        const seq = win.get_stable_sequence();
        const ids = this._windowSignalIds.get(seq);
        if (!ids)
            return;

        for (const id of ids) {
            try { win.disconnect(id); } catch (_e) {}
        }

        this._windowSignalIds.delete(seq);
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
        let workspace = -1;
        let monitor = -1;
        let opacity = 255;

        try {
            const ws = win.get_workspace();
            if (ws)
                workspace = ws.index();
        } catch (_e) {
        }

        try {
            if (typeof win.get_monitor === 'function')
                monitor = win.get_monitor();
        } catch (_e) {
        }

        try {
            const actor = (typeof win.get_compositor_private === 'function')
                ? win.get_compositor_private()
                : null;
            if (actor)
                opacity = (typeof actor.get_opacity === 'function') ? actor.get_opacity() : actor.opacity;
        } catch (_e) {
            opacity = 255;
        }

        return {
            id: String(win.get_stable_sequence()),
            title: win.get_title() || '',
            appId: win.get_wm_class() || win.get_wm_class_instance() || '',
            pid: win.get_pid(),
            workspace: workspace,
            monitor: monitor,
            frame: {x: frame.x, y: frame.y, width: frame.width, height: frame.height},
            client: {x: frame.x, y: frame.y, width: frame.width, height: frame.height},
            active: !!win.appears_focused,
            minimized: !!win.minimized,
            maximized: !!(win.maximized_horizontally && win.maximized_vertically),
            visible: !win.minimized,
            alwaysOnTop: (typeof win.is_above === 'function') ? !!win.is_above() : !!win.above,
            decorated: win.decorated !== false,
            transparency: Math.max(0, Math.min(255, Math.round(Number(opacity))))
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
