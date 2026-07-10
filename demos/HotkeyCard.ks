#Requires AutoHotkey v2.0

/*
    HotkeyCard — a small, persistent "cheat sheet" overlay shared by the demos in this folder.

    It shows a title and a list of shortcuts as keycap-style pills in the bottom-right corner and stays
    put until you click it to dismiss (the shortcuts are easy to forget, so it does NOT auto-close). Each
    demo #includes this file and calls:

        HotkeyCard.Show("Window Tiler", [
            ["Ctrl+Alt+ Q/W/E", "Snap to the top row"],
            ["Ctrl+Alt+R",      "Restore"] ])

    Demonstrates a reusable Overlay component: Image text/shape drawing, content-fit layout via
    MeasureText, bottom-right placement from the monitor work area, and a HotIf-scoped click-to-dismiss
    (so only clicks on the card are captured — everything else passes through the click-through overlay).
*/

class HotkeyCard {
    static Margin := 22                  ; gap from the screen edges
    static ov := ""
    static rect := {x: 0, y: 0, w: 0, h: 0}
    static shown := false
    static registered := false
    static pending := ""

    static Show(title, lines) {
        this.pending := {title: title, lines: lines}
        ; Build on the main/UI thread once the event loop is up (the GUI toolkit isn't ready during the
        ; top-level auto-execute a demo calls this from). SetTimer callbacks run there.
        SetTimer(() => this.Build(), -60)
    }

    static SetTrayIcon(emoji, size := 64, fontSize := 42) {
        local img := ""

        try {
            local font := this.EmojiFont(fontSize)
            local tw := 0, th := 0
            img := Image.Create(size, size)
            img.MeasureText(emoji, font, &tw, &th)
            img.DrawText(emoji, Round((size - tw) / 2), Round((size - th) / 2), "0xFFFFFFFF", font)

            local hbm := img.ToBitmap()
            if hbm
                TraySetIcon("HBITMAP:" hbm, 0, true)
        } catch {
        } finally {
            if IsObject(img)
                img.Dispose()
        }
    }

    static EmojiFont(size) {
        if DirExist("/System/Library/CoreServices")
            return "Apple Color Emoji " size
        if DirExist("/usr/share/fonts")
            return "Noto Color Emoji " size
        return "Segoe UI Emoji " size
    }

    static Build() {
        local title := this.pending.title, lines := this.pending.lines
        local pad := 16, rowH := 29, pillH := 22, pillPad := 9, colGap := 14
        local titleFont := "Arial 12 bold", keyFont := "Arial 10 bold", descFont := "Arial 10", hintFont := "Arial 9"

        ; Everything below is authored in LOGICAL units. The canvas is a physical-resolution bitmap (see
        ; Image.Create's scale arg = dpi) so it stays crisp, but the overlay's on-screen size and all screen
        ; geometry (placement, click-to-dismiss hit-test) use `geo`, matching the coordinate system the OS
        ; reports: physical pixels on Windows/Linux (geo = dpi), logical points on macOS (geo = 1, since Cocoa
        ; handles HiDPI itself — the overlay is still drawn from the physical-resolution canvas).
        local dpi := A_ScreenScale     ; per-platform DPI scale (Keysharp KS module; the including script imports it)
        local geo := DirExist("/System/Library/CoreServices") ? 1 : dpi

        ; Measure so the card fits its content exactly.
        local m := Image.Create(1, 1)
        local tw := 0, th := 0
        m.MeasureText(title, titleFont, &tw, &th)
        local titleW := tw
        local keyWs := [], descW := 0
        for ln in lines {
            m.MeasureText(ln[1], keyFont, &tw, &th), keyWs.Push(tw)
            m.MeasureText(ln[2], descFont, &tw, &th), descW := Max(descW, tw)
        }
        m.Dispose()

        local pillCol := 0
        for kw in keyWs
            pillCol := Max(pillCol, kw + pillPad * 2)
        local descX := pad + pillCol + colGap
        local w := Max(descX + descW, pad + titleW + 78) + pad
        local titleH := 34, hintH := 22
        local h := pad + titleH + lines.Length * rowH + hintH

        local img := Image.Create(w, h, , dpi)
        img.FillRoundRect(0, 0, w, h, 12, "0xF01C1F28")
        img.DrawRoundRect(1, 1, w - 2, h - 2, 12, "0xFF3C4353", 1.5)
        img.DrawText(title, pad, pad, "0xFF5EC8FF", titleFont)
        img.DrawText("✕ click to close", w - 118, pad + 3, "0xFF7A8698", hintFont)
        img.DrawLine(pad, pad + titleH - 8, w - pad, pad + titleH - 8, "0xFF333A48", 1)

        local y := pad + titleH
        for i, ln in lines {
            local pillW := keyWs[i] + pillPad * 2
            img.FillRoundRect(pad, y, pillW, pillH, 5, "0xFF2C323E")
            img.DrawRoundRect(pad, y, pillW, pillH, 5, "0xFF474F60", 1)
            img.DrawText(ln[1], pad + pillPad, y + 4, "0xFFE7B84B", keyFont)
            img.DrawText(ln[2], descX, y + 4, "0xFFD6DBE4", descFont)
            y += rowH
        }
        img.DrawText("Click this card to dismiss it.", pad, y + 2, "0xFF6E7A8C", hintFont)

        if !IsObject(this.ov)
            this.ov := Overlay(0, 0, , , dpi)   ; scale = dpi: the canvas is physical-resolution, shown crisp at its logical size
        ; The overlay's on-screen rect — used for placement and the click-to-dismiss hit-test — is the logical
        ; size (w/h) scaled by geo, matching the overlay's on-screen footprint in the OS coordinate system.
        local pw := Round(w * geo), ph := Round(h * geo)
        this.ov.SetImage(img)
        img.Dispose()

        local margin := Round(this.Margin * geo)
        MonitorGetWorkArea(MonitorGetPrimary(), &l, &t, &r, &b)
        this.rect := {x: r - pw - margin, y: b - ph - margin, w: pw, h: ph}
        this.ov.X := this.rect.x, this.ov.Y := this.rect.y
        this.ov.Show()
        this.shown := true

        ; Click-to-dismiss: a plain left-click, but only while the cursor is over the card (HotIf), so every
        ; other click passes straight through the click-through overlay. Registered once.
        if !this.registered {
            this.registered := true
            HotIf((*) => HotkeyCard.shown && HotkeyCard.Over())
            Hotkey("LButton", (*) => HotkeyCard.Hide())
            HotIf()
        }
    }

    static Over() {
        CoordMode("Mouse", "Screen")
        MouseGetPos(&mx, &my)
        local rc := this.rect
        return mx >= rc.x && mx < rc.x + rc.w && my >= rc.y && my < rc.y + rc.h
    }

    static Hide(*) {
        if IsObject(this.ov)
            this.ov.Hide()
        ; ov.Hide keeps the overlay's own Visible=true if the compositor couldn't confirm the withdraw, so mirror the
        ; REAL on-screen state into our click-to-dismiss gate: a dismiss the compositor dropped leaves the card
        ; clickable (it retries on the next click) instead of permanently short-circuiting `shown` while it's still up.
        this.shown := IsObject(this.ov) && this.ov.Visible
    }
}
