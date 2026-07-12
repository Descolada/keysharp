#Requires AutoHotkey v2.0
#Requires capability InputMonitoring   ; the passive keyboard/mouse hook; prompted at startup by this directive
#SingleInstance Force
#import KS { A_ScreenScale }     ; A_ScreenScale is a Keysharp addition (per-platform DPI scale factor), so it lives in the KS module
#include HotkeyCard.ks

/*
    InputHUD — an on-screen keyboard + mouse HUD whose keys and buttons light up live as you type
    and click (a "keycaster", handy for screencasts, demos and teaching).

        - Two always-on-top overlays: a keyboard and a mouse. Keys/buttons glow while held — blue when you
          physically press them, amber when a script or remap injects them (Send).
        - Lock keys also show their TOGGLE state as green "LED" lights: a Num/Caps/Scroll status strip in the
          board's top-right corner (like a real keyboard's lock lights), plus a pip on the Caps key itself —
          each lit green while that lock is on. Toggle is drawn independently of the press colour, so a key
          reports BOTH whether it is held down (blue/amber) and whether its lock is on (green) at the same time.
        - The layout adapts to the OS: Win/Super/Alt/Menu on Windows & Linux, Command/Option/Control on macOS.
        - Both HUDs are separately draggable AND zoomable: left-drag either one to reposition it, or right-drag
          it to scale it — drag right to enlarge, left to shrink. Only clicks that land ON a HUD are captured;
          clicks anywhere else pass straight through the click-through overlays.
        - Non-intrusive: it never swallows your input — everything you press still reaches your apps.

        Left-drag a HUD    ...  move it
        Right-drag a HUD   ...  zoom it (right = bigger, left = smaller)
        Ctrl+Alt+Shift+Q   ...  quit

    Demonstrates cross-platform Keysharp: a single InputHook ("V H") capturing BOTH keyboard and mouse
    (OnKeyDown/OnKeyUp + OnMouseDown/OnMouseUp, KeyOpt("{All}","N") to notify-without-suppressing; the "H"
    option also surfaces input a hotkey/remap suppresses, so a remap's physical source and the LButton used
    to drag a HUD still register), telling physical from injected input via A_EventInfo.IsInjected, lock-key
    toggle state polled with GetKeyState(key, "T") and surfaced as green LEDs, Overlay canvases drawn with
    Image primitives (FillRoundRect/FillEllipse/DrawText) and updated live via SetImage, live repositioning
    through Overlay.X/Y and live rescaling via Overlay.Scale (Win/Linux) or logical W/H (macOS) — re-rendered at
    DPI*zoom to stay crisp and centre-anchored so it scales in place — and HotIf-scoped hotkeys so the drag/zoom
    only capture the click while the cursor is over a HUD (so normal clicks pass straight through).

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
    ; Lock-toggle LEDs (Num/Caps/Scroll). Green = ON — deliberately unlike the blue/amber press colours, so a key
    ; can show its toggle (green LED) and its press (blue/amber fill) at once without the two being confused.
    static LedOn    := "0xFF34D399"      ; toggled ON: green LED core
    static LedGlow  := "0x6634D399"      ; ON: soft translucent halo around the core
    static LedOnRim := "0xFFA7F3D0"      ; ON: bright rim
    static LedOff   := "0xFF2A2F3A"      ; toggled OFF: recessed dark dot
    static LedOffRim:= "0xFF434B5A"      ; OFF: faint rim
    static LedLab   := "0xFF828C9E"      ; status-strip label (unlit)
    static LedLabOn := "0xFFB9EED4"      ; status-strip label (lit)
    static LedFont  := "Arial 8 bold"

    ; ---- state -------------------------------------------------------------
    static DPI := 1                      ; canvas render scale (A_ScreenScale): each overlay bitmap is drawn at physical resolution
    static Geo := 1                      ; on-screen geometry scale: DPI where the OS places in physical px (Win/Linux), 1 where it
                                         ; places in logical points and handles HiDPI itself (macOS) — mirrors the GUI's DpiScale
    static ih := ""
    static kb := ""                      ; {ov, cx, cy (fixed centre = zoom anchor), x, y (derived top-left), w, h (logical render size), pw, ph (physical on-screen size), zoom, keys[]}
    static ms := ""                      ; {ov, cx, cy (fixed centre = zoom anchor), x, y (derived top-left), w, h (logical render size), pw, ph (physical on-screen size), zoom}
    static down := Map()                 ; vk -> "phys" | "synth"  (pressed keys, keyed by origin)
    static mdown := Map()                ; "left"/"right"/"middle"/"x1"/"x2" -> "phys" | "synth"
    static wheelFlash := 0               ; -1 up, +1 down, 0 none
    static dispVk := Map()               ; vks the keyboard actually shows (gates re-render)
    ; Lock keys: name -> {GetKeyState name, status-strip label}. Only these three carry a real toggle state.
    static Locks := [["num","NumLock","Num"], ["caps","CapsLock","Caps"], ["scroll","ScrollLock","Scroll"]]
    static toggle := Map()               ; lock name ("num"/"caps"/"scroll") -> Bool: is that lock currently on
    static leds := []                    ; pre-laid-out status-strip LEDs (built alongside the keyboard)
    static togPoll := true               ; a lock key fired -> refresh toggle state on the next tick
    static togTick := 0                  ; slow-poll counter (catches lock changes made by other apps)
    static isMac := false
    static kbDirty := true               ; a render is pending (rendered on the main-thread tick)
    static msDirty := true
    ; Zoom (right-drag over a HUD): each HUD carries its own on-screen scale factor. Horizontal drag distance
    ; maps to the scale — drag right to enlarge, left to shrink — clamped to [ZoomMin, ZoomMax].
    static ZoomMin  := 0.5
    static ZoomMax  := 3.0
    static ZoomSens := 300               ; px of horizontal drag per doubling (or halving) of a HUD's scale
    static ZoomEase := 0.5               ; per-tick easing toward the drag's target scale (1 = instant, no smoothing)
    static busy := false                 ; a drag/zoom loop is running — blocks a second one from starting on top of it
    ; Stable timer callbacks (kept in-class so SetTimer can cancel/reschedule the same reference).
    static wheelOff := (*) => (InputHUD.wheelFlash := 0, InputHUD.msDirty := true)

    static Install() {
        this.isMac := DirExist("/System/Library/CoreServices")
        HotkeyCard.SetTrayIcon("⌨️")
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
        this.ms := {ov: Overlay(0, 0, 118, 188, this.DPI), cx: 0, cy: 0, x: 0, y: 0, w: 118, h: 188, pw: Round(118 * this.Geo), ph: Round(188 * this.Geo), zoom: 1}
        this.BuildKeyboard()
        this.RefreshToggles()            ; seed the lock LEDs so the first paint already shows the real state
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
            ["Green LED", "Num / Caps / Scroll lock is toggled on"],
            ["Left-drag a HUD", "Move it"],
            ["Right-drag a HUD", "Zoom it (right = bigger, left = smaller)"],
            ["Ctrl+Alt+Shift+Q", "Quit"] ])
    }

    ; Redraw whatever changed, on the main/UI thread. Coalesces bursts of input into one paint per tick.
    static Tick() {
        ; Lock LEDs: refresh promptly after a lock key fires (togPoll), and on a slow safety poll (~200ms) so a
        ; lock toggled by another app is still picked up. Throttled so we never hammer GetKeyState (on Wayland it
        ; can round-trip the input daemon). Num/Scroll aren't drawn keys, so polling is the only way to see them.
        this.togTick += 1
        if (this.togPoll || this.togTick >= 12) {
            this.togPoll := false, this.togTick := 0
            if this.RefreshToggles()
                this.kbDirty := true
        }
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
        this.kb := {ov: Overlay(0, 0, lw, lh, this.DPI), cx: 0, cy: 0, x: 0, y: 0, w: lw, h: lh, pw: Round(lw * this.Geo), ph: Round(lh * this.Geo), keys: keys, zoom: 1}
        this.BuildLeds(lw, navX)         ; status-light strip in the empty top-right corner, above the nav cluster
    }

    ; Lays out the Num/Caps/Scroll status LEDs in the empty top-right corner of the board (where a real keyboard's
    ; lock lights live): three round LEDs evenly spaced across the width of the nav cluster below them, each with a
    ; small label. Positions/labels are measured once here so RenderKeyboard just fills them per toggle state.
    static BuildLeds(lw, navX) {
        local P := this.U + this.G
        local left := this.Pad + navX * P            ; left edge of the nav cluster below
        local band := (lw - this.Pad - left) / this.Locks.Length
        local d := 9                                 ; LED diameter (logical px)
        local tmp := Image.Create(1, 1)
        this.leds := []
        for i, lk in this.Locks {
            local cx := left + (i - 0.5) * band      ; centre of this LED's slot (i is 1-based)
            local tw := 0, th := 0
            tmp.MeasureText(lk[3], this.LedFont, &tw, &th)
            this.leds.Push({name: lk[1], label: lk[3], x: cx - d / 2, y: this.Pad + 1, d: d,
                            lx: cx - tw / 2, ly: this.Pad + 1 + d + 2})
        }
        tmp.Dispose()
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
        ; A lock key on the board (only Caps, here) carries a toggle name so it gets an on-key toggle pip.
        local tog := (vk = 20) ? "caps" : (vk = 144) ? "num" : (vk = 145) ? "scroll" : ""
        return {vk: vk, label: label, tog: tog, px: px, py: py, pw: pw, tx: px + (pw - tw) / 2, ty: py + (this.U - th) / 2}
    }

    static RenderKeyboard() {
        local kb := this.kb
        local z := this.Geometry(kb)     ; snapshot the zoom ONCE; the bitmap, on-screen size and position all use z
        ; Draw the (fixed) logical layout at DPI * z so the on-screen bitmap stays crisp at any zoom level.
        local img := Image.Create(kb.w, kb.h, , this.DPI * z)
        img.FillRoundRect(0, 0, kb.w, kb.h, 14, this.Bg)
        img.DrawRoundRect(1, 1, kb.w - 2, kb.h - 2, 14, this.Border, 2)
        for k in kb.keys {
            local src := this.down.Has(k.vk) ? this.down[k.vk] : ""
            img.FillRoundRect(k.px, k.py, k.pw, this.U, 6, src = "synth" ? this.SynFill : src = "phys" ? this.LitFill : this.KeyFill)
            if (src != "")
                img.DrawRoundRect(k.px + 0.5, k.py + 0.5, k.pw - 1, this.U - 1, 6, src = "synth" ? this.SynEdge : this.LitEdge, 1.5)
            if (k.label != "")
                img.DrawText(k.label, k.tx, k.ty, src = "synth" ? this.SynText : src = "phys" ? this.LitText : this.KeyText, this.Font)
            if (k.tog != "")                                          ; a lock key on the board: toggle pip in its corner
                this.DrawLed(img, k.px + k.pw - 9, k.py + 3, 7, this.IsLockOn(k.tog), false)
        }
        ; Keyboard status lights (Num / Caps / Scroll) in the top-right corner — the toggle state at a glance,
        ; including the two locks that have no key on this layout.
        for led in this.leds {
            local on := this.IsLockOn(led.name)
            this.DrawLed(img, led.x, led.y, led.d, on)
            img.DrawText(led.label, led.lx, led.ly, on ? this.LedLabOn : this.LedLab, this.LedFont)
        }
        this.ApplyGeom(kb, z)            ; size + position the overlay with the SAME z the bitmap used — one clean repaint
        kb.ov.SetImage(img)
        img.Dispose()
    }

    static IsLockOn(name) => this.toggle.Has(name) && this.toggle[name]

    ; Draws a round status LED in the bounding box (x, y, d, d): a glowing green dot when on, a recessed dark
    ; dot when off. Used for both the status strip (with halo) and the smaller on-key toggle pips (halo off, so
    ; the halo can't bleed into a key's centred label).
    static DrawLed(img, x, y, d, on, halo := true) {
        if on {
            if halo
                img.FillEllipse(x - 2, y - 2, d + 4, d + 4, this.LedGlow)  ; soft halo
            img.FillEllipse(x, y, d, d, this.LedOn)
            img.DrawEllipse(x, y, d, d, this.LedOnRim, 1)
        } else {
            img.FillEllipse(x, y, d, d, this.LedOff)
            img.DrawEllipse(x, y, d, d, this.LedOffRim, 1)
        }
    }

    ; Polls the three lock keys' toggle state (GetKeyState "T") into this.toggle; returns true if any changed.
    ; Cheap on X11 (reads Xkb state directly); on Wayland it reads the input daemon's LED snapshot — the same
    ; daemon that already serves this HUD's physical hook (so if keys light up at all, this works too).
    static RefreshToggles() {
        local changed := false
        for lk in this.Locks {
            local on := GetKeyState(lk[2], "T") ? true : false
            if (!this.toggle.Has(lk[1]) || this.toggle[lk[1]] != on)
                this.toggle[lk[1]] := on, changed := true
        }
        return changed
    }

    static RenderMouse() {
        local ms := this.ms
        local z := this.Geometry(ms)
        local img := Image.Create(ms.w, ms.h, , this.DPI * z)
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
        this.ApplyGeom(ms, z)
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
        ; hotkey/remap suppresses — a remap's physical source key, or the LButton used to drag a HUD — not
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
        if (vk = 20 || vk = 144 || vk = 145)     ; a lock key: its toggle may have just flipped — refresh next tick
            this.togPoll := true
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
    ; Dragging & zooming — plain Left/Right-button hotkeys scoped (via HotIf) to "cursor is over a HUD", so a
    ; press is captured ONLY over a HUD; clicks anywhere else pass through the click-through overlays untouched.
    ; Left-drag moves a HUD; right-drag scales it (right = enlarge, left = shrink). No modifier is needed
    ; (unlike WindowGrab's Super+drag): a plain button and a Super+button are mutually-exclusive hotkey
    ; variants, so InputHUD and WindowGrab can run together without their drag gestures colliding.
    ; ======================================================================
    static SetupDrag() {
        HotIf((*) => this.OverHud() != "")
        Hotkey("LButton", (*) => this.DragHud())
        Hotkey("RButton", (*) => this.ZoomHud())
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
        if (which = "" || this.busy)                 ; ignore a press that lands mid-drag/zoom (see ZoomHud)
            return
        local o := (which = "kb") ? this.kb : this.ms
        this.busy := true
        try {
            CoordMode("Mouse", "Screen")
            MouseGetPos(&gx, &gy)
            local offX := gx - o.cx, offY := gy - o.cy   ; grab offset from the HUD's centre (the zoom anchor)
            ; No workaround needed to light LButton here: with the InputHook's "H" option the LButton press is
            ; reported to OnMouseDown (as physical) even though this hotkey suppresses it, so the HUD lights it.
            while GetKeyState("LButton", "P") {
                MouseGetPos(&mx, &my)
                o.cx := mx - offX, o.cy := my - offY     ; move the centre; x/y follow (size is unchanged mid-drag)
                o.x := Round(o.cx - o.pw / 2), o.y := Round(o.cy - o.ph / 2)
                o.ov.X := o.x, o.ov.Y := o.y
                Sleep 8
            }
        } finally {
            this.busy := false
        }
    }

    ; Right-drag over a HUD scales it: the horizontal distance from where the drag began sets the zoom factor
    ; (right = enlarge, left = shrink); the HUD scales around its fixed centre (cx/cy). Like DragHud this runs as a
    ; hotkey pseudo-thread and loops while RButton is held; it writes ONLY o.zoom and flips the dirty flag, so the
    ; main-thread tick derives all geometry and redraws the crisp bitmap (this thread never draws or resizes).
    static ZoomHud() {
        local which := this.OverHud()
        ; Guard against re-entry: a mouse button can retrigger its hotkey mid-drag (autorepeat, the InputHook "H"
        ; re-surfacing the held press, a bounced event). A second ZoomHud starting on top of the first captures its
        ; OWN startX/startZoom, and the two loops then drive o.zoom toward different targets — which is what made the
        ; HUD suddenly jump to a huge or tiny size. One `busy` flag lets only one drag/zoom loop run at a time.
        if (which = "" || this.busy)
            return
        local o := (which = "kb") ? this.kb : this.ms
        this.busy := true
        try {
            CoordMode("Mouse", "Screen")
            MouseGetPos(&startX)
            local startZoom := o.zoom
            while GetKeyState("RButton", "P") {
                MouseGetPos(&mx)
                ; Exponential target: every ZoomSens px of travel doubles (right) or halves (left) the scale, so the
                ; gesture feels symmetric. Ease the actual scale toward that target instead of snapping to it, so a
                ; fast flick ramps smoothly over a few ticks; settle exactly on target so a resting mouse stops
                ; re-rendering. Only redraw when the (clamped) scale actually changed.
                local target := this.ClampZoom(startZoom * (2 ** ((mx - startX) / this.ZoomSens)))
                local next := o.zoom + (target - o.zoom) * this.ZoomEase
                if (Abs(target - next) < 0.004)
                    next := target
                if (next != o.zoom) {
                    o.zoom := next               ; the ONLY shared write; the tick derives x/y/pw/ph from it
                    if (which = "kb")
                        this.kbDirty := true
                    else
                        this.msDirty := true
                }
                Sleep 8
            }
        } finally {
            this.busy := false
        }
    }

    ; Snapshots a HUD's zoom ONCE and derives its on-screen geometry from that single value: the cached on-screen
    ; size (pw/ph) and top-left (x/y), positioned so the HUD's fixed CENTRE (cx/cy — set by Place, moved by a drag)
    ; stays put as it scales. Returns z so the caller renders the bitmap at the SAME zoom it will be displayed at.
    ; The zoom loop only ever writes `zoom` (one field), so a render tick that interleaves with it can never read a
    ; half-updated geometry — which was the cause of the board briefly rendering at one scale but showing at another
    ; (a huge, blurry, off-centre frame). All geometry is computed here, on the render's own (main) thread.
    static Geometry(o) {
        local z := o.zoom
        o.pw := Round(o.w * this.Geo * z)
        o.ph := Round(o.h * this.Geo * z)
        o.x := Round(o.cx - o.pw / 2)
        o.y := Round(o.cy - o.ph / 2)
        return z
    }

    ; Pushes the geometry onto the overlay using the SAME z the bitmap was rendered at, as a SILENT size change so
    ; the SetImage that follows is the only repaint: on Windows/Linux the on-screen size rides Overlay.Scale (= DPI*z)
    ; — changing Scale just re-defines the canvas, it doesn't repaint — while macOS carries the zoom in the logical
    ; W/H (Cocoa renders the HiDPI backing itself). Position is set here too so size and position land together.
    static ApplyGeom(o, z) {
        if this.isMac {
            o.ov.W := Round(o.w * z)
            o.ov.H := Round(o.h * z)
        } else {
            o.ov.Scale := this.DPI * z
        }
        o.ov.X := o.x, o.ov.Y := o.y
    }

    static ClampZoom(z) => Min(this.ZoomMax, Max(this.ZoomMin, z))

    ; ======================================================================
    static Place() {
        MonitorGetWorkArea(MonitorGetPrimary(), &l, &t, &r, &b)
        ; Lay the two HUDs out in the OS's screen-coordinate units (see Geo): their on-screen sizes are pw/ph,
        ; and the inter-panel gap and edge margins scale by Geo too so the spacing keeps up with the DPI.
        ; Anchored to the bottom-LEFT (keyboard first, mouse to its right) so the pair clears the HotkeyCard,
        ; which sits at the bottom-right.
        local gap := Round(20 * this.Geo), margin := Round(40 * this.Geo)
        local kbX := l + margin, kbY := b - this.kb.ph - margin
        local msX := kbX + this.kb.pw + gap, msY := b - this.ms.ph - margin
        ; The zoom anchor is each HUD's CENTRE (cx/cy); x/y/pw/ph are then derived from it by Geometry.
        this.kb.cx := kbX + this.kb.pw / 2, this.kb.cy := kbY + this.kb.ph / 2
        this.ms.cx := msX + this.ms.pw / 2, this.ms.cy := msY + this.ms.ph / 2
        this.Geometry(this.kb)
        this.Geometry(this.ms)
        this.kb.ov.X := this.kb.x, this.kb.ov.Y := this.kb.y
        this.ms.ov.X := this.ms.x, this.ms.ov.Y := this.ms.y
    }
}

InputHUD.Install()
Persistent
