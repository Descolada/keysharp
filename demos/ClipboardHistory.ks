#Requires AutoHotkey v2.0
#Requires capability InputMonitoring, InputInjection   ; the hotkey keyboard hook + Send (paste); macOS asks for Accessibility on first paste-back
#SingleInstance Force
#import KS { A_ScreenScale }     ; A_ScreenScale is a Keysharp addition (per-platform DPI scale factor), so it lives in the KS module
#include Shell.ks

/*
    ClipboardHistory — remembers the text you copy and lets you pick an earlier clip and paste it, through
    a clean keyboard-driven Overlay picker (no native GUI).

    Usage: copy text as usual, then press Ctrl+Alt+V to open the picker. Then:
        1 – 9        paste that clip instantly
        Up / Down    move the selection (wraps); Enter pastes it
        Esc          close without pasting  (Ctrl+Alt+V also toggles it closed)
    Ctrl+Alt+Shift+Q exits the demo. The chosen clip is pasted back into the window you were in.

    Demonstrates cross-platform Keysharp: clipboard monitoring (OnClipboardChange), reading/writing text
    (A_Clipboard), an Overlay rendered with Image primitives (a nicer, fully-styled list than a native
    control), HotIf-scoped hotkeys that are live only while the picker is open, and Send() to paste.

    Cross-platform notes: A_Clipboard is text-only off Windows (images/files read as ""), so this keeps a
    TEXT history. Paste uses Cmd+V on macOS and Ctrl+V elsewhere. WinActivate (to paste back) needs
    Accessibility permission on macOS; foreign-window focus on Wayland needs a compositor backend.
*/

class ClipboardHistory {
    ; --- config -------------------------------------------------------------
    static Max := 40                 ; how many recent text clips to keep
    static Visible := 9              ; show 9 so every visible row maps to a 1–9 quick-paste key
    static ShowHotkey := "^!v"       ; Ctrl+Alt+V toggles the picker

    ; --- state --------------------------------------------------------------
    static clips := []               ; oldest -> newest
    static selfWrite := ""           ; a value WE put on the clipboard; skip recording it once
    static pasteKeys := "^v"         ; set per-OS in Install()
    static picker := ""              ; the Overlay
    static shown := false
    static items := []               ; clips currently shown (newest-first)
    static idx := 1                  ; selected row (1-based)
    static targetWin := 0            ; window to paste back into
    static clearTip := (*) => ToolTip()

    static Install() {
#if OSX
        this.pasteKeys := "#v"                       ; macOS pastes with Cmd+V
#endif

        Shell.SetTrayIcon("📋")
        OnClipboardChange((dt, *) => this.OnClip(dt))  ; lambda keeps `this`; also keeps the script alive
        this.Remember(A_Clipboard)                     ; seed with whatever's already copied so the first open isn't empty
                                                       ; (Remember ignores blank/unset; non-text reads back as "" and is skipped)
        Hotkey(this.ShowHotkey, (*) => this.Toggle())
        Hotkey("^!+q", (*) => ExitApp())               ; Ctrl+Alt+Shift+Q — the shared demo-quit chord

        ; Navigation keys, live ONLY while the picker is open (HotIf), so they pass through the rest of
        ; the time. Registered once here.
        HotIf((*) => ClipboardHistory.shown)
        Hotkey("*Up", (*) => this.Move(-1))
        Hotkey("*Down", (*) => this.Move(1))
        Hotkey("*Enter", (*) => this.Accept())
        Hotkey("*Escape", (*) => this.Close())
        loop 9
            Hotkey("*" A_Index, this.Pick(A_Index))
        HotIf()

        Shell.Show("Clipboard History", [
            ["Ctrl+Alt+V", "Open / close the picker"],
            ["Ctrl+Alt+Shift+Q", "Exit"] ])
        Shell.SetTrayMenu([ ["Clear history", (*) => this.ClearHistory()] ])
    }

    static ClearHistory() {
        this.clips := []
        if this.shown
            this.Close()
        this.Tip("Clipboard history cleared.")
    }

    static Pick(n) => (*) => this.PickIndex(n)   ; per-digit handler with n captured by value

    ; --- clipboard capture --------------------------------------------------
    ; dataType 1 = text (or files, which read back as ""); 0 = empty; 2 = other. (The registered wrapper lambda
    ; already absorbs OnClipboardChange's extra args, so this handler only needs dataType.)
    static OnClip(dataType) {
        if (dataType != 1)
            return
        text := A_Clipboard
        if (!IsSet(text) || text = "")
            return
        if (text = this.selfWrite) {                 ; our own paste-write; skip it once
            this.selfWrite := ""
            return
        }
        this.Remember(text)
    }

    static Remember(text?) {
        if (!IsSet(text) || text = "")
            return
        removeAt := 0
        for i, c in this.clips                        ; de-dupe: move an existing entry to newest
            if (!removeAt && IsSet(c) && c = text)
                removeAt := i
        if removeAt
            this.clips.RemoveAt(removeAt)
        this.clips.Push(text)
        while (this.clips.Length > this.Max)
            this.clips.RemoveAt(1)
    }

    ; --- picker -------------------------------------------------------------
    static Toggle() => this.shown ? this.Close() : this.Open()

    static Open() {
        this.items := []
        loop this.clips.Length {                      ; newest first, skipping any stale unset/blank entries
            pos := this.clips.Length - A_Index + 1
            if !this.clips.Has(pos)
                continue
            text := this.clips[pos]
            if (!IsSet(text) || text = "")
                continue
            this.items.Push(text)
            if (this.items.Length >= this.Visible)
                break
        }
        if (this.items.Length = 0) {
            this.Tip("Clipboard history is empty — copy some text first.")
            return
        }
        this.targetWin := WinActive("A")             ; remember where to paste back
        this.idx := 1
        this.shown := true
        this.Render()
    }

    static Move(d) {
        if !this.shown
            return
        this.idx := Mod(this.idx - 1 + d + this.items.Length, this.items.Length) + 1   ; wraps
        this.Render()
    }

    static Accept() {
        if this.shown
            this.PickIndex(this.idx)
    }

    static PickIndex(n) {
        if (!this.shown || n < 1 || n > this.items.Length)
            return
        if !this.items.Has(n) {
            this.Close()
            return
        }
        text := this.items[n]
        if (!IsSet(text) || text = "") {
            this.Close()
            return
        }
        this.Close()
        this.selfWrite := text                       ; so OnClip doesn't treat this as a fresh copy
        A_Clipboard := text
        this.Remember(text)                          ; bump it to newest ourselves
        if this.targetWin
            try WinActivate(this.targetWin)
        Sleep 120                                     ; let focus settle before pasting
        Send(this.pasteKeys)
    }

    static Close() {
        this.shown := false
        if IsObject(this.picker)
            this.picker.Hide()
    }

    static Render() {
        local n := this.items.Length
        local pad := 16, rowH := 30, titleH := 34, footH := 22, w := 640
        local h := pad + titleH + n * rowH + footH + pad
        ; Everything below is authored in LOGICAL units: the canvas is a physical-resolution bitmap (Image.Create's
        ; scale arg = dpi) so it stays crisp, and it's centred by its on-screen size (w/h scaled by geo). geo
        ; matches the OS coordinate system — physical px on Windows/Linux (geo = dpi), logical points on macOS
        ; (geo = 1, Cocoa handles HiDPI itself). See Shell/InputHUD for the same pattern.
        local dpi := A_ScreenScale
#if OSX
        local geo := 1
#else
        local geo := dpi
#endif

        local img := Image.Create(w, h, , dpi)
        img.FillRoundRect(0, 0, w, h, 12, "0xF01C1F28")
        img.DrawRoundRect(1, 1, w - 2, h - 2, 12, "0xFF3C4353", 1.5)
        img.DrawText("Clipboard History", pad, pad, "0xFF5EC8FF", "Arial 12 bold")
        img.DrawText("1–9 · ↑↓ + Enter · Esc", w - 196, pad + 3, "0xFF7A8698", "Arial 9")
        img.DrawLine(pad, pad + titleH - 8, w - pad, pad + titleH - 8, "0xFF333A48", 1)

        local y := pad + titleH
        for i, text in this.items {
            local sel := (i = this.idx)
            if sel
                img.FillRoundRect(8, y - 1, w - 16, rowH - 3, 6, "0xFF17A3F0")
            local num := (i <= 9) ? i ".  " : "    "
            img.DrawText(num this.Preview(text, 86), pad + 4, y + 4, sel ? "0xFF07101C" : "0xFFD6DBE4", "Arial 10")
            y += rowH
        }
        img.DrawText("Newest first  ·  showing " n " of " this.clips.Length, pad, y + 2, "0xFF6E7A8C", "Arial 9")

        if !IsObject(this.picker)
            this.picker := Overlay(0, 0, , , dpi)   ; scale = dpi: physical-resolution canvas, shown crisp at its logical size
        local pw := Round(w * geo), ph := Round(h * geo)
        this.picker.SetImage(img)
        img.Dispose()

        MonitorGetWorkArea(MonitorGetPrimary(), &l, &t, &r, &b)
        this.picker.X := (l + r) // 2 - pw // 2, this.picker.Y := (t + b) // 2 - ph // 2
        this.picker.Show()
    }

    ; A single-line, length-capped preview of a clip.
    static Preview(text, cap := 86) {
        s := Trim(RegExReplace(text, "\s+", " "))
        return StrLen(s) > cap ? SubStr(s, 1, cap - 3) "..." : s
    }

    static Tip(msg, ms := 1800) {
        CoordMode("ToolTip", "Screen")
        MonitorGetWorkArea(MonitorGetPrimary(), &l, &t, &r, &b)
        ToolTip(msg, (l + r) // 2 - StrLen(msg) * 3, t + 40)
        SetTimer(this.clearTip, 0)
        if (ms > 0)                                    ; match the sibling demos' Tip(): ms=0 means "leave it up"
            SetTimer(this.clearTip, -ms)
    }
}

ClipboardHistory.Install()
Persistent
