#Requires AutoHotkey v2.0
#SingleInstance Force
#import KS { A_ScreenScale }     ; A_ScreenScale is a Keysharp addition (per-platform DPI scale factor), so it lives in the KS module
#include HotkeyCard.ks

/*
    InputHUD — an on-screen keyboard + mouse HUD whose keys and buttons light up live as you type
    and click (a "keycaster", handy for screencasts, demos and teaching).

        - Two always-on-top overlays: a keyboard and a mouse. Keys/buttons glow while held — blue when you
          physically press them, amber when a script or remap injects them (Send).
        - The layout adapts to the OS: Win/Super/Alt/Menu on Windows & Linux, Command/Option/Control on macOS.
        - Both HUDs are separately draggable: hold Super/Win/Cmd and drag either one to reposition it.
        - Non-intrusive: it never swallows your input — everything you press still reaches your apps.

        Hold Super/Win/Cmd + drag  ...  move a HUD
        Ctrl+Alt+Shift+Q           ...  quit

    Demonstrates cross-platform Keysharp: a single InputHook ("V H") capturing BOTH keyboard and mouse
    (OnKeyDown/OnKeyUp + OnMouseDown/OnMouseUp, KeyOpt("{All}","N") to notify-without-suppressing; the "H"
    option also surfaces input a hotkey/remap suppresses, so a remap's physical source and the #LButton used
    to drag a HUD still register), telling physical from injected input via A_EventInfo.IsInjected, Overlay
    canvases drawn with Image primitives (FillRoundRect/DrawText) and updated live via SetImage, live
    repositioning through Overlay.X/Y, and a HotIf-scoped hotkey so the drag only captures the click while the
    cursor is over a HUD (so normal clicks pass straight through).

    Note: amber (injected) only lights for keys the hook can observe — on Linux that means the sender used
    SendMode "Event" (the default "Input"/SendInput bypasses the hook); on Windows, cross-process injections.

    Threading note: overlays, Image text and ToolTip all use the GUI toolkit, which isn't ready during
    top-level auto-execute (it runs before the event loop is up). SetTimer callbacks are pseudo-threads
    that run on the main/UI thread once it is, so setup is deferred to one; and because InputHook callbacks
    may run off the main thread, all drawing is funnelled onto a SetTimer tick — handlers only flip a flag.

    Cross-platform notes: on Linux the physical hook is served by the input daemon; the overlays are
    click-through canvases on X11/Wayland/Windows/macOS alike. Key positions assume a US QWERTY layout
    (keys are matched by virtual-key code); non-US layouts still light up, just under the US label.
*/

class InputHUD {
    ; ---- look & feel -------------------------------------------------------
    static U := 30                       ; key unit size in px
    static G := 5                        ; gap between keys
    static Pad := 12                     ; overlay inner padding
    static Font := "Arial 10 bold"
    static Bg      := "0xF01B1E26"       ; panel background (mostly opaque)
    static Border  := "0xFF3A3F4B"
    static KeyFill := "0xFF2C313C"       ; idle key
    static KeyText := "0xFFD7DCE5"
    static LitFill := "0xFF17A3F0"       ; physically-pressed key (bright blue)
    static LitText := "0xFF07101C"
    static LitEdge := "0xFFCDEBFF"
    static SynFill := "0xFFF5A623"       ; script-injected key (amber): Send/remap output, not a physical press
    static SynText := "0xFF1A1206"
    static SynEdge := "0xFFFFE3B0"

    ; ---- state -------------------------------------------------------------
    static DPI := 1                      ; canvas render scale (A_ScreenScale): each overlay bitmap is drawn at physical resolution
    static Geo := 1                      ; on-screen geometry scale: DPI where the OS places in physical px (Win/Linux), 1 where it
                                         ; places in logical points and handles HiDPI itself (macOS) — mirrors the GUI's DpiScale
    static ih := ""
    static kb := ""                      ; {ov, x, y, w, h (logical render size), pw, ph (physical on-screen size), keys[]}
    static ms := ""                      ; {ov, x, y, w, h (logical render size), pw, ph (physical on-screen size)}
    static down := Map()                 ; vk -> "phys" | "synth"  (pressed keys, keyed by origin)
    static mdown := Map()                ; "left"/"right"/"middle"/"x1"/"x2" -> "phys" | "synth"
    static wheelFlash := 0               ; -1 up, +1 down, 0 none
    static dispVk := Map()               ; vks the keyboard actually shows (gates re-render)
    static isMac := false
    static kbDirty := true               ; a render is pending (rendered on the main-thread tick)
    static msDirty := true
    ; Stable timer callbacks (kept in-class so SetTimer can cancel/reschedule the same reference).
    static wheelOff := (*) => (InputHUD.wheelFlash := 0, InputHUD.msDirty := true)

    static Install() {
        this.isMac := DirExist("/System/Library/CoreServices")
        ; Overlays / Image text / ToolTip all use the GUI toolkit, which isn't ready during top-level
        ; auto-execute (before the event loop is up). SetTimer callbacks run on the main/UI thread once it
        ; is, so build and show everything from there.
        SetTimer(() => this.Setup(), -60)
    }

    static Setup() {
        ; The layout below is authored in LOGICAL units. Each overlay's canvas is built at physical resolution
        ; (Image.Create's scale arg = DPI) so it stays crisp, but the overlay's on-screen size and all screen
        ; geometry (placement, drag hit-testing) use pw/ph scaled by Geo, matching the coordinate system the OS
        ; reports: physical pixels on Windows/Linux (Geo = DPI), logical points on macOS (Geo = 1, since Cocoa
        ; handles HiDPI itself — the overlay is still drawn from the physical-resolution canvas).
        this.DPI := A_ScreenScale
        this.Geo := this.isMac ? 1 : this.DPI
        this.ms := {ov: Overlay(0, 0, 118, 188, this.DPI), x: 0, y: 0, w: 118, h: 188, pw: Round(118 * this.Geo), ph: Round(188 * this.Geo)}
        this.BuildKeyboard()
        this.Place()
        this.RenderKeyboard()
        this.RenderMouse()
        this.kb.ov.Show()
        this.ms.ov.Show()
        this.HookInput()
        this.SetupDrag()
        SetTimer(() => this.Tick(), 16)  ; the ONLY place overlays are redrawn — always the main/UI thread
        Hotkey("^!+q", (*) => ExitApp())
        HotkeyCard.Show("Input HUD", [
            ["Type / click", "Keys & buttons light up as you press"],
            ["Blue vs amber", "Blue = physical; amber = script-injected (Send / remap)"],
            ["Super/Win/Cmd + drag", "Move a HUD"],
            ["Ctrl+Alt+Shift+Q", "Quit"] ])
    }

    ; Redraw whatever changed, on the main/UI thread. Coalesces bursts of input into one paint per tick.
    static Tick() {
        if this.kbDirty {
            this.kbDirty := false
            this.RenderKeyboard()
        }
        if this.msDirty {
            this.msDirty := false
            this.RenderMouse()
        }
    }

    ; ======================================================================
    ; Keyboard layout (US QWERTY). Each key: [vk, label, widthUnits]; [0, "", w] is a spacer.
    ; ======================================================================
    static BuildKeyboard() {
        local rows := [
            [ [27,"Esc",1], [0,"",1], [112,"F1",1],[113,"F2",1],[114,"F3",1],[115,"F4",1], [0,"",.5],
              [116,"F5",1],[117,"F6",1],[118,"F7",1],[119,"F8",1], [0,"",.5], [120,"F9",1],[121,"F10",1],[122,"F11",1],[123,"F12",1] ],
            [ [192,"``",1],[49,"1",1],[50,"2",1],[51,"3",1],[52,"4",1],[53,"5",1],[54,"6",1],[55,"7",1],[56,"8",1],[57,"9",1],[48,"0",1],[189,"-",1],[187,"=",1],[8,"Bksp",2] ],
            [ [9,"Tab",1.5],[81,"Q",1],[87,"W",1],[69,"E",1],[82,"R",1],[84,"T",1],[89,"Y",1],[85,"U",1],[73,"I",1],[79,"O",1],[80,"P",1],[219,"[",1],[221,"]",1],[220,"\",1.5] ],
            [ [20,"Caps",1.75],[65,"A",1],[83,"S",1],[68,"D",1],[70,"F",1],[71,"G",1],[72,"H",1],[74,"J",1],[75,"K",1],[76,"L",1],[186,";",1],[222,"'",1],[13,"Enter",2.25] ],
            [ [160,"Shift",2.25],[90,"Z",1],[88,"X",1],[67,"C",1],[86,"V",1],[66,"B",1],[78,"N",1],[77,"M",1],[188,",",1],[190,".",1],[191,"/",1],[161,"Shift",2.75] ],
            this.BottomRow()
        ]

        local keys := []
        local uy := 0
        for row in rows {
            local ux := 0.0
            for k in row {
                if (k[1] != 0)
                    keys.Push(this.MakeKey(k[1], k[2], ux, uy, k[3]))
                ux += k[3]
            }
            uy += 1
        }

        ; Editing / nav cluster + arrows to the right of the main block (fills the top-right, like a TKL board).
        local navX := 15.5
        keys.Push(this.MakeKey(45,"Ins",  navX,   1)), keys.Push(this.MakeKey(36,"Home", navX+1, 1)), keys.Push(this.MakeKey(33,"PgUp", navX+2, 1))
        keys.Push(this.MakeKey(46,"Del",  navX,   2)), keys.Push(this.MakeKey(35,"End",  navX+1, 2)), keys.Push(this.MakeKey(34,"PgDn", navX+2, 2))
        keys.Push(this.MakeKey(38,"Up",   navX+1, 4))
        keys.Push(this.MakeKey(37,"Lt",   navX,   5)), keys.Push(this.MakeKey(40,"Dn", navX+1, 5)), keys.Push(this.MakeKey(39,"Rt", navX+2, 5))

        ; Panel size = bounding box of all keys + padding.
        local maxR := 0.0, maxB := 0.0
        for k in keys {
            maxR := Max(maxR, k.px + k.pw)
            maxB := Max(maxB, k.py + this.U)
            this.dispVk[k.vk] := true
        }
        local lw := Round(maxR + this.Pad), lh := Round(maxB + this.Pad)
        this.kb := {ov: Overlay(0, 0, lw, lh, this.DPI), x: 0, y: 0, w: lw, h: lh, pw: Round(lw * this.Geo), ph: Round(lh * this.Geo), keys: keys}
    }

    ; The bottom modifier row differs per OS (Command/Option on macOS; Win/Alt/Menu elsewhere).
    ; vks: LCtrl 162, LWin 91, LAlt 164, Space 32, RAlt 165, RWin 92, Apps 93, RCtrl 163.
    static BottomRow() {
        if this.isMac
            return [ [162,"Ctrl",1.25],[164,"Opt",1.25],[91,"Cmd",1.25],[32,"",6.25],[92,"Cmd",1.25],[165,"Opt",1.25],[163,"Ctrl",1.5] ]
        return [ [162,"Ctrl",1.25],[91,"Win",1.25],[164,"Alt",1.25],[32,"",6.25],[165,"Alt",1.25],[92,"Win",1.25],[93,"Menu",1.25],[163,"Ctrl",1.25] ]
    }

    ; Positions a key and pre-measures its label (so rendering never re-measures — keeps redraws cheap).
    static MakeKey(vk, label, ux, uy, w := 1) {
        local P := this.U + this.G
        local px := this.Pad + ux * P
        local py := this.Pad + uy * P
        local pw := w * this.U + (w - 1) * this.G
        local tw := 0, th := 0
        if (label != "") {
            local tmp := Image.Create(1, 1)
            tmp.MeasureText(label, this.Font, &tw, &th)
            tmp.Dispose()
        }
        return {vk: vk, label: label, px: px, py: py, pw: pw, tx: px + (pw - tw) / 2, ty: py + (this.U - th) / 2}
    }

    static RenderKeyboard() {
        local kb := this.kb
        local img := Image.Create(kb.w, kb.h, , this.DPI)
        img.FillRoundRect(0, 0, kb.w, kb.h, 14, this.Bg)
        img.DrawRoundRect(1, 1, kb.w - 2, kb.h - 2, 14, this.Border, 2)
        for k in kb.keys {
            local src := this.down.Has(k.vk) ? this.down[k.vk] : ""
            img.FillRoundRect(k.px, k.py, k.pw, this.U, 6, src = "synth" ? this.SynFill : src = "phys" ? this.LitFill : this.KeyFill)
            if (src != "")
                img.DrawRoundRect(k.px + 0.5, k.py + 0.5, k.pw - 1, this.U - 1, 6, src = "synth" ? this.SynEdge : this.LitEdge, 1.5)
            if (k.label != "")
                img.DrawText(k.label, k.tx, k.ty, src = "synth" ? this.SynText : src = "phys" ? this.LitText : this.KeyText, this.Font)
        }
        kb.ov.SetImage(img)
        img.Dispose()
    }

    static RenderMouse() {
        local ms := this.ms
        local img := Image.Create(ms.w, ms.h, , this.DPI)
        img.FillRoundRect(0, 0, ms.w, ms.h, 12, this.Bg)

        local bx := 20, by := 14, bw := ms.w - 40, bh := ms.h - 34
        img.FillRoundRect(bx, by, bw, bh, bw / 2, this.KeyFill)          ; mouse body (rounded top & bottom)

        local half := bw / 2, btnH := bh * 0.42, gap := 4
        ; left / right buttons
        img.FillRoundRect(bx + gap,            by + gap, half - gap*1.5, btnH, 14, this.BtnFill("left"))
        img.FillRoundRect(bx + half + gap*0.5, by + gap, half - gap*1.5, btnH, 14, this.BtnFill("right"))
        ; scroll wheel (also the middle button) between them
        local ww := 12, wx := bx + half - ww/2, wy := by + gap + 3
        local midFill := "0xFF565E6E"                                    ; idle wheel
        if this.mdown.Has("middle")
            midFill := this.mdown["middle"] = "synth" ? this.SynFill : this.LitFill
        else if (this.wheelFlash != 0)
            midFill := this.LitFill                                      ; a scroll tick flashes blue
        img.FillRoundRect(wx, wy, ww, btnH - 6, 6, midFill)
        if (this.wheelFlash != 0) {                                      ; a little up/down tick on scroll
            local ay := (this.wheelFlash < 0) ? wy + 3 : wy + btnH - 12
            img.FillRect(wx + 3, ay, ww - 6, 3, this.LitEdge)
        }
        ; thumb (side) buttons
        img.FillRoundRect(bx - 3, by + btnH + 10, 6, 16, 3, this.BtnFill("x2"))
        img.FillRoundRect(bx - 3, by + btnH + 30, 6, 16, 3, this.BtnFill("x1"))

        img.DrawText("Mouse", bx, ms.h - 16, this.KeyText, "Arial 9")
        ms.ov.SetImage(img)
        img.Dispose()
    }

    ; Fill colour for a mouse-button rectangle by pressed origin: physical = blue, injected = amber, idle = grey.
    static BtnFill(name) {
        if !this.mdown.Has(name)
            return "0xFF3A404D"
        return this.mdown[name] = "synth" ? this.SynFill : this.LitFill
    }

    ; ======================================================================
    ; Input capture — ONE InputHook for keys and mouse, notify-only (never suppresses). Handlers only mark
    ; state dirty; the actual paint happens on the main-thread tick (callbacks may be off the UI thread).
    ; ======================================================================
    static HookInput() {
        ; "V" (VisibleText + VisibleNonText) = never suppress: this is a passive monitor, so every key still
        ; reaches your focused app. "H" collects each event BEFORE hotkey processing, so we also see input a
        ; hotkey/remap suppresses — a remap's physical source key, or the #LButton used to drag a HUD — not
        ; just what passes through. KeyOpt("{All}","N") makes every key fire OnKeyDown/OnKeyUp.
        this.ih := InputHook("V H")
        this.ih.KeyOpt("{All}", "N")
        this.ih.OnKeyDown    := (h, vk, sc) => this.OnKey(vk, true)
        this.ih.OnKeyUp      := (h, vk, sc) => this.OnKey(vk, false)
        this.ih.OnMouseDown  := (h, btn, x, y) => this.OnMouse(btn, true)
        this.ih.OnMouseUp    := (h, btn, x, y) => this.OnMouse(btn, false)
        this.ih.Start()
    }

    static OnKey(vk, isDown) {
        ; Record each pressed key by origin so it can be coloured: "phys" (your fingers) vs "synth" (injected
        ; by a script/remap). A_EventInfo.IsInjected tells them apart. Clear a key only on an up whose origin
        ; matches what we recorded, so an injected up can't wipe a physical hold (or vice versa) — this also
        ; absorbs the unbalanced ups a passive monitor sees (keys held before start, OS-synthesized releases,
        ; the masking LCtrl on Win-hotkey release). ExpandVk fans a generic Ctrl/Shift/Alt out to both sides.
        local src := A_EventInfo.IsInjected ? "synth" : "phys"
        for v in this.ExpandVk(vk) {
            if isDown {
                if (!this.down.Has(v) || this.down[v] != src) {
                    this.down[v] := src
                    if this.dispVk.Has(v)
                        this.kbDirty := true
                }
            } else if (this.down.Has(v) && this.down[v] = src) {
                this.down.Delete(v)
                if this.dispVk.Has(v)
                    this.kbDirty := true
            }
        }
    }

    ; Generic modifier vks (Shift 16 / Ctrl 17 / Alt 18) light up BOTH physical keys; everything else is itself.
    static ExpandVk(vk) {
        switch vk {
            case 16: return [160, 161]
            case 17: return [162, 163]
            case 18: return [164, 165]
        }
        return [vk]
    }

    static OnMouse(btn, isDown) {
        local n := this.NormBtn(btn)
        if (n = "")
            return
        if (n = "wheelup" || n = "wheeldown") {
            if isDown {
                this.wheelFlash := (n = "wheelup") ? -1 : 1
                this.msDirty := true
                SetTimer(this.wheelOff, -180)
            }
            return
        }
        ; Same origin bookkeeping as OnKey: record "phys"/"synth", and only clear on a matching-origin up.
        local src := A_EventInfo.IsInjected ? "synth" : "phys"
        if isDown {
            if (!this.mdown.Has(n) || this.mdown[n] != src)
                this.mdown[n] := src, this.msDirty := true
        } else if (this.mdown.Has(n) && this.mdown[n] = src)
            this.mdown.Delete(n), this.msDirty := true
    }

    static NormBtn(btn) {
        switch btn {
            case "LButton", "Click": return "left"
            case "RButton": return "right"
            case "MButton": return "middle"
            case "XButton1": return "x1"
            case "XButton2": return "x2"
            case "WheelUp": return "wheelup"
            case "WheelDown": return "wheeldown"
        }
        return ""
    }

    ; ======================================================================
    ; Dragging — a Super/Win/Cmd + LButton hotkey scoped (via HotIf) to "cursor is over a HUD", so the
    ; press is captured only there; plain clicks pass through the click-through overlays untouched.
    ; ======================================================================
    static SetupDrag() {
        HotIf((*) => this.OverHud() != "")
        Hotkey("#LButton", (*) => this.DragHud())
        HotIf()
    }

    static OverHud() {
        CoordMode("Mouse", "Screen")
        MouseGetPos(&mx, &my)
        if this.InRect(mx, my, this.kb)
            return "kb"
        if this.InRect(mx, my, this.ms)
            return "ms"
        return ""
    }

    static InRect(x, y, o) => IsObject(o) && x >= o.x && x < o.x + o.pw && y >= o.y && y < o.y + o.ph

    static DragHud() {
        local which := this.OverHud()
        if (which = "")
            return
        local o := (which = "kb") ? this.kb : this.ms
        CoordMode("Mouse", "Screen")
        MouseGetPos(&gx, &gy)
        local offX := gx - o.x, offY := gy - o.y
        ; No workaround needed to light LButton here: with the InputHook's "H" option the #LButton press is
        ; reported to OnMouseDown (as physical) even though this hotkey suppresses it, so the HUD lights it.
        while GetKeyState("LButton", "P") {
            MouseGetPos(&mx, &my)
            o.x := mx - offX, o.y := my - offY
            o.ov.X := o.x, o.ov.Y := o.y
            Sleep 8
        }
    }

    ; ======================================================================
    static Place() {
        MonitorGetWorkArea(MonitorGetPrimary(), &l, &t, &r, &b)
        ; Lay the two HUDs out in the OS's screen-coordinate units (see Geo): their on-screen sizes are pw/ph,
        ; and the inter-panel gap and bottom margin scale by Geo too so the spacing keeps up with the DPI.
        local gap := Round(20 * this.Geo), bottom := Round(40 * this.Geo)
        local total := this.kb.pw + gap + this.ms.pw
        this.kb.x := (l + r) // 2 - total // 2
        this.kb.y := b - this.kb.ph - bottom
        this.ms.x := this.kb.x + this.kb.pw + gap
        this.ms.y := b - this.ms.ph - bottom
        this.kb.ov.X := this.kb.x, this.kb.ov.Y := this.kb.y
        this.ms.ov.X := this.ms.x, this.ms.ov.Y := this.ms.y
    }
}

InputHUD.Install()
Persistent
