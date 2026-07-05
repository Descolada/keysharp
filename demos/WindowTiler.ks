#Requires AutoHotkey v2.0
#SingleInstance Force
#import KS { Highlight, A_ScreenScale }   ; A_ScreenScale (per-platform DPI scale factor) is used by the included HotkeyCard
#include HotkeyCard.ks

/*
    WindowTiler — snap the active window to halves, quarters, or maximize on its current monitor.

    Demonstrates cross-platform Keysharp: window actions (WinActive / WinGetPos / WinMove / WinRestore /
    WinMaximize), multi-monitor geometry (MonitorGet / MonitorGetWorkArea, which excludes panels/taskbars),
    dynamic Hotkey() registration, and an Overlay (Highlight) + ToolTip for feedback.

    Hotkeys (Ctrl+Alt + a QWE/ASD/ZXC grid that mirrors screen position — no numpad needed):

        Q W E       top-left    top-half     top-right
        A S D   =   left-half   MAXIMIZE     right-half
        Z X C       bottom-left bottom-half  bottom-right

        Ctrl+Alt+F  centre (two-thirds, centred)
        Ctrl+Alt+R  restore

    Edit WindowTiler.Prefix / WindowTiler.Grid below if these clash with your desktop's shortcuts.

    Cross-platform notes: work-area math excludes panels/taskbars everywhere. Moving/resizing a foreign
    window works natively on Windows and X11; on Wayland it needs a compositor backend (KWin / GNOME /
    Cinnamon), and on macOS it needs Accessibility permission. Coordinates are logical (DPI-independent).
*/

class WindowTiler {
    ; --- config -------------------------------------------------------------
    static Prefix := "^!"            ; Ctrl+Alt. Change to "#" (Super/Win/Cmd) etc. if you prefer.
    static Grid := Map(              ; hotkey suffix -> region
        "q", "topleft",  "w", "top",     "e", "topright",
        "a", "left",     "s", "maximize", "d", "right",
        "z", "botleft",  "x", "bottom",  "c", "botright",
        "f", "center",   "r", "restore")

    static hl := ""                  ; reused Highlight for the snap-target flash
    ; Stable timer callbacks (kept in-class so SetTimer can cancel/reschedule the same reference).
    static hideFlash := (*) => IsObject(WindowTiler.hl) && WindowTiler.hl.Hide()
    static clearTip := (*) => ToolTip()

    static Install() {
        SetWinDelay(-1)             ; no post-move delay -> snappy tiling
        for suffix, region in this.Grid
            Hotkey(this.Prefix suffix, this.Handler(region), "On")
        HotkeyCard.Show("Window Tiler", [
            ["Ctrl+Alt+ Q/W/E", "Top-left · top · top-right"],
            ["Ctrl+Alt+ A/S/D", "Left · maximize · right"],
            ["Ctrl+Alt+ Z/X/C", "Bottom-left · bottom · bottom-right"],
            ["Ctrl+Alt+ F / R", "Centre · restore"] ])
    }

    static Handler(region) => (*) => this.Snap(region)

    static Snap(region) {
        local info := this.ActiveWindowArea()
        if !IsObject(info)
            return

        try {
            if (region = "restore") {
                WinRestore(info.id)
                this.Flash({x: info.l, y: info.t, w: 1, h: 1})   ; nothing to outline; just clear
                this.Tip("Restore", 800)
                return
            }
            if (region = "maximize") {
                WinMaximize(info.id)
                this.Flash({x: info.l, y: info.t, w: info.w, h: info.h})
                this.Tip("Maximize", 800)
                return
            }

            local r := this.RegionRect(region, info)
            WinRestore(info.id)                          ; un-maximize so the resize takes effect
            WinMove(r.x, r.y, r.w, r.h, info.id)
            this.Flash(r)
            this.Tip(this.Label(region), 800)
        } catch as e
            this.Tip("Tile failed: " e.Message, 2500)
    }

    ; Active window + the work area of the monitor it mostly sits on.
    static ActiveWindowArea() {
        local id := WinActive("A")
        if !id
            return 0
        WinGetPos(&wx, &wy, &ww, &wh, id)
        local mon := this.MonitorAt(wx + ww // 2, wy + wh // 2)
        MonitorGetWorkArea(mon, &l, &t, &r, &b)
        return {id: id, l: l, t: t, r: r, b: b, w: r - l, h: b - t}
    }

    static MonitorAt(x, y) {
        Loop MonitorGetCount() {
            MonitorGet(A_Index, &ml, &mt, &mr, &mb)
            if (x >= ml && x < mr && y >= mt && y < mb)
                return A_Index
        }
        return MonitorGetPrimary()
    }

    static RegionRect(region, info) {
        local l := info.l, t := info.t, w := info.w, h := info.h
        local hw := w // 2, hh := h // 2
        switch region {
            case "left":     return {x: l,      y: t,      w: hw,     h: h}
            case "right":    return {x: l + hw, y: t,      w: w - hw, h: h}
            case "top":      return {x: l,      y: t,      w: w,      h: hh}
            case "bottom":   return {x: l,      y: t + hh, w: w,      h: h - hh}
            case "topleft":  return {x: l,      y: t,      w: hw,     h: hh}
            case "topright": return {x: l + hw, y: t,      w: w - hw, h: hh}
            case "botleft":  return {x: l,      y: t + hh, w: hw,     h: h - hh}
            case "botright": return {x: l + hw, y: t + hh, w: w - hw, h: h - hh}
            case "center":   return {x: l + w // 6, y: t + h // 6, w: w * 2 // 3, h: h * 2 // 3}
        }
        return {x: l, y: t, w: w, h: h}
    }

    static Labels := Map(
        "left", "Left half",   "right", "Right half", "top", "Top half",    "bottom", "Bottom half",
        "topleft", "Top-left", "topright", "Top-right", "botleft", "Bottom-left", "botright", "Bottom-right",
        "center", "Centre")
    static Label(region) => this.Labels.Has(region) ? this.Labels[region] : region

    ; Briefly outline the snap target so the action is visible.
    static Flash(r) {
        if (r.w < 2 || r.h < 2) {
            if IsObject(this.hl)
                this.hl.Hide()
            return
        }
        if !IsObject(this.hl)
            this.hl := Highlight(r.x, r.y, r.w, r.h, "0x2196F3", 3)
        else
            this.hl.Show(r.x, r.y, r.w, r.h)
        SetTimer(this.hideFlash, -380)
    }

    static Tip(msg, ms := 1500) {
        CoordMode("ToolTip", "Screen")
        MonitorGetWorkArea(MonitorGetPrimary(), &l, &t, &r, &b)
        ToolTip(msg, (l + r) // 2 - StrLen(msg) * 3, t + 30)   ; brief action label, centred near the top
        SetTimer(this.clearTip, 0)
        if (ms > 0)
            SetTimer(this.clearTip, -ms)
    }
}

WindowTiler.Install()
Persistent
