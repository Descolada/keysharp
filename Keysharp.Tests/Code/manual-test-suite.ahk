#NoTrayIcon

#Requires AutoHotkey v2.0
#SingleInstance Force

#import "Ks" { * }
global gMain := ""
global gTabs := ""
global gLogEdit := ""
global gSendTarget := ""
global gHotstringTarget := ""
global gInputHookExpected := ""
global gInputHookActual := ""
global gWindowTitleEdit := ""
global gWindowInfoEdit := ""
global gClipboardTextEdit := ""
global gSoundInfoEdit := ""
global gSoundVolumeEdit := ""
global gClipboardMonitorEnabled := false
global gClipboardChangeCount := 0
global gClipboardClipWaitRunning := false
global gClipboardDelayedPayload := ""
global gHotkeyHitCount := 0
global gHotstringHitCount := 0
global gInputHookObj := ""
global gInputHookLastText := ""
global gInputHookLastReason := ""
global gWindowHelper := ""
global gPixelHelper := ""
global gWindowHelperOriginalPos := Map()
global gPixelAssetPath := A_ScriptDir "/Gui/killbill.png"
global gStatus := Map()
global gLogText := ""
global gWinEventHooks := []
global gWinEventCount := 0
global gWinEventMoveCount := 0
global gMouseHookObj := ""
global gMouseDownCount := 0
global gMouseUpCount := 0
global gMouseMoveCount := 0

BuildMainGui()
RegisterInputProbes()
EnsureWindowHelper(false)
EnsurePixelHelper(false)
UpdateEnvironmentSummary()
gMain.Show("w1180 h900")
AppendLog("Suite ready. Start on the Overview tab and confirm permissions before testing.")
return

BuildMainGui() {
	global gMain, gTabs, gLogEdit, gSendTarget, gHotstringTarget, gInputHookExpected, gInputHookActual
	global gWindowTitleEdit, gWindowInfoEdit, gClipboardTextEdit, gSoundInfoEdit, gSoundVolumeEdit, gStatus

	gMain := Gui("+Resize", "Cross-Platform Manual Suite")
	gMain.SetFont("s9", "Segoe UI")
	gMain.OnEvent("Close", (*) => ExitApp())

	header := gMain.AddText("x20 y16 w1120 h28", "Manual release harness for input, foreign windows, pixel/image, and clipboard behavior.")
	header.SetFont("s14 Bold")

	gTabs := gMain.AddTab3("x20 y52 w1120 h640", ["Overview", "Input", "Windows", "Pixel && Image", "Clipboard", "Sound"])

	gTabs.UseTab("Overview")
	gMain.AddText("xc+16 yc+12 w1040", "Use this suite on each target platform separately: Windows, Linux X11, Linux Wayland, and macOS. Record blocked-by-policy cases distinctly from true failures.")
	gMain.AddText("xc+16 y+6 w1040", "Recommended order: 1) run self-validating tests, 2) run external-window tests, 3) repeat after granting any required OS permissions.")
	envInfo := gMain.AddEdit("xc+16 y+10 w800 h140 ReadOnly -Wrap")
	gStatus["overview_env"] := envInfo

	btnCreateHelpers := gMain.AddButton("x+24 yp w240 h30", "Show Helper Windows")
	btnCreateHelpers.OnEvent("Click", (*) => ShowHelperWindows())
	btnResetStatuses := gMain.AddButton("xp y+10 w240 h30", "Reset Status Text")
	btnResetStatuses.OnEvent("Click", (*) => ResetStatuses())
	btnClearLog := gMain.AddButton("xp y+10 w240 h30", "Clear Log")
	btnClearLog.OnEvent("Click", (*) => ClearLog())
	btnActiveInfo := gMain.AddButton("xp y+10 w240 h30", "Capture Active Window")
	btnActiveInfo.OnEvent("Click", (*) => CaptureActiveWindow())

	overviewNotes := gMain.AddEdit("xc+16 y+20 w1064 h300 ReadOnly -Wrap")
	overviewNotes.Value :=
	(
	"Suite conventions:`n"
	"- PASS: the suite validated the result or the tester visually confirmed the expected behavior.`n"
	"- FAIL: incorrect behavior, wrong text, wrong coordinates, wrong target window, or no callback.`n"
	"- BLOCKED: the OS, compositor, or permissions prevented the operation.`n`n"
	"High-signal checks for release:`n"
	"- Send/SendText/SendInput/SendPlay into the suite-owned edit.`n"
	"- Global hotkey and hotstring delivery.`n"
	"- InputHook capture and end reason.`n"
	"- WinExist/WinActivate/WinMove against the helper window, then a foreign app.`n"
	"- PixelGetColor/PixelSearch/ImageSearch against the pixel helper.`n"
	"- Clipboard text, delayed ClipWait, OnClipboardChange, and image copy."
	)

	gTabs.UseTab("Input")
	gMain.AddText("xc+16 yc+12 w1060", "These tests are intended to be mostly self-validating. If the test updates the target edit or status line correctly, mark it as good. If focus or permissions interfere, note the behavior in the log.")

	sendGroup := gMain.AddGroupBox("xc+16 yc+50 w500 h430", "Send Variants")
	gMain.UseGroup(sendGroup)
	gMain.AddText("xc+16 yc+24 w468", "Each button clears the target edit, focuses it, sends text, and validates the exact resulting text.")
	gSendTarget := gMain.AddEdit("xc+16 y+8 w468 h120 -Wrap")
	btnSend := gMain.AddButton("xc+16 y+12 w110 h28", "Run Send()")
	btnSend.OnEvent("Click", (*) => RunSendVariant("Send"))
	btnSendText := gMain.AddButton("x+10 yp w110 h28", "Run SendText()")
	btnSendText.OnEvent("Click", (*) => RunSendVariant("SendText"))
	btnSendInput := gMain.AddButton("x+10 yp w110 h28", "Run SendInput()")
	btnSendInput.OnEvent("Click", (*) => RunSendVariant("SendInput"))
	btnSendPlay := gMain.AddButton("x+10 yp w110 h28", "Run SendPlay()")
	btnSendPlay.OnEvent("Click", (*) => RunSendVariant("SendPlay"))
	btnSendUnicode := gMain.AddButton("xc+16 y+10 w150 h28", "Unicode SendText")
	btnSendUnicode.OnEvent("Click", (*) => RunSendScenario("SendText", "Mägi, Köln, São Paulo`n", "Unicode SendText"))
	btnSendEmoji := gMain.AddButton("x+12 yp w150 h28", "Emoji SendText")
	btnSendEmoji.OnEvent("Click", (*) => RunSendScenario("SendText", "Faces: 😀 😎 🚀`n", "Emoji SendText"))
	btnSendMixed := gMain.AddButton("x+12 yp w146 h28", "Mixed Unicode")
	btnSendMixed.OnEvent("Click", (*) => RunSendScenario("SendInput", "Mixed: ääkkönen, 日本語, 😀`n", "Mixed Unicode SendInput"))
	gMain.AddText("xc+16 y+12 w468 h34", "Extra coverage: Unicode, accented Latin text, CJK text, and emoji. These tests are useful for keyboard-layout and surrogate-pair issues.")
	sendStatus := gMain.AddText("xc+16 y+8 w468 h44", "Status: Not run")
	gStatus["input_send"] := sendStatus
	gMain.UseGroup()
	gTabs.UseTab("Input")

	hotkeyGroup := gMain.AddGroupBox("xc+540 yc+50 w540 h490", "Hotkey / Hotstring / InputHook")
	gMain.UseGroup(hotkeyGroup)
	gMain.AddText("xc+16 yc+24 w508 h34", "Hotkey probe: test several modifier combinations. Hotstring probe: run the 3-case matrix in the edit below. InputHook: click Start, type the expected text, then press Enter.")
	gMain.AddText("xc+16 y+6 w508 h40", "Hotstring matrix: 1) kssuite  2) ksend<Space>  3) prefixksword<Space>. These also verify documented A_EndChar behavior. Modifier mapping hint: Ctrl = ^, Alt/Option = !, Shift = +, Win/Cmd = #.")
	btnResetHotkey := gMain.AddButton("xc+16 y+10 w170 h28", "Reset Hotkey Counter")
	btnResetHotkey.OnEvent("Click", (*) => ResetHotkeyProbe())
	gMain.AddText("x+12 yp w320 h28", "Press: Ctrl+Alt+1, Win/Cmd+Ctrl+9, Win/Cmd+Alt+0")
	hotkeyStatus := gMain.AddText("xc+16 y+10 w508 h32", "Hotkey status: waiting for the matrix hotkeys")
	gStatus["input_hotkey"] := hotkeyStatus

	gHotstringTarget := gMain.AddEdit("xc+16 y+10 w508 h100 -Wrap")
	gHotstringTarget.Value := HotstringProbeInstructions()
	btnResetHotstring := gMain.AddButton("xc+16 y+10 w170 h28", "Reset Hotstring Probe")
	btnResetHotstring.OnEvent("Click", (*) => ResetHotstringProbe())
	btnValidateHotstring := gMain.AddButton("x+12 yp w170 h28", "Validate Hotstring")
	btnValidateHotstring.OnEvent("Click", (*) => ValidateHotstringProbe())
	hotstringStatus := gMain.AddText("xc+16 y+10 w508 h24", "Hotstring status: waiting for the 3 probe cases")
	gStatus["input_hotstring"] := hotstringStatus

	gMain.AddText("xc+16 y+12 w64 h24", "Expected:")
	gInputHookExpected := gMain.AddEdit("x+6 yp-2 w120", "abc123")
	btnStartInputHook := gMain.AddButton("x+16 yp-2 w140 h28", "Start InputHook")
	btnStartInputHook.OnEvent("Click", (*) => StartInputHookProbe())
	btnValidateInputHook := gMain.AddButton("x+12 yp w140 h28", "Validate Hook")
	btnValidateInputHook.OnEvent("Click", (*) => ValidateInputHookProbe())
	gInputHookActual := gMain.AddEdit("xc+16 y+10 w508 h26 ReadOnly")
	inputHookStatus := gMain.AddText("xc+16 y+8 w508 h24", "InputHook status: idle")
	gStatus["input_hook"] := inputHookStatus
	gMain.UseGroup()
	gTabs.UseTab("Input")

	mouseGroup := gMain.AddGroupBox("xc+16 yc+496 w500 h120", "Mouse InputHook (OnMouseDown / OnMouseUp / OnMouseMove)")
	gMain.UseGroup(mouseGroup)
	btnStartMouse := gMain.AddButton("xc+16 yc+24 w104 h26", "Start Mouse")
	btnStartMouse.OnEvent("Click", (*) => StartMouseHookProbe())
	btnStopMouse := gMain.AddButton("x+8 yp w70 h26", "Stop")
	btnStopMouse.OnEvent("Click", (*) => StopMouseHookProbe())
	btnBlockMove := gMain.AddButton("x+8 yp w120 h26", "Block Move (2s)")
	btnBlockMove.OnEvent("Click", (*) => TestBlockMoveProbe())
	btnBlockMButton := gMain.AddButton("x+8 yp w160 h26", "Toggle Block MButton")
	btnBlockMButton.OnEvent("Click", (*) => ToggleBlockMButton())
	mouseReadout := gMain.AddEdit("xc+16 y+10 w468 h22 ReadOnly -Wrap", "Start, then click / wheel / move the mouse to see the last event and live counts.")
	gStatus["input_mouse_readout"] := mouseReadout
	mouseStatus := gMain.AddText("xc+16 y+8 w468 h22", "Mouse hook: idle")
	gStatus["input_mouse"] := mouseStatus
	gMain.UseGroup()

	gTabs.UseTab("Windows")
	gMain.AddText("xc+16 yc+12 w1040", "The helper-window tests are self-validating. The external-window tools are intentionally semi-automated: point them at a real app and confirm the visible behavior.")

	helperWinGroup := gMain.AddGroupBox("xc+16 yc+50 w500 h390", "Helper Window Automation")
	gMain.UseGroup(helperWinGroup)
	gMain.AddText("xc+16 yc+24 w468 h34", "These buttons act on a dedicated helper window owned by the suite. Use them to validate WinExist, WinActivate, WinWaitActive, WinGetPos, and WinMove without external app noise.")
	btnShowHelper := gMain.AddButton("xc+16 y+10 w150 h28", "Show Helper")
	btnShowHelper.OnEvent("Click", (*) => EnsureWindowHelper(true))
	btnRunHelperAutomation := gMain.AddButton("x+12 yp w220 h28", "Run Helper Win* Test")
	btnRunHelperAutomation.OnEvent("Click", (*) => RunWindowHelperAutomation())
	btnHideHelper := gMain.AddButton("x+12 yp w74 h28", "Hide")
	btnHideHelper.OnEvent("Click", (*) => HideWindowHelper())
	helperStatus := gMain.AddText("xc+16 y+12 w468 h96", "Helper status: Not run")
	gStatus["window_helper"] := helperStatus
	gMain.UseGroup()
	gTabs.UseTab("Windows")

	externalWinGroup := gMain.AddGroupBox("xc+540 yc+50 w540 h410", "External Window Tools")
	gMain.UseGroup(externalWinGroup)
	gMain.AddText("xc+16 yc+24 w508 h34", "Use Capture Active Window to prefill the title field, or type your own title match. Activate and Move are semi-automated and should be confirmed by the tester.")
	gWindowTitleEdit := gMain.AddEdit("xc+16 y+8 w508", "")
	btnCaptureActive := gMain.AddButton("xc+16 y+10 w150 h28", "Capture Active")
	btnCaptureActive.OnEvent("Click", (*) => CaptureActiveWindow())
	btnActivateTarget := gMain.AddButton("x+10 yp w150 h28", "Activate Title")
	btnActivateTarget.OnEvent("Click", (*) => ActivateExternalWindow())
	btnMoveTarget := gMain.AddButton("x+10 yp w188 h28", "Move Title +40,+40")
	btnMoveTarget.OnEvent("Click", (*) => MoveExternalWindow())
	btnFromPoint := gMain.AddButton("xc+16 y+10 w220 h28", "Use Window From Mouse Point")
	btnFromPoint.OnEvent("Click", (*) => CaptureWindowFromPoint())
	gMain.AddText("x+12 yp w276 h34", "Reads the window under the current mouse cursor and fills the target title.")
	gWindowInfoEdit := gMain.AddEdit("xc+16 y+10 w508 h140 ReadOnly -Wrap")
	externalStatus := gMain.AddText("xc+16 y+8 w508 h28", "External status: waiting for a target title")
	gStatus["window_external"] := externalStatus
	gMain.UseGroup()
	gTabs.UseTab("Windows")

	winEventGroup := gMain.AddGroupBox("xc+16 yc+470 w1064 h140", "WinEvent (Ks.WinEvent) Window Event Subscriptions")
	gMain.UseGroup(winEventGroup)
	gMain.AddText("xc+16 yc+24 w1032 h34", "Subscribes to Active / Create / Close / Move / Minimize / Restore / TitleChange through Ks.WinEvent and logs them. Move events are counted (not logged) to avoid flooding. After starting, switch, open, close, minimize, restore, and drag windows to generate events.")
	btnStartWinEvent := gMain.AddButton("xc+16 y+10 w200 h28", "Start WinEvent Probe")
	btnStartWinEvent.OnEvent("Click", (*) => StartWinEventProbe())
	btnStopWinEvent := gMain.AddButton("x+10 yp w200 h28", "Stop WinEvent Probe")
	btnStopWinEvent.OnEvent("Click", (*) => StopWinEventProbe())
	winEventStatus := gMain.AddText("x+10 yp+4 w580 h24", "WinEvent: not started")
	gStatus["window_winevent"] := winEventStatus
	gMain.UseGroup()

	gTabs.UseTab("Pixel && Image")
	gMain.AddText("xc+16 yc+12 w1040", "The pixel helper is a borderless window with a solid background and an image fixture. Keep it fully visible and unobstructed when running these tests.")

	pixelGroup := gMain.AddGroupBox("xc+16 yc+50 w500 h380", "Pixel Helper")
	gMain.UseGroup(pixelGroup)
	gMain.AddText("xc+16 yc+24 w468 h34", "PixelGetColor samples a known region. PixelSearch looks for the sampled color. ImageSearch looks for killbill.png inside the helper.")
	btnShowPixel := gMain.AddButton("xc+16 y+10 w150 h28", "Show Pixel Helper")
	btnShowPixel.OnEvent("Click", (*) => EnsurePixelHelper(true))
	btnPixelGet := gMain.AddButton("x+12 yp w120 h28", "PixelGetColor")
	btnPixelGet.OnEvent("Click", (*) => RunPixelGetColorTest())
	btnPixelSearch := gMain.AddButton("x+12 yp w120 h28", "PixelSearch")
	btnPixelSearch.OnEvent("Click", (*) => RunPixelSearchTest())
	btnImageSearch := gMain.AddButton("xc+16 y+10 w150 h28", "ImageSearch")
	btnImageSearch.OnEvent("Click", (*) => RunImageSearchTest())
	pixelStatus := gMain.AddText("xc+16 y+12 w468 h24", "Pixel status: Not run")
	gStatus["pixel_color"] := pixelStatus
	imageStatus := gMain.AddText("xc+16 y+8 w468 h24", "Image status: Not run")
	gStatus["pixel_image"] := imageStatus

	pixelNotes := gMain.AddEdit("xc+16 y+10 w468 h112 ReadOnly -Wrap")
	pixelNotes.Value :=
	(
	"Expected behavior:`n"
	"- PixelGetColor should return a hex color for the helper's solid background region.`n"
	"- PixelSearch should find that same sampled color within the helper bounds.`n"
	"- ImageSearch should find killbill.png if the asset exists and the helper is visible.`n`n"
	"If ImageSearch fails, confirm whether the helper was obscured, scaled, or on a different monitor."
	)
	gMain.UseGroup()

	gTabs.UseTab("Clipboard")
	gMain.AddText("xc+16 yc+12 w1040", "Clipboard tests are a mix of self-validating checks and manual confirmation. Use a real external app for the image paste step if you want a final interoperability check.")

	clipGroup := gMain.AddGroupBox("xc+16 yc+50 w500 h390", "Clipboard Tests")
	gMain.UseGroup(clipGroup)
	gMain.AddText("xc+16 yc+24 w468 h34", "Text round-trip and delayed ClipWait are self-validating. Clipboard change monitoring shows whether callbacks are fired. Image copy is manual after the copy step succeeds.")
	gClipboardTextEdit := gMain.AddEdit("xc+16 y+8 w468 h92", "Clipboard probe text:`nAlpha beta gamma`nUnicode: Eesti, 日本語, emoji-free.")
	btnClipboardRoundTrip := gMain.AddButton("xc+16 y+10 w150 h28", "Text Round Trip")
	btnClipboardRoundTrip.OnEvent("Click", (*) => RunClipboardTextRoundTrip())
	btnClipWait := gMain.AddButton("x+10 yp w150 h28", "Delayed ClipWait")
	btnClipWait.OnEvent("Click", (*) => RunClipboardClipWaitTest())
	btnClipboardImage := gMain.AddButton("x+10 yp w148 h28", "Copy Image Asset")
	btnClipboardImage.OnEvent("Click", (*) => RunClipboardImageCopy())
	btnToggleMonitor := gMain.AddButton("xc+16 y+10 w150 h28", "Toggle Change Monitor")
	btnToggleMonitor.OnEvent("Click", (*) => ToggleClipboardMonitor())
	clipStatus := gMain.AddText("x+12 yp+4 w300 h24", "Clipboard status: Not run")
	gStatus["clipboard_main"] := clipStatus
	clipMonitorStatus := gMain.AddText("xc+16 y+10 w468 h24", "Clipboard monitor: disabled")
	gStatus["clipboard_monitor"] := clipMonitorStatus

	clipNotes := gMain.AddEdit("xc+16 y+10 w468 h52 ReadOnly -Wrap")
	clipNotes.Value :=
	(
		"Expected behavior:`n"
		"- Text Round Trip should preserve exact text.`n"
		"- Delayed ClipWait should return after a timer populates the clipboard.`n"
		"- Copy Image Asset should place killbill.png on the clipboard if the asset exists.`n"
		"- When the monitor is enabled, copying text in any app should increment the change counter."
	)
	gMain.UseGroup()

	gTabs.UseTab("Sound")
	gMain.AddText("xc+16 yc+12 w1040", "Use this tab to exercise sound device enumeration and default-device controls. On non-Windows platforms, differences in device backends or permissions should be logged as platform limitations rather than silent failures.")

	soundGroup := gMain.AddGroupBox("xc+16 yc+50 w500 h410", "Sound Devices")
	gMain.UseGroup(soundGroup)
	gMain.AddText("xc+16 yc+24 w468 h34", "Refresh lists the default device state and attempts to enumerate numbered devices until the API stops returning names.")
	btnSoundRefresh := gMain.AddButton("xc+16 y+10 w150 h28", "Refresh Sound")
	btnSoundRefresh.OnEvent("Click", (*) => RefreshSoundStatus())
	btnSoundBeep := gMain.AddButton("x+12 yp w150 h28", "Beep Test")
	btnSoundBeep.OnEvent("Click", (*) => RunSoundBeepTest())
	soundStatus := gMain.AddText("xc+16 y+10 w468 h24", "Sound status: Not run")
	gStatus["sound_main"] := soundStatus
	gSoundInfoEdit := gMain.AddEdit("xc+16 y+8 w468 h232 ReadOnly -Wrap")
	gMain.UseGroup()
	gTabs.UseTab("Sound")

	soundControlGroup := gMain.AddGroupBox("xc+540 yc+50 w540 h410", "Default Device Controls")
	gMain.UseGroup(soundControlGroup)
	gMain.AddText("xc+16 yc+24 w508 h34", "These controls target the default playback device. Refresh after each action to verify mute and volume state.")
	btnMute := gMain.AddButton("xc+16 y+10 w150 h28", "Mute")
	btnMute.OnEvent("Click", (*) => SetSoundMute(true))
	btnUnmute := gMain.AddButton("x+10 yp w150 h28", "Unmute")
	btnUnmute.OnEvent("Click", (*) => SetSoundMute(false))
	btnVol25 := gMain.AddButton("x+10 yp w60 h28", "25%")
	btnVol25.OnEvent("Click", (*) => SetSoundVolumeValue(25))
	btnVol50 := gMain.AddButton("x+8 yp w60 h28", "50%")
	btnVol50.OnEvent("Click", (*) => SetSoundVolumeValue(50))
	btnVol100 := gMain.AddButton("x+8 yp w52 h28", "100%")
	btnVol100.OnEvent("Click", (*) => SetSoundVolumeValue(100))
	gMain.AddText("xc+16 y+12 w120 h24", "Set volume:")
	gSoundVolumeEdit := gMain.AddEdit("x+8 yp-2 w90", "50")
	btnApplyVolume := gMain.AddButton("x+12 yp-2 w130 h28", "Apply Volume")
	btnApplyVolume.OnEvent("Click", (*) => ApplySoundVolume())
	gMain.AddText("xc+16 y+12 w508 h78", "Expected behavior:`n- Mute and Unmute should toggle the default device state.`n- Set volume should change the reported percentage.`n- Device enumeration may vary by platform.")
	soundControlStatus := gMain.AddText("xc+16 y+10 w508 h28", "Sound control status: waiting")
	gStatus["sound_control"] := soundControlStatus
	gMain.UseGroup()

	gTabs.UseTab()
	gMain.AddText("x20 y706 w1120", "Activity Log")
	gLogEdit := gMain.AddEdit("x20 y730 w1120 h150 ReadOnly -Wrap")
}

RegisterInputProbes() {
	Hotkey("^!1", (*) => HotkeyProbe("^!1 / Ctrl+Alt+1"))
	Hotkey("#^9", (*) => HotkeyProbe("#^9 / Win-or-Cmd+Ctrl+9"))
	Hotkey("#!0", (*) => HotkeyProbe("#!0 / Win-or-Cmd+Alt+0"))
}

UpdateEnvironmentSummary() {
	global gStatus

	summary :=
	(
	"Platform: " A_OSVersion "`n"
	"Script dir: " A_ScriptDir "`n"
	"Desktop: " A_Desktop "`n"
	"Temp: " A_Temp "`n"
	"Image fixture: " gPixelAssetPath "`n"
	"Image fixture exists: " (FileExist(gPixelAssetPath) ? "yes" : "no") "`n`n"
	"Before marking failures, confirm platform permissions:`n"
	"- macOS: Accessibility, Input Monitoring, and Screen Recording for the host process.`n"
	"- Linux Wayland: compositor/portal policies for focus, synthetic input, and screenshots.`n"
	"- Linux X11: confirm the target desktop environment/window manager.`n"
	"- Windows: confirm the suite and the target app run at the same integrity level."
	)
	gStatus["overview_env"].Value := summary
}

AppendLog(message) {
	global gLogEdit, gLogText

	timeStamp := FormatTime(, "yyyy-MM-dd HH:mm:ss")
	gLogText .= (gLogText = "" ? "" : "`r`n") "[" timeStamp "] " message
	gLogEdit.Value := gLogText
	; Setting .Value replaces the whole text and leaves the view at the top, so pin it to the newest
	; line with WM_VSCROLL/SB_BOTTOM after the edit has processed its replaced text.
	PostMessage(0x115, 7, 0, gLogEdit)
}

ClearLog() {
	global gLogText, gLogEdit

	gLogText := ""
	gLogEdit.Value := ""
	AppendLog("Log cleared.")
}

SetStatus(key, text) {
	global gStatus

	if gStatus.Has(key)
		gStatus[key].Value := text
}

ResetStatuses() {
	SetStatus("input_send", "Status: Not run")
	SetStatus("input_hotkey", "Hotkey status: waiting for Ctrl+Alt+1")
	SetStatus("input_hotstring", "Hotstring status: waiting for the 3 probe cases")
	SetStatus("input_hook", "InputHook status: idle")
	SetStatus("input_mouse", "Mouse hook: idle")
	SetStatus("input_mouse_readout", "Start, then click / wheel / move the mouse to see the last event and live counts.")
	SetStatus("window_helper", "Helper status: Not run")
	SetStatus("window_external", "External status: waiting for a target title")
	SetStatus("pixel_color", "Pixel status: Not run")
	SetStatus("pixel_image", "Image status: Not run")
	SetStatus("clipboard_main", "Clipboard status: Not run")
	SetStatus("clipboard_monitor", "Clipboard monitor: " (gClipboardMonitorEnabled ? "enabled" : "disabled"))
	SetStatus("sound_main", "Sound status: Not run")
	SetStatus("sound_control", "Sound control status: waiting")
	SetStatus("window_winevent", "WinEvent: not started")
	AppendLog("Status text reset.")
}

; Single callback for every WinEvent subscription. The event kind is read from hook.EventType,
; so one handler covers Active/Create/Close/Move/Minimize/Restore/TitleChange. Move fires very
; frequently (once per drag step), so it is counted and surfaced in the status line rather than
; written to the log, while every other event is logged with the affected window's title.
OnWinEvent(hook, hwnd, dwmsEventTime) {
	global gWinEventCount, gWinEventMoveCount

	evType := hook.EventType

	if (evType = "Move") {
		gWinEventMoveCount++
		SetStatus("window_winevent", "WinEvent: " gWinEventCount " events, " gWinEventMoveCount " moves (last hwnd " Format("0x{:X}", hwnd) ")")
		return
	}

	gWinEventCount++

	; Look up the title by the *pure* window id (integer), not an "ahk_id <id>" string: the integer form
	; matches regardless of A_DetectHiddenWindows, so hidden helper windows don't raise a "window not found"
	; error. Even so, a window seen by Create/Close may already be gone by the time the callback runs, so the
	; lookup is wrapped to keep the probe from ever throwing.
	title := "<n/a>"
	if (evType != "Close") {
		try
			title := WinGetTitle(hwnd)
		catch
			title := "<unavailable>"
	}

	AppendLog("WinEvent " evType ": hwnd=" Format("0x{:X}", hwnd) " title=" (title = "" ? "<none>" : title))
	SetStatus("window_winevent", "WinEvent: " gWinEventCount " events, " gWinEventMoveCount " moves (last " evType ")")
}

StartWinEventProbe() {
	global gWinEventHooks, gWinEventCount, gWinEventMoveCount

	StopWinEventProbe()
	gWinEventCount := 0
	gWinEventMoveCount := 0

	try {
		gWinEventHooks.Push(WinEvent.Active(OnWinEvent))
		gWinEventHooks.Push(WinEvent.Create(OnWinEvent))
		gWinEventHooks.Push(WinEvent.Close(OnWinEvent))
		gWinEventHooks.Push(WinEvent.Move(OnWinEvent))
		gWinEventHooks.Push(WinEvent.Minimize(OnWinEvent))
		gWinEventHooks.Push(WinEvent.Restore(OnWinEvent))
		gWinEventHooks.Push(WinEvent.TitleChange(OnWinEvent))
		SetStatus("window_winevent", "WinEvent: probe started — switch, open, close, and drag windows")
		AppendLog("WinEvent probe started (" gWinEventHooks.Length " subscriptions). Activate, open, close, minimize, restore, and drag windows.")
	} catch as err {
		SetStatus("window_winevent", "WinEvent: BLOCKED/ERROR")
		AppendLog("WinEvent probe failed: " err.Message)
	}
}

StopWinEventProbe() {
	global gWinEventHooks

	if (gWinEventHooks.Length = 0)
		return

	for hook in gWinEventHooks {
		try hook.Stop()
	}

	gWinEventHooks := []
	SetStatus("window_winevent", "WinEvent: stopped")
	AppendLog("WinEvent probe stopped.")
}

PrepareSendTarget() {
	global gMain, gSendTarget

	gSendTarget.Value := ""
	WinActivate("ahk_id " gMain.Hwnd)
	Sleep(120)
	gSendTarget.Focus()
	Sleep(120)
}

RunSendVariant(mode) {
	global gSendTarget

	expected := "Alpha 123`nLiteral braces {Blind}{Text}`n"

	try {
		PrepareSendTarget()

		switch mode {
			case "Send":
				Send("{Text}Alpha 123`nLiteral braces {Blind}{Text}`n")
			case "SendText":
				SendText("Alpha 123`r`nLiteral braces {Blind}{Text}`r`n")
			case "SendInput":
				SendInput("{Text}Alpha 123`nLiteral braces {Blind}{Text}`n")
			case "SendPlay":
				SendPlay("{Text}Alpha 123`nLiteral braces {Blind}{Text}`n")
			default:
				throw Error("Unknown send mode: " mode)
		}

		Sleep(250)
		actual := NormalizeNewlines(gSendTarget.Value)

		if (actual = NormalizeNewlines(expected)) {
			SetStatus("input_send", mode " status: PASS")
			AppendLog(mode " produced the expected text in the suite-owned edit.")
		} else {
			SetStatus("input_send", mode " status: FAIL")
			AppendLog(mode " mismatch. Expected <" expected "> but saw <" actual ">.")
		}
	} catch as err {
		SetStatus("input_send", mode " status: BLOCKED/ERROR")
		AppendLog(mode " threw an error: " err.Message)
	}
}

RunSendScenario(mode, expected, label := "") {
	global gSendTarget

	try {
		PrepareSendTarget()

		switch mode {
			case "Send":
				Send("{Text}" expected)
			case "SendText":
				SendText(expected)
			case "SendInput":
				SendInput("{Text}" expected)
			case "SendPlay":
				SendPlay("{Text}" expected)
			default:
				throw Error("Unknown send mode: " mode)
		}

		Sleep(250)
		actual := NormalizeNewlines(gSendTarget.Value)
		expectedNorm := NormalizeNewlines(expected)
		labelText := label != "" ? label : mode

		if (actual = expectedNorm) {
			SetStatus("input_send", labelText " status: PASS")
			AppendLog(labelText " produced the expected text in the suite-owned edit.")
		} else {
			SetStatus("input_send", labelText " status: FAIL")
			AppendLog(labelText " mismatch. Expected <" expectedNorm "> but saw <" actual ">.")
		}
	} catch as err {
		labelText := label != "" ? label : mode
		SetStatus("input_send", labelText " status: BLOCKED/ERROR")
		AppendLog(labelText " threw an error: " err.Message)
	}
}

NormalizeNewlines(text) {
	text := StrReplace(text, "`r`n", "`n")
	text := StrReplace(text, "`r", "`n")
	return text
}

HotkeyProbe(label, *) {
	global gHotkeyHitCount

	gHotkeyHitCount += 1
	SetStatus("input_hotkey", "Hotkey status: PASS via " label " (" gHotkeyHitCount " hit" (gHotkeyHitCount = 1 ? "" : "s") ")")
	AppendLog("Hotkey probe fired via " label ". Total hits: " gHotkeyHitCount ".")
}

ResetHotkeyProbe() {
	global gHotkeyHitCount

	gHotkeyHitCount := 0
	SetStatus("input_hotkey", "Hotkey status: waiting for the matrix hotkeys")
	AppendLog("Hotkey counter reset.")
}

ResetHotstringProbe() {
	global gHotstringHitCount, gHotstringTarget

	gHotstringHitCount := 0
	gHotstringTarget.Value := HotstringProbeInstructions()
	SetStatus("input_hotstring", "Hotstring status: waiting for the 3 probe cases")
	AppendLog("Hotstring probe reset.")
}

ValidateHotstringProbe() {
	global gHotstringTarget

	missing := []

	for _, probe in ["KEYSHARP-SUITE [A_EndChar=<blank>]", "ENDCHAR-OK [A_EndChar=Space]", "INSIDE-WORD-OK [A_EndChar=Space]"] {
		if !InStr(gHotstringTarget.Value, probe)
			missing.Push(probe)
	}

	if missing.Length = 0 {
		SetStatus("input_hotstring", "Hotstring status: PASS (3/3 cases)")
		AppendLog("Hotstring validation passed for kssuite, ksend<Space>, and prefixksword<Space>.")
	} else {
		SetStatus("input_hotstring", "Hotstring status: FAIL")
		AppendLog("Hotstring validation failed. Missing: " JoinProbeNames(missing) ". Current edit content: " gHotstringTarget.Value)
	}
}

HotstringProbeInstructions() {
	return "Run these in order on separate lines:`n1. kssuite`n2. ksend<Space>`n3. prefixksword<Space>`n`nExpected markers:`nKEYSHARP-SUITE [A_EndChar=<blank>]`nENDCHAR-OK [A_EndChar=Space]`nINSIDE-WORD-OK [A_EndChar=Space]"
}

JoinProbeNames(items) {
	text := ""

	for index, item in items
		text .= (index = 1 ? "" : ", ") item

	return text
}

StartInputHookProbe() {
	global gInputHookObj, gInputHookLastText, gInputHookLastReason, gInputHookActual

	try {
		gInputHookLastText := ""
		gInputHookLastReason := ""
		gInputHookActual.Value := ""
		gInputHookObj := InputHook("V")
		gInputHookObj.KeyOpt("{Enter}", "E")
		gInputHookObj.OnEnd := InputHookEnded
		gInputHookObj.Start()
		SetStatus("input_hook", "InputHook status: capturing, type the expected text then press Enter")
		AppendLog("InputHook started.")
	} catch as err {
		SetStatus("input_hook", "InputHook status: BLOCKED/ERROR")
		AppendLog("InputHook start failed: " err.Message)
	}
}

InputHookEnded(hook) {
	global gInputHookLastText, gInputHookLastReason, gInputHookActual

	gInputHookLastText := hook.Input
	gInputHookLastReason := hook.EndReason
	gInputHookActual.Value := gInputHookLastText
	SetStatus("input_hook", "InputHook status: ended with reason " gInputHookLastReason)
	AppendLog("InputHook ended. Reason=" gInputHookLastReason ", Input=" gInputHookLastText)
}

ValidateInputHookProbe() {
	global gInputHookExpected, gInputHookLastText, gInputHookLastReason

	expected := gInputHookExpected.Value

	if (gInputHookLastText = expected && gInputHookLastReason != "") {
		SetStatus("input_hook", "InputHook status: PASS")
		AppendLog("InputHook validation passed.")
	} else {
		SetStatus("input_hook", "InputHook status: FAIL")
		AppendLog("InputHook validation failed. Expected <" expected "> but saw <" gInputHookLastText "> with reason <" gInputHookLastReason ">.")
	}
}

StartMouseHookProbe() {
	global gMouseHookObj, gMouseDownCount, gMouseUpCount, gMouseMoveCount

	try {
		if (IsObject(gMouseHookObj) && gMouseHookObj.InProgress)
			gMouseHookObj.Stop()

		gMouseDownCount := 0
		gMouseUpCount := 0
		gMouseMoveCount := 0
		; "V" keeps keystrokes/clicks visible (non-suppressing) so the harness stays usable.
		gMouseHookObj := InputHook("V")
		gMouseHookObj.OnMouseDown := MouseHookDown
		gMouseHookObj.OnMouseUp := MouseHookUp
		gMouseHookObj.OnMouseMove := MouseHookMove
		gMouseHookObj.Start()
		UpdateMouseHookReadout("(waiting for mouse activity)")
		SetStatus("input_mouse", "Mouse hook: capturing. Click, wheel, and move the mouse.")
		AppendLog("Mouse InputHook started.")
	} catch Error as err {
		SetStatus("input_mouse", "Mouse hook: BLOCKED/ERROR")
		AppendLog("Mouse InputHook start failed: " err.Message)
	}
}

StopMouseHookProbe() {
	global gMouseHookObj

	if (IsObject(gMouseHookObj) && gMouseHookObj.InProgress) {
		gMouseHookObj.VisibleMouseMove := true ; Make sure movement is restored on stop.
		gMouseHookObj.Stop()
	}

	SetStatus("input_mouse", "Mouse hook: stopped")
	AppendLog("Mouse InputHook stopped.")
}

MouseHookDown(hook, button, x, y) {
	global gMouseDownCount

	gMouseDownCount++
	UpdateMouseHookReadout("Down " button " @ " x "," y)
}

MouseHookUp(hook, button, x, y) {
	global gMouseUpCount

	gMouseUpCount++
	UpdateMouseHookReadout("Up " button " @ " x "," y)
}

MouseHookMove(hook, x, y) {
	global gMouseMoveCount

	gMouseMoveCount++

	; Movement fires continuously; refresh the readout only periodically so the GUI stays responsive.
	if (Mod(gMouseMoveCount, 10) = 0)
		UpdateMouseHookReadout("Move @ " x "," y)
}

UpdateMouseHookReadout(lastEvent) {
	global gMouseDownCount, gMouseUpCount, gMouseMoveCount

	SetStatus("input_mouse_readout", "Last: " lastEvent "   |   Down=" gMouseDownCount " Up=" gMouseUpCount " Move=" gMouseMoveCount)
}

TestBlockMoveProbe() {
	global gMouseHookObj

	if !(IsObject(gMouseHookObj) && gMouseHookObj.InProgress) {
		SetStatus("input_mouse", "Mouse hook: start it first")
		return
	}

	gMouseHookObj.VisibleMouseMove := false
	SetStatus("input_mouse", "Mouse hook: movement BLOCKED for 2s (cursor should freeze, then recover).")
	AppendLog("Mouse movement suppression engaged (VisibleMouseMove:=false) for 2s.")
	SetTimer(UnblockMoveProbe, -2000) ; Auto-revert so the tester is never locked out.
}

UnblockMoveProbe() {
	global gMouseHookObj

	if (IsObject(gMouseHookObj) && gMouseHookObj.InProgress)
		gMouseHookObj.VisibleMouseMove := true

	SetStatus("input_mouse", "Mouse hook: movement restored")
	AppendLog("Mouse movement suppression released (VisibleMouseMove:=true).")
}

ToggleBlockMButton() {
	static blocked := false
	global gMouseHookObj

	if !(IsObject(gMouseHookObj) && gMouseHookObj.InProgress) {
		SetStatus("input_mouse", "Mouse hook: start it first")
		return
	}

	blocked := !blocked
	; +S suppresses the middle button like a suppressed keystroke; +V makes it visible again.
	gMouseHookObj.KeyOpt("{MButton}", blocked ? "+S" : "+V")
	SetStatus("input_mouse", "Mouse hook: MButton " (blocked ? "SUPPRESSED (middle-click should do nothing)" : "visible"))
	AppendLog("MButton suppression " (blocked ? "enabled" : "disabled") " via KeyOpt.")
}

EnsureWindowHelper(showWindow := true) {
	global gWindowHelper

	if !IsObject(gWindowHelper) {
		gWindowHelper := Gui("+AlwaysOnTop", "KS Manual Suite Target")
		gWindowHelper.SetFont("s10", "Segoe UI")
		gWindowHelper.AddText("xm ym w320", "This helper window exists for Win* tests.")
		helperEdit := gWindowHelper.AddEdit("xm y+12 w320 h90")
		helperEdit.Value := "The suite will activate and move this window."
		gWindowHelper.AddText("xm y+12 w320", "Keep this window visible for the self-owned Win* automation pass.")
	}

	if showWindow
		gWindowHelper.Show("x80 y120 w360 h220")
}

HideWindowHelper() {
	global gWindowHelper

	if IsObject(gWindowHelper) {
		gWindowHelper.Hide()
		AppendLog("Window helper hidden.")
	}
}

ShowHelperWindows() {
	EnsureWindowHelper(true)
	EnsurePixelHelper(true)
	AppendLog("Helper windows shown.")
}

RunWindowHelperAutomation() {
	global gWindowHelper, gWindowHelperOriginalPos

	try {
		EnsureWindowHelper(true)
		title := "KS Manual Suite Target"
		hwnd := WinExist(title)

		if !hwnd
			throw Error("Helper window was not found.")

		WinGetPos(&x1, &y1, &w1, &h1, "ahk_id " hwnd)
		gWindowHelperOriginalPos["x"] := x1
		gWindowHelperOriginalPos["y"] := y1
		gWindowHelperOriginalPos["w"] := w1
		gWindowHelperOriginalPos["h"] := h1

		WinActivate("ahk_id " hwnd)
		WinWaitActive("ahk_id " hwnd, , 2)
		WinMove(x1 + 40, y1 + 40, w1, h1, "ahk_id " hwnd)
		Sleep(200)
		WinGetPos(&x2, &y2, &w2, &h2, "ahk_id " hwnd)
		WinMove(x1, y1, w1, h1, "ahk_id " hwnd)

		if (x2 = x1 + 40 && y2 = y1 + 40) {
			SetStatus("window_helper", "Helper status: PASS")
			AppendLog("Helper Win* automation passed. Window activated and moved as expected.")
		} else {
			SetStatus("window_helper", "Helper status: FAIL")
			AppendLog("Helper Win* automation failed. Expected moved pos " (x1 + 40) "," (y1 + 40) " but saw " x2 "," y2 ".")
		}
	} catch as err {
		SetStatus("window_helper", "Helper status: BLOCKED/ERROR")
		AppendLog("Helper Win* automation error: " err.Message)
	}
}

CaptureActiveWindow() {
	global gWindowTitleEdit, gWindowInfoEdit

	try {
		hwnd := WinExist("A")
		title := WinGetTitle("ahk_id " hwnd)
		className := WinGetClass("ahk_id " hwnd)
		WinGetPos(&x, &y, &w, &h, "ahk_id " hwnd)
		gWindowTitleEdit.Value := title
		gWindowInfoEdit.Value := "Title: " title "`r`nClass: " className "`r`nHwnd: " hwnd "`r`nPos: " x "," y "  Size: " w "x" h
		SetStatus("window_external", "External status: captured active window")
		AppendLog("Captured active window: " title " [" className "]")
	} catch as err {
		SetStatus("window_external", "External status: BLOCKED/ERROR")
		AppendLog("Capture active window failed: " err.Message)
	}
}

CaptureWindowFromPoint() {
	global gWindowTitleEdit, gWindowInfoEdit

	try {
		CoordMode "Mouse", "Screen"
		MouseGetPos(&mx, &my)
		hwnd := WinFromPoint(mx, my)

		if !hwnd
			throw Error("WinFromPoint returned no hwnd for " mx "," my ".")

		title := WinGetTitle("ahk_id " hwnd)
		className := WinGetClass("ahk_id " hwnd)
		WinGetPos(&x, &y, &w, &h, "ahk_id " hwnd)
		gWindowTitleEdit.Value := title
		gWindowInfoEdit.Value := "From point: " mx "," my "`r`nTitle: " title "`r`nClass: " className "`r`nHwnd: " hwnd "`r`nPos: " x "," y "  Size: " w "x" h
		SetStatus("window_external", "External status: captured window from mouse point")
		AppendLog("CaptureWindowFromPoint found hwnd " hwnd " with title <" title "> at mouse point " mx "," my ".")
	} catch as err {
		SetStatus("window_external", "External status: BLOCKED/ERROR")
		AppendLog("CaptureWindowFromPoint failed: " err.Message)
	}
}

ActivateExternalWindow() {
	global gWindowTitleEdit

	title := Trim(gWindowTitleEdit.Value)

	if (title = "") {
		SetStatus("window_external", "External status: enter or capture a title first")
		return
	}

	try {
		WinActivate(title)
		WinWaitActive(title, , 2)
		SetStatus("window_external", "External status: PASS if the requested window came to front")
		AppendLog("ActivateExternalWindow ran against title match <" title ">.")
	} catch as err {
		SetStatus("window_external", "External status: BLOCKED/ERROR")
		AppendLog("ActivateExternalWindow failed for <" title ">: " err.Message)
	}
}

MoveExternalWindow() {
	global gWindowTitleEdit, gWindowInfoEdit

	title := Trim(gWindowTitleEdit.Value)

	if (title = "") {
		SetStatus("window_external", "External status: enter or capture a title first")
		return
	}

	try {
		WinGetPos(&x1, &y1, &w1, &h1, title)
		WinMove(x1 + 40, y1 + 40, w1, h1, title)
		Sleep(200)
		WinGetPos(&x2, &y2, &w2, &h2, title)
		gWindowInfoEdit.Value := "Before: " x1 "," y1 "  " w1 "x" h1 "`r`nAfter:  " x2 "," y2 "  " w2 "x" h2
		SetStatus("window_external", "External status: PASS if the target moved by +40,+40")
		AppendLog("MoveExternalWindow ran against title match <" title ">. Before=" x1 "," y1 " After=" x2 "," y2 ".")
	} catch as err {
		SetStatus("window_external", "External status: BLOCKED/ERROR")
		AppendLog("MoveExternalWindow failed for <" title ">: " err.Message)
	}
}

EnsurePixelHelper(showWindow := true) {
	global gPixelHelper, gPixelAssetPath

	if !IsObject(gPixelHelper) {
		gPixelHelper := Gui("+ToolWindow +AlwaysOnTop", "KS Pixel Target")
		gPixelHelper.BackColor := "AA2233"
		gPixelHelper.SetFont("s10 cFFFFFF", "Segoe UI")
		gPixelHelper.AddText("x16 y16 w220 h24 BackgroundTrans", "KS Pixel Target")
		if FileExist(gPixelAssetPath)
			gPixelHelper.AddPicture("x20 y60", gPixelAssetPath)
		gPixelHelper.AddText("x160 y48 w184 h160 BackgroundTrans", "Keep this helper fully visible.`n`nImageSearch looks for killbill.png.`n`nPixel tests sample the solid background.")
	}

	if showWindow
		gPixelHelper.Show("x760 y120 w372 h280")
}

RunPixelGetColorTest() {
	global gPixelHelper

	try {
		prevMode := CoordMode("Pixel", "Screen")
		EnsurePixelHelper(true)
		WinGetPos(&x, &y, &w, &h, "KS Pixel Target")
		sampleX := x + 30
		sampleY := y + h - 30
		color := PixelGetColor(sampleX, sampleY)
		SetStatus("pixel_color", "Pixel status: PASS if the color below matches the red helper background (0xAA2233): " color)
		AppendLog("PixelGetColor sampled (" sampleX "," sampleY ") -> " color)
	} catch as err {
		SetStatus("pixel_color", "Pixel status: BLOCKED/ERROR")
		AppendLog("PixelGetColor test failed: " err.Message)
	} finally {
		CoordMode("Pixel", prevMode)
	}
}

RunPixelSearchTest() {
	try {
		prevMode := CoordMode("Pixel", "Screen")
		EnsurePixelHelper(true)
		WinGetPos(&x, &y, &w, &h, "KS Pixel Target")
		sampleX := x + 30
		sampleY := y + 30
		color := PixelGetColor(sampleX, sampleY)
		PixelSearch(&foundX, &foundY, x, y, x + w - 1, y + h - 1, color)

		if (foundX >= x && foundX <= x + w && foundY >= y && foundY <= y + h) {
			SetStatus("pixel_color", "Pixel status: PASS")
			AppendLog("PixelSearch found color " color " at " foundX "," foundY " inside the helper bounds.")
		} else {
			SetStatus("pixel_color", "Pixel status: FAIL")
			AppendLog("PixelSearch returned out-of-bounds coordinates for color " color ".")
		}
	} catch as err {
			SetStatus("pixel_color", "Pixel status: BLOCKED/ERROR")
			AppendLog("PixelSearch test failed: " err.Message)
	} finally {
		CoordMode("Pixel", prevMode)
	}
}

RunImageSearchTest() {
	global gPixelAssetPath

	if !FileExist(gPixelAssetPath) {
		SetStatus("pixel_image", "Image status: BLOCKED - killbill.png not found")
		AppendLog("ImageSearch skipped because the fixture does not exist: " gPixelAssetPath)
		return
	}

	try {
		prevMode := CoordMode("Pixel", "Screen")
		EnsurePixelHelper(true)
		WinGetPos(&x, &y, &w, &h, "KS Pixel Target")
		ImageSearch(&foundX, &foundY, x, y, x + w - 1, y + h - 1, "*1 " . gPixelAssetPath)

		if (foundX != "" && foundY != "" && foundX >= x && foundX <= x + w && foundY >= y && foundY <= y + h) {
			SetStatus("pixel_image", "Image status: PASS")
			AppendLog("ImageSearch found the fixture at " foundX "," foundY ".")
		} else if (foundX = "" || foundY = "") {
			SetStatus("pixel_image", "Image status: FAIL")
			AppendLog("ImageSearch did not find the fixture within the helper window.")
		} else {
			SetStatus("pixel_image", "Image status: FAIL")
			AppendLog("ImageSearch returned out-of-bounds coordinates.")
		}
	} catch as err {
		SetStatus("pixel_image", "Image status: BLOCKED/ERROR")
		AppendLog("ImageSearch test failed: " err.Message)
	} finally {
		CoordMode("Pixel", prevMode)
	}
}

RunClipboardTextRoundTrip() {
	global gClipboardTextEdit

	try {
		SetTimer(PopulateClipboardTimer, 0)
		expected := gClipboardTextEdit.Value
		A_Clipboard := expected
		if !WaitForClipboardValue(expected, 2000)
			throw Error("Clipboard did not round-trip to the expected text within 2 seconds.")

		if (A_Clipboard = expected) {
			SetStatus("clipboard_main", "Clipboard status: PASS")
			AppendLog("Clipboard text round-trip passed.")
		} else {
			SetStatus("clipboard_main", "Clipboard status: FAIL")
			AppendLog("Clipboard text mismatch. Expected <" expected "> but saw <" A_Clipboard ">.")
		}
	} catch as err {
		SetStatus("clipboard_main", "Clipboard status: BLOCKED/ERROR")
		AppendLog("Clipboard text round-trip failed: " err.Message)
	}
}

RunClipboardClipWaitTest() {
	global gClipboardClipWaitRunning, gClipboardDelayedPayload

	if gClipboardClipWaitRunning {
		SetStatus("clipboard_main", "Clipboard status: waiting for the previous ClipWait run")
		AppendLog("Delayed ClipWait ignored because a prior run is still active.")
		return
	}

	gClipboardClipWaitRunning := true

	try {
		SetTimer(PopulateClipboardTimer, 0)
		gClipboardDelayedPayload := "Delayed clipboard payload [" A_TickCount "]"
		SetTimer(PopulateClipboardTimer, -500)
		if !WaitForClipboardValue(gClipboardDelayedPayload, 2000)
			throw Error("Clipboard did not reach the delayed payload within 2 seconds.")

		if (A_Clipboard = gClipboardDelayedPayload) {
			SetStatus("clipboard_main", "Clipboard status: PASS (ClipWait)")
			AppendLog("Delayed ClipWait test passed.")
		} else {
			SetStatus("clipboard_main", "Clipboard status: FAIL (ClipWait)")
			AppendLog("Delayed ClipWait test failed. Clipboard now contains <" A_Clipboard ">.")
		}
	} catch as err {
		SetStatus("clipboard_main", "Clipboard status: BLOCKED/ERROR")
		AppendLog("Delayed ClipWait test failed: " err.Message)
	} finally {
		SetTimer(PopulateClipboardTimer, 0)
		gClipboardDelayedPayload := ""
		gClipboardClipWaitRunning := false
	}
}

PopulateClipboardTimer() {
	global gClipboardDelayedPayload
	A_Clipboard := gClipboardDelayedPayload
}

WaitForClipboardValue(expected, timeoutMs, pollMs := 50) {
	deadline := A_TickCount + timeoutMs

	while (A_TickCount < deadline) {
		if (A_Clipboard = expected)
			return true

		Sleep(Min(pollMs, Max(1, deadline - A_TickCount)))
	}

	return A_Clipboard = expected
}

RunClipboardImageCopy() {
	global gPixelAssetPath

	if !FileExist(gPixelAssetPath) {
		SetStatus("clipboard_main", "Clipboard status: BLOCKED - killbill.png not found")
		AppendLog("Clipboard image copy skipped because the fixture does not exist: " gPixelAssetPath)
		return
	}

	try {
		CopyImageToClipboard(gPixelAssetPath)
		ClipWait(2, true)
		SetStatus("clipboard_main", "Clipboard status: PASS if you can now paste the image into another app")
		AppendLog("Image copied to clipboard from " gPixelAssetPath ".")
	} catch as err {
		SetStatus("clipboard_main", "Clipboard status: BLOCKED/ERROR")
		AppendLog("Clipboard image copy failed: " err.Message)
	}
}

ToggleClipboardMonitor() {
	global gClipboardMonitorEnabled

	try {
		if gClipboardMonitorEnabled {
			OnClipboardChange(ClipboardChanged, 0)
			gClipboardMonitorEnabled := false
			SetStatus("clipboard_monitor", "Clipboard monitor: disabled")
			AppendLog("Clipboard change monitor disabled.")
		} else {
			OnClipboardChange(ClipboardChanged, 1)
			gClipboardMonitorEnabled := true
			SetStatus("clipboard_monitor", "Clipboard monitor: enabled (changes seen: " gClipboardChangeCount ")")
			AppendLog("Clipboard change monitor enabled.")
		}
	} catch as err {
		SetStatus("clipboard_monitor", "Clipboard monitor: BLOCKED/ERROR")
		AppendLog("Toggling clipboard monitor failed: " err.Message)
	}
}

RefreshSoundStatus() {
	global gSoundInfoEdit, gSoundVolumeEdit

	try {
		defaultName := SoundGetName()
		defaultVolume := SoundGetVolume()
		defaultMute := SoundGetMute()
		report := "Default device`r`n"
		report .= "Name: " defaultName "`r`n"
		report .= "Volume: " defaultVolume "`r`n"
		report .= "Muted: " defaultMute "`r`n`r`n"
		report .= "Enumerated devices`r`n"

		foundAny := false

		Loop 12 {
			try {
				name := SoundGetName(A_Index)

				if (name = "")
					break

				vol := SoundGetVolume(A_Index)
				mute := SoundGetMute(A_Index)
				report .= A_Index ". " name " | Vol=" vol " | Mute=" mute "`r`n"
				foundAny := true
			} catch {
				break
			}
		}

		if !foundAny
			report .= "(No numbered devices returned beyond the default device.)"

		gSoundInfoEdit.Value := report
		gSoundVolumeEdit.Value := defaultVolume
		SetStatus("sound_main", "Sound status: PASS")
		AppendLog("Sound status refreshed. Default device: " defaultName ".")
	} catch as err {
		SetStatus("sound_main", "Sound status: BLOCKED/ERROR")
		AppendLog("RefreshSoundStatus failed: " err.Message)
	}
}

SetSoundMute(newState) {
	try {
		SoundSetMute(newState)
		Sleep(100)
		current := SoundGetMute()
		SetStatus("sound_control", "Sound control status: mute=" current)
		AppendLog("SetSoundMute(" newState ") executed. Current mute state: " current ".")
		RefreshSoundStatus()
	} catch as err {
		SetStatus("sound_control", "Sound control status: BLOCKED/ERROR")
		AppendLog("SetSoundMute failed: " err.Message)
	}
}

SetSoundVolumeValue(newValue) {
	try {
		SoundSetVolume(newValue)
		Sleep(100)
		current := SoundGetVolume()
		gSoundVolumeEdit.Value := current
		SetStatus("sound_control", "Sound control status: volume=" current)
		AppendLog("SetSoundVolume(" newValue ") executed. Current volume: " current ".")
		RefreshSoundStatus()
	} catch as err {
		SetStatus("sound_control", "Sound control status: BLOCKED/ERROR")
		AppendLog("SetSoundVolumeValue failed: " err.Message)
	}
}

ApplySoundVolume() {
	global gSoundVolumeEdit

	try {
		target := gSoundVolumeEdit.Value + 0

		if (target < 0 || target > 100)
			throw Error("Volume must be between 0 and 100.")

		SetSoundVolumeValue(target)
	} catch as err {
		SetStatus("sound_control", "Sound control status: BLOCKED/ERROR")
		AppendLog("ApplySoundVolume failed: " err.Message)
	}
}

RunSoundBeepTest() {
	try {
		SoundBeep(900, 300)
		Sleep(80)
		SoundBeep(1100, 300)
		SetStatus("sound_main", "Sound status: PASS if two beeps were audible")
		AppendLog("SoundBeep test executed.")
	} catch as err {
		SetStatus("sound_main", "Sound status: BLOCKED/ERROR")
		AppendLog("RunSoundBeepTest failed: " err.Message)
	}
}

ClipboardChanged(*) {
	global gClipboardChangeCount, gClipboardMonitorEnabled

	gClipboardChangeCount += 1
	if gClipboardMonitorEnabled
		SetStatus("clipboard_monitor", "Clipboard monitor: enabled (changes seen: " gClipboardChangeCount ")")
	AppendLog("Clipboard change callback fired. Count=" gClipboardChangeCount ".")
}

HotstringProbe(trigger, output) {
	global gHotstringHitCount

	gHotstringHitCount += 1
	SetStatus("input_hotstring", "Hotstring status: triggered via " trigger " (" gHotstringHitCount " hit" (gHotstringHitCount = 1 ? "" : "s") ")")
	AppendLog("Hotstring probe fired via " trigger ".")
	SendText(output " [A_EndChar=" DescribeEndChar(A_EndChar) "]")
}

DescribeEndChar(endChar) {
	if (endChar = "")
		return "<blank>"

	if (endChar = " ")
		return "Space"

	if (endChar = "`t")
		return "Tab"

	if (endChar = "`n" || endChar = "`r")
		return "Enter"

	return endChar
}

:*:kssuite::
{
	HotstringProbe("kssuite", "KEYSHARP-SUITE")
}

::ksend::
{
	HotstringProbe("ksend", "ENDCHAR-OK")
}

:?:ksword::
{
	HotstringProbe("ksword", "INSIDE-WORD-OK")
}
