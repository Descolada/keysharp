#Requires AutoHotkey v2.0
#Requires capability ScreenCapture, InputMonitoring
#SingleInstance Force

#import "Ks" { * }
#include ../Keysharp/Scripts/OCR.ks
#include HotkeyCard.ks

/*
    OCR Snip demo for Keysharp's Image, Overlay and OCR.ks APIs.

    Hotkeys:
        Ctrl+Shift+O         Start a snip.
        Ctrl+Shift+Backspace Clear the current OCR word overlays.
        Ctrl+Shift+Esc       Exit.

    The selected text is copied to the clipboard. Recognized words are boxed with
    transparent rounded overlays so the source screen remains clickable.
*/

class OCRSnip {
    static StartHotkey := "^+o"
    static ClearHotkey := "^+Backspace"
    static ExitHotkey := "^+Esc"

    static MinSize := 6
    static PollMs := 20
    static OcrScale := 2

    static Active := false
    static SelectionOverlay := ""
    static WordOverlays := []
    static Words := []                 ; {x, y, w, h, text} per drawn word overlay, for hover tooltips
    static PreviousCursor := ""
    static PreviousMouseCoordMode := ""
    static Desktop := ""
    ; Stable timer/hotkey callbacks (kept in-class so SetTimer/Hotkey can cancel/toggle the same reference).
    static clearTip := (*) => ToolTip()
    static block := (*) => 0            ; empty body: registering LButton as a hotkey suppresses it during a snip
    static hoverTimer := ""             ; set to ObjBindMethod(this, "HoverCheck") in Install

    static Install() {
        this.hoverTimer := ObjBindMethod(this, "HoverCheck")
        Hotkey(this.StartHotkey, (*) => this.Start())
        Hotkey(this.ClearHotkey, (*) => this.ClearWordOverlays())
        Hotkey(this.ExitHotkey, (*) => this.Exit())
        HotkeyCard.Show("OCR Snip", [
            ["Ctrl+Shift+O", "Start a snip — drag a box to OCR"],
            ["Ctrl+Shift+Backspace", "Clear the word boxes"],
            ["Ctrl+Shift+Esc", "Exit"] ])
    }

    static Start(*) {
        local rect, result

        if this.Active
            return

        this.Active := true

        ; Block the left mouse button (down AND up) from the app for the duration of the snip, so dragging
        ; the selection rectangle doesn't also select text/objects under the click-through overlay. This
        ; installs the mouse hook, which also makes GetKeyState("LButton","P") read the hook's tracked state.
        this.SetButtonBlock(true)

        this.ClearWordOverlays(false)

        this.PreviousCursor := A_Cursor
        this.PreviousMouseCoordMode := A_CoordModeMouse
        CoordMode("Mouse", "Screen")



        try {
            A_Cursor := "Cross"

            this.Desktop := this.GetVirtualDesktop()
            this.SelectionOverlay := Overlay(this.Desktop.x, this.Desktop.y, this.Desktop.w, this.Desktop.h)


            rect := this.SelectRect()
            this.DestroySelectionOverlay()
            this.RestoreCursorAndCoordMode()

            if !IsObject(rect) {
                this.StatusTip("OCR snip cancelled.", 1200)
                return
            }

            if (rect.w < this.MinSize || rect.h < this.MinSize) {
                this.StatusTip("OCR snip ignored: selection was too small.", 1600)
                return
            }

            this.StatusTip("OCR running...", 0)
            result := OCR.FromRect(rect.x, rect.y, rect.w, rect.h, {scale: this.OcrScale})
            A_Clipboard := result.Text
            this.DrawWordOverlays(result.Words)

            this.StatusTip("Copied " result.Words.Length " OCR word(s) to the clipboard.", 2500)
        } catch as err {
            this.DestroySelectionOverlay()
            this.RestoreCursorAndCoordMode()
            this.StatusTip("OCR snip failed: " err.Message, 5000)
        } finally {
            this.SetButtonBlock(false)
            this.Active := false
        }
    }

    ; Toggle a global LButton (down+up) suppressor. Enabled only while a snip is in progress.
    static SetButtonBlock(on) {
        state := on ? "On" : "Off"
        try Hotkey("*LButton", this.block, state)
        try Hotkey("*LButton Up", this.block, state)
    }

    static SelectRect() {
        local sx := "", sy := "", mx := 0, my := 0, lastMx := "", lastMy := ""

        Loop {
            if this.CancelPressed()
                return ""

            MouseGetPos(&mx, &my)
            if (mx != lastMx || my != lastMy) {
                this.RenderSelectionOverlay(mx, my)
                lastMx := mx, lastMy := my
            }

            if GetKeyState("LButton", "P")
                break

            Sleep(this.PollMs)
        }

        sx := mx, sy := my

        Loop {
            if this.CancelPressed()
                return ""

            MouseGetPos(&mx, &my)
            if (mx != lastMx || my != lastMy) {
                this.RenderSelectionOverlay(mx, my, sx, sy)
                lastMx := mx, lastMy := my
            }

            if !GetKeyState("LButton", "P")
                break

            Sleep(this.PollMs)
        }

        return this.NormalizeRect(sx, sy, mx, my)
    }

    static CancelPressed() => GetKeyState("Esc", "P") || GetKeyState("RButton", "P")

    static RenderSelectionOverlay(mx, my, sx := "", sy := "") {
        local d := this.Desktop
        local img := Image.Create(d.w, d.h)
        local cx := mx - d.x, cy := my - d.y, rect, rx, ry, rw, rh

        ; Faint full-screen guide lines for precise alignment...
        img.DrawLine(cx, 0, cx, d.h, "0x40FFFFFF", 1)
        img.DrawLine(0, cy, d.w, cy, "0x40FFFFFF", 1)

        if (sx != "") {
            rect := this.NormalizeRect(sx, sy, mx, my)
            rx := rect.x - d.x, ry := rect.y - d.y, rw := rect.w, rh := rect.h

            if (rw > 0 && rh > 0) {
                img.FillRoundRect(rx, ry, rw, rh, 8, "0x331A73E8")
                img.DrawRoundRect(rx, ry, Max(1, rw - 1), Max(1, rh - 1), 8, "0xD81A73E8", 2)
            }
        }

        ; ...plus a bold crosshair cursor reticle at the pointer, so it's clear you click-and-drag to select.
        this.DrawCrosshair(img, cx, cy)

        this.SelectionOverlay.SetImage(img)
        img.Dispose()
        this.SelectionOverlay.Show(d.x, d.y, d.w, d.h)
    }

    ; Draws a crosshair reticle (a "+" with a centre gap) at (cx, cy). A dark outline pass under a bright
    ; fill pass keeps it clearly visible on any background — the standard region-select cursor look.
    static DrawCrosshair(img, cx, cy) {
        local arm := 17, gap := 6
        for pass in [{col: "0xC0101010", th: 4}, {col: "0xFFF5F5F5", th: 2}] {
            img.DrawLine(cx - arm, cy, cx - gap, cy, pass.col, pass.th)   ; left arm
            img.DrawLine(cx + gap, cy, cx + arm, cy, pass.col, pass.th)   ; right arm
            img.DrawLine(cx, cy - arm, cx, cy - gap, pass.col, pass.th)   ; top arm
            img.DrawLine(cx, cy + gap, cx, cy + arm, pass.col, pass.th)   ; bottom arm
        }
    }

    static DrawWordOverlays(words) {
        local pad := 3, radius := 6, x, y, w, h, ov

        this.ClearWordOverlays(false)

        for word in words {
            if (Trim(word.Text) = "" || word.w < 1 || word.h < 1)
                continue

            x := Round(word.x) - pad
            y := Round(word.y) - pad
            w := Round(word.w) + pad * 2
            h := Round(word.h) + pad * 2

            ov := Overlay(x, y, w, h)
            ov.FillRoundRect(0, 0, w, h, radius, "0x2200A1FF")
            ov.DrawRoundRect(1, 1, Max(1, w - 2), Max(1, h - 2), radius, "0xB000A1FF", 2)
            ov.Show()
            this.WordOverlays.Push(ov)
            this.Words.Push({x: x, y: y, w: w, h: h, text: word.Text})
        }

        ; Hovering a word overlay shows its recognized text in a tooltip (the overlays are click-through,
        ; so we hit-test the stored rects against the cursor rather than relying on WinFromPoint).
        if (this.Words.Length > 0)
            SetTimer(this.hoverTimer, 120)
    }

    static ClearWordOverlays(showTip := true) {
        SetTimer(this.hoverTimer, 0)
        ToolTip(, , , 2)               ; hide the hover tooltip (slot 2)

        for ov in this.WordOverlays {
            try ov.Destroy()
        }
        this.WordOverlays := []
        this.Words := []

        if showTip
            this.StatusTip("OCR word overlays cleared.", 1200)
    }

    static DestroySelectionOverlay() {
        if IsObject(this.SelectionOverlay) {
            try this.SelectionOverlay.Destroy()
            this.SelectionOverlay := ""
        }
    }

    static RestoreCursorAndCoordMode() {
        if (this.PreviousCursor != "")
            try A_Cursor := this.PreviousCursor

        if (this.PreviousMouseCoordMode != "")
            try CoordMode("Mouse", this.PreviousMouseCoordMode)
    }

    static GetVirtualDesktop() {
        local count := MonitorGetCount()
        local left := 0, top := 0, right := A_TotalScreenWidth, bottom := A_TotalScreenHeight
        local l, t, r, b

        if (count > 0) {
            Loop count {
                MonitorGet(A_Index, &l, &t, &r, &b)
                if (A_Index = 1) {
                    left := l, top := t, right := r, bottom := b
                } else {
                    left := Min(left, l)
                    top := Min(top, t)
                    right := Max(right, r)
                    bottom := Max(bottom, b)
                }
            }
        }

        return {x: left, y: top, w: Max(1, right - left), h: Max(1, bottom - top)}
    }

    static NormalizeRect(x1, y1, x2, y2) {
        local x := Min(x1, x2), y := Min(y1, y2)
        return {x: x, y: y, w: Abs(x2 - x1), h: Abs(y2 - y1)}
    }

    static StatusTip(message, ms := 2000) {
        ToolTip(message)
        SetTimer(this.clearTip, 0)
        if (ms > 0)
            SetTimer(this.clearTip, -ms)
    }

    static Exit(*) {
        this.DestroySelectionOverlay()
        this.ClearWordOverlays(false)
        this.RestoreCursorAndCoordMode()
        ExitApp()
    }

    ; Polls the cursor against the stored OCR word rects and shows the recognized text of the word under it
    ; (the overlays are click-through, so we hit-test the stored rects rather than relying on WinFromPoint).
    static HoverCheck() {
        static lastText := ""

        if (!IsObject(this.Words) || this.Words.Length = 0) {
            SetTimer(this.hoverTimer, 0)
            ToolTip(, , , 2)
            lastText := ""
            return
        }

        CoordMode("Mouse", "Screen")
        CoordMode("ToolTip", "Screen")
        MouseGetPos(&mx, &my)

        for word in this.Words {
            if (mx >= word.x && mx < word.x + word.w && my >= word.y && my < word.y + word.h) {
                if (word.text != lastText) {
                    ToolTip(word.text, mx + 14, my + 20, 2)
                    lastText := word.text
                }
                return
            }
        }

        if (lastText != "") {
            ToolTip(, , , 2)
            lastText := ""
        }
    }
}

OCRSnip.Install()
Persistent

