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
    static WordOverlays := []
    static Words := []                 ; {x, y, w, h, text} per drawn word overlay, for hover tooltips
    static PreviousCursor := ""
    static PreviousMouseCoordMode := ""
    static Desktop := ""

    ; Live-selection tracking overlays. Rather than one desktop-sized overlay repainted on every mouse move
    ; (which lagged badly — each frame reallocated and re-uploaded a full-screen bitmap), the crosshair is
    ; split into cheap, independently-movable click-through overlays: two 1px full-screen guide lines, a small
    ; reticle at the pointer, and a lazily-sized selection box. Following the cursor is then just a window move.
    static RetHalf := 21               ; half-size of the square reticle overlay (its centre sits on the pointer)
    static ih := ""                    ; InputHook feeding event-driven OnMouseMove (replaces Sleep-poll tracking)
    static GuideV := "", GuideH := ""  ; vertical / horizontal full-screen alignment guides
    static Reticle := ""               ; the crosshair reticle that follows the pointer
    static SelFill := ""               ; translucent fill of the drag box — a tiny tile the overlay stretches to size
    static SelBars := ""               ; the four border edges of the drag box — thin tiles, stretched to size
    static curX := 0, curY := 0        ; latest pointer position reported by OnMouseMove
    static Dragging := false           ; true between button-down and button-up (gates the selection box)
    static selDirty := false           ; a selection repaint is pending (set by OnMouseMove, cleared by the loop)
    static AnchorX := 0, AnchorY := 0  ; where the drag started (screen px)
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
            this.SetupTracking()

            rect := this.SelectRect()
            this.TeardownTracking()
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
            this.TeardownTracking()
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
        ; The guides and reticle follow the pointer on their own via OnMouseMove, so this loop only has to
        ; watch the mouse button. Phase 1: wait for the left button to go down.
        Loop {
            if this.CancelPressed()
                return ""

            if GetKeyState("LButton", "P")
                break

            Sleep(this.PollMs)
        }

        MouseGetPos(&ax, &ay)
        this.AnchorX := ax, this.AnchorY := ay
        this.curX := ax, this.curY := ay
        this.Dragging := true
        this.selDirty := true

        ; Phase 2: wait for release. The selection box is repainted HERE, coalesced: OnMouseMove only flags
        ; the newest position, and this loop repaints it at most once per tick — so a fast drag can't back up
        ; a queue of stale selection-sized repaints (which is what made the box lag behind the cursor).
        Loop {
            if this.CancelPressed()
                return ""

            if this.selDirty {
                this.selDirty := false
                this.UpdateSelection(this.curX, this.curY)
            }

            if !GetKeyState("LButton", "P")
                break

            Sleep(this.PollMs)
        }

        MouseGetPos(&ex, &ey)
        this.Dragging := false
        return this.NormalizeRect(this.AnchorX, this.AnchorY, ex, ey)
    }

    static CancelPressed() => GetKeyState("Esc", "P") || GetKeyState("RButton", "P")

    ; Called by the InputHook the instant the pointer moves: reposition the guides and reticle (a cheap window
    ; move, no repaint) and, while dragging, resize the selection box. Runs on the UI thread — same thread as
    ; the SelectRect loop that pumps it — so touching the overlays here is safe.
    static OnMove(mx, my) {
        this.curX := mx, this.curY := my

        if IsObject(this.GuideV)
            this.GuideV.X := mx
        if IsObject(this.GuideH)
            this.GuideH.Y := my
        if IsObject(this.Reticle)
            this.Reticle.Move(mx - this.RetHalf, my - this.RetHalf)
        ; Don't repaint the selection here: move events can arrive faster than a selection-sized repaint
        ; finishes, so painting per event backs up and the box lags. Just flag it; SelectRect's loop repaints
        ; the newest rect at most once per tick (coalesced), which keeps the box on the cursor.
        if this.Dragging
            this.selDirty := true
    }

    ; Positions the translucent fill and the four border edges to the current drag rectangle. Every piece is a
    ; tiny tile the overlay stretches, so this is just window moves/resizes — no bitmap is built per frame.
    static UpdateSelection(mx, my) {
        local r := this.NormalizeRect(this.AnchorX, this.AnchorY, mx, my)

        if (r.w < 1 || r.h < 1) {
            this.HideSelection()
            return
        }

        local th := 2
        this.SelFill.Show(r.x, r.y, r.w, r.h)
        this.SelBars[1].Show(r.x, r.y, r.w, th)                    ; top
        this.SelBars[2].Show(r.x, r.y + r.h - th, r.w, th)         ; bottom
        this.SelBars[3].Show(r.x, r.y, th, r.h)                    ; left
        this.SelBars[4].Show(r.x + r.w - th, r.y, th, r.h)         ; right
    }

    static HideSelection() {
        if IsObject(this.SelFill)
            this.SelFill.Hide()
        if IsObject(this.SelBars)
            for bar in this.SelBars
                bar.Hide()
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
        local pad := 3, radius := 6, x, y, w, h

        this.ClearWordOverlays(false)

        ; Collect the padded word rectangles and their overall bounding box, so EVERY box can be painted onto
        ; a single click-through overlay. The previous version created one Overlay per word, and each Overlay
        ; is a top-level layered window — creating hundreds of windows (a handle + UpdateLayeredWindow + a
        ; UI-thread hop each) is what made highlighting a big area take seconds. One overlay = one window, one
        ; paint, one upload, regardless of word count.
        local rects := [], minX := 0x7FFFFFFF, minY := 0x7FFFFFFF, maxX := -0x7FFFFFFF, maxY := -0x7FFFFFFF
        for word in words {
            if (Trim(word.Text) = "" || word.w < 1 || word.h < 1)
                continue

            x := Round(word.x) - pad
            y := Round(word.y) - pad
            w := Round(word.w) + pad * 2
            h := Round(word.h) + pad * 2

            rects.Push({x: x, y: y, w: w, h: h})
            this.Words.Push({x: x, y: y, w: w, h: h, text: word.Text})
            minX := Min(minX, x), minY := Min(minY, y)
            maxX := Max(maxX, x + w), maxY := Max(maxY, y + h)
        }

        if (rects.Length = 0)
            return

        ; Draw every box straight onto the overlay's own canvas (no off-screen image = no defensive copy). Ops
        ; before Show don't upload, so it's one window, one paint, one upload for the whole set.
        local bw := maxX - minX, bh := maxY - minY
        local ov := Overlay(minX, minY, bw, bh)
        for r in rects {
            ov.FillRoundRect(r.x - minX, r.y - minY, r.w, r.h, radius, "0x2200A1FF")
            ov.DrawRoundRect(r.x - minX + 1, r.y - minY + 1, Max(1, r.w - 2), Max(1, r.h - 2), radius, "0xB000A1FF", 2)
        }
        ov.Show()
        this.WordOverlays.Push(ov)

        ; Hovering a word box shows its recognized text in a tooltip (the overlay is click-through, so we
        ; hit-test the stored rects against the cursor rather than relying on WinFromPoint).
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

    ; Builds the click-through tracking overlays and starts event-driven cursor tracking. Each piece is its
    ; own overlay so following the pointer is a window move, not a full-screen repaint — which is what made
    ; the old single desktop-sized overlay lag.
    static SetupTracking() {
        local d := this.Desktop
        MouseGetPos(&mx, &my)
        this.curX := mx, this.curY := my
        this.Dragging := false

        ; Faint full-screen guide lines for precise alignment (1px each; repositioned as the cursor moves)...
        this.GuideV := Overlay(mx, d.y, 1, d.h)
        this.GuideV.Clear("0x40FFFFFF")
        this.GuideV.Show()

        this.GuideH := Overlay(d.x, my, d.w, 1)
        this.GuideH.Clear("0x40FFFFFF")
        this.GuideH.Show()

        ; ...a bold crosshair reticle at the pointer (drawn once into a small overlay that just gets moved)...
        local s := this.RetHalf * 2
        this.Reticle := Overlay(mx - this.RetHalf, my - this.RetHalf, s, s)
        this.DrawCrosshair(this.Reticle, this.RetHalf, this.RetHalf)
        this.Reticle.Show()

        ; ...and the selection box: a translucent fill plus four thin border edges. Each is a tiny solid tile the
        ; overlay STRETCHES to size, so growing the box while dragging is a window resize + one display copy —
        ; never a per-frame bitmap rebuilt through the image pipeline.
        this.SelFill := Overlay(0, 0, 4, 4)
        this.SelFill.Clear("0x331A73E8")
        this.SelBars := []
        loop 4 {
            local bar := Overlay(0, 0, 4, 4)
            bar.Clear("0xD81A73E8")
            this.SelBars.Push(bar)
        }

        ; Event-driven tracking: OnMouseMove repositions the overlays the instant the pointer moves, so the
        ; crosshair keeps up with no Sleep/SetTimer polling (the previous poll is what made it lag).
        this.ih := InputHook("V")
        this.ih.OnMouseMove := (h, x, y) => this.OnMove(x, y)
        this.ih.Start()
    }

    static TeardownTracking() {
        this.Dragging := false

        if IsObject(this.ih) {
            try this.ih.Stop()
            this.ih := ""
        }

        local all := [this.GuideV, this.GuideH, this.Reticle, this.SelFill]
        if IsObject(this.SelBars)
            for bar in this.SelBars
                all.Push(bar)
        for ov in all {
            if IsObject(ov)
                try ov.Destroy()
        }

        this.GuideV := "", this.GuideH := "", this.Reticle := "", this.SelFill := "", this.SelBars := ""
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
        this.TeardownTracking()
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

