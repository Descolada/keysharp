#Requires AutoHotkey v2.0
#SingleInstance Force

import * from Ks

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
	gMain.AddText("x36 y86 w1040", "Use this suite on each target platform separately: Windows, Linux X11, Linux Wayland, and macOS. Record blocked-by-policy cases distinctly from true failures.")
	gMain.AddText("x36 y108 w1040", "Recommended order: 1) run self-validating tests, 2) run external-window tests, 3) repeat after granting any required OS permissions.")
	envInfo := gMain.AddEdit("x36 y138 w800 h140 ReadOnly -Wrap")
	gStatus["overview_env"] := envInfo

	btnCreateHelpers := gMain.AddButton("x860 y138 w240 h30", "Show Helper Windows")
	btnCreateHelpers.OnEvent("Click", (*) => ShowHelperWindows())
	btnResetStatuses := gMain.AddButton("x860 y178 w240 h30", "Reset Status Text")
	btnResetStatuses.OnEvent("Click", (*) => ResetStatuses())
	btnClearLog := gMain.AddButton("x860 y218 w240 h30", "Clear Log")
	btnClearLog.OnEvent("Click", (*) => ClearLog())
	btnActiveInfo := gMain.AddButton("x860 y258 w240 h30", "Capture Active Window")
	btnActiveInfo.OnEvent("Click", (*) => CaptureActiveWindow())

	overviewNotes := gMain.AddEdit("x36 y300 w1064 h300 ReadOnly -Wrap")
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
	gMain.AddText("x36 y86 w1040", "These tests are intended to be mostly self-validating. If the test updates the target edit or status line correctly, mark it as good. If focus or permissions interfere, note the behavior in the log.")

	sendGroup := gMain.AddGroupBox("x36 y116 w500 h430", "Send Variants")
	gMain.AddText("x52 y146 w468", "Each button clears the target edit, focuses it, sends text, and validates the exact resulting text.")
	gSendTarget := gMain.AddEdit("x52 y176 w468 h120 -Wrap")
	btnSend := gMain.AddButton("x52 y312 w110 h28", "Run Send()")
	btnSend.OnEvent("Click", (*) => RunSendVariant("Send"))
	btnSendText := gMain.AddButton("x172 y312 w110 h28", "Run SendText()")
	btnSendText.OnEvent("Click", (*) => RunSendVariant("SendText"))
	btnSendInput := gMain.AddButton("x292 y312 w110 h28", "Run SendInput()")
	btnSendInput.OnEvent("Click", (*) => RunSendVariant("SendInput"))
	btnSendPlay := gMain.AddButton("x412 y312 w110 h28", "Run SendPlay()")
	btnSendPlay.OnEvent("Click", (*) => RunSendVariant("SendPlay"))
	btnSendUnicode := gMain.AddButton("x52 y352 w150 h28", "Unicode SendText")
	btnSendUnicode.OnEvent("Click", (*) => RunSendScenario("SendText", "Mägi, Köln, São Paulo`n", "Unicode SendText"))
	btnSendEmoji := gMain.AddButton("x214 y352 w150 h28", "Emoji SendText")
	btnSendEmoji.OnEvent("Click", (*) => RunSendScenario("SendText", "Faces: 😀 😎 🚀`n", "Emoji SendText"))
	btnSendMixed := gMain.AddButton("x376 y352 w146 h28", "Mixed Unicode")
	btnSendMixed.OnEvent("Click", (*) => RunSendScenario("SendInput", "Mixed: ääkkönen, 日本語, 😀`n", "Mixed Unicode SendInput"))
	gMain.AddText("x52 y390 w468 h34", "Extra coverage: Unicode, accented Latin text, CJK text, and emoji. These tests are useful for keyboard-layout and surrogate-pair issues.")
	sendStatus := gMain.AddText("x52 y430 w468 h52", "Status: Not run")
	gStatus["input_send"] := sendStatus

	hotkeyGroup := gMain.AddGroupBox("x560 y116 w540 h430", "Hotkey / Hotstring / InputHook")
	gMain.AddText("x576 y146 w508", "Hotkey probe: test several modifier combinations. Hotstring probe: type kssuite into the edit below. InputHook: click Start, type the expected text, then press Enter.")
	gMain.AddText("x576 y176 w508 h40", "Modifier mapping hint: Ctrl = ^, Alt/Option = !, Shift = +, Win/Cmd = #. On macOS, the Command key is typically tested via #.")
	btnResetHotkey := gMain.AddButton("x576 y226 w170 h28", "Reset Hotkey Counter")
	btnResetHotkey.OnEvent("Click", (*) => ResetHotkeyProbe())
	gMain.AddText("x756 y232 w328", "Press: Ctrl+Alt+1, Win/Cmd+Ctrl+9, Win/Cmd+Alt+0")
	hotkeyStatus := gMain.AddText("x576 y262 w508 h36", "Hotkey status: waiting for the matrix hotkeys")
	gStatus["input_hotkey"] := hotkeyStatus

	gHotstringTarget := gMain.AddEdit("x576 y306 w508 h72")
	gHotstringTarget.Value := "Type kssuite here to test hotstring expansion."
	btnResetHotstring := gMain.AddButton("x576 y390 w170 h28", "Reset Hotstring Probe")
	btnResetHotstring.OnEvent("Click", (*) => ResetHotstringProbe())
	btnValidateHotstring := gMain.AddButton("x756 y390 w170 h28", "Validate Hotstring")
	btnValidateHotstring.OnEvent("Click", (*) => ValidateHotstringProbe())
	hotstringStatus := gMain.AddText("x576 y426 w508", "Hotstring status: waiting for kssuite")
	gStatus["input_hotstring"] := hotstringStatus

	gMain.AddText("x576 y462 w64", "Expected:")
	gInputHookExpected := gMain.AddEdit("x646 y458 w120", "abc123")
	btnStartInputHook := gMain.AddButton("x782 y458 w140 h28", "Start InputHook")
	btnStartInputHook.OnEvent("Click", (*) => StartInputHookProbe())
	btnValidateInputHook := gMain.AddButton("x934 y458 w140 h28", "Validate Hook")
	btnValidateInputHook.OnEvent("Click", (*) => ValidateInputHookProbe())
	gInputHookActual := gMain.AddEdit("x576 y498 w508 h28 ReadOnly")
	inputHookStatus := gMain.AddText("x576 y532 w508", "InputHook status: idle")
	gStatus["input_hook"] := inputHookStatus

	gTabs.UseTab("Windows")
	gMain.AddText("x36 y86 w1040", "The helper-window tests are self-validating. The external-window tools are intentionally semi-automated: point them at a real app and confirm the visible behavior.")

	helperWinGroup := gMain.AddGroupBox("x36 y116 w500 h360", "Helper Window Automation")
	gMain.AddText("x52 y146 w468", "These buttons act on a dedicated helper window owned by the suite. Use them to validate WinExist, WinActivate, WinWaitActive, WinGetPos, and WinMove without external app noise.")
	btnShowHelper := gMain.AddButton("x52 y190 w150 h28", "Show Helper")
	btnShowHelper.OnEvent("Click", (*) => EnsureWindowHelper(true))
	btnRunHelperAutomation := gMain.AddButton("x214 y190 w220 h28", "Run Helper Win* Test")
	btnRunHelperAutomation.OnEvent("Click", (*) => RunWindowHelperAutomation())
	btnHideHelper := gMain.AddButton("x446 y190 w74 h28", "Hide")
	btnHideHelper.OnEvent("Click", (*) => HideWindowHelper())
	helperStatus := gMain.AddText("x52 y236 w468 h80", "Helper status: Not run")
	gStatus["window_helper"] := helperStatus

	externalWinGroup := gMain.AddGroupBox("x560 y116 w540 h390", "External Window Tools")
	gMain.AddText("x576 y146 w508", "Use Capture Active Window to prefill the title field, or type your own title match. Activate and Move are semi-automated and should be confirmed by the tester.")
	gWindowTitleEdit := gMain.AddEdit("x576 y182 w508", "")
	btnCaptureActive := gMain.AddButton("x576 y222 w150 h28", "Capture Active")
	btnCaptureActive.OnEvent("Click", (*) => CaptureActiveWindow())
	btnActivateTarget := gMain.AddButton("x736 y222 w150 h28", "Activate Title")
	btnActivateTarget.OnEvent("Click", (*) => ActivateExternalWindow())
	btnMoveTarget := gMain.AddButton("x896 y222 w188 h28", "Move Title +40,+40")
	btnMoveTarget.OnEvent("Click", (*) => MoveExternalWindow())
	btnFromPoint := gMain.AddButton("x576 y260 w220 h28", "Use Window From Mouse Point")
	btnFromPoint.OnEvent("Click", (*) => CaptureWindowFromPoint())
	gMain.AddText("x808 y266 w276", "Reads the window under the current mouse cursor and fills the target title.")
	gWindowInfoEdit := gMain.AddEdit("x576 y300 w508 h140 ReadOnly -Wrap")
	externalStatus := gMain.AddText("x576 y452 w508", "External status: waiting for a target title")
	gStatus["window_external"] := externalStatus

	gTabs.UseTab("Pixel && Image")
	gMain.AddText("x36 y86 w1040", "The pixel helper is a borderless window with a solid background and an image fixture. Keep it fully visible and unobstructed when running these tests.")

	pixelGroup := gMain.AddGroupBox("x36 y116 w500 h360", "Pixel Helper")
	gMain.AddText("x52 y146 w468", "PixelGetColor samples a known region. PixelSearch looks for the sampled color. ImageSearch looks for killbill.png inside the helper.")
	btnShowPixel := gMain.AddButton("x52 y190 w150 h28", "Show Pixel Helper")
	btnShowPixel.OnEvent("Click", (*) => EnsurePixelHelper(true))
	btnPixelGet := gMain.AddButton("x212 y190 w120 h28", "PixelGetColor")
	btnPixelGet.OnEvent("Click", (*) => RunPixelGetColorTest())
	btnPixelSearch := gMain.AddButton("x344 y190 w120 h28", "PixelSearch")
	btnPixelSearch.OnEvent("Click", (*) => RunPixelSearchTest())
	btnImageSearch := gMain.AddButton("x52 y228 w150 h28", "ImageSearch")
	btnImageSearch.OnEvent("Click", (*) => RunImageSearchTest())
	pixelStatus := gMain.AddText("x52 y272 w468", "Pixel status: Not run")
	gStatus["pixel_color"] := pixelStatus
	imageStatus := gMain.AddText("x52 y304 w468", "Image status: Not run")
	gStatus["pixel_image"] := imageStatus

	pixelNotes := gMain.AddEdit("x52 y344 w468 h108 ReadOnly -Wrap")
	pixelNotes.Value :=
	(
	"Expected behavior:`n"
	"- PixelGetColor should return a hex color for the helper's solid background region.`n"
	"- PixelSearch should find that same sampled color within the helper bounds.`n"
	"- ImageSearch should find killbill.png if the asset exists and the helper is visible.`n`n"
	"If ImageSearch fails, confirm whether the helper was obscured, scaled, or on a different monitor."
	)

	gTabs.UseTab("Clipboard")
	gMain.AddText("x36 y86 w1040", "Clipboard tests are a mix of self-validating checks and manual confirmation. Use a real external app for the image paste step if you want a final interoperability check.")

	clipGroup := gMain.AddGroupBox("x36 y116 w500 h360", "Clipboard Tests")
	gMain.AddText("x52 y146 w468", "Text round-trip and delayed ClipWait are self-validating. Clipboard change monitoring shows whether callbacks are fired. Image copy is manual after the copy step succeeds.")
	gClipboardTextEdit := gMain.AddEdit("x52 y182 w468 h92", "Clipboard probe text:`nAlpha beta gamma`nUnicode: Eesti, 日本語, emoji-free.")
	btnClipboardRoundTrip := gMain.AddButton("x52 y290 w150 h28", "Text Round Trip")
	btnClipboardRoundTrip.OnEvent("Click", (*) => RunClipboardTextRoundTrip())
	btnClipWait := gMain.AddButton("x212 y290 w150 h28", "Delayed ClipWait")
	btnClipWait.OnEvent("Click", (*) => RunClipboardClipWaitTest())
	btnClipboardImage := gMain.AddButton("x372 y290 w148 h28", "Copy Image Asset")
	btnClipboardImage.OnEvent("Click", (*) => RunClipboardImageCopy())
	btnToggleMonitor := gMain.AddButton("x52 y330 w150 h28", "Toggle Change Monitor")
	btnToggleMonitor.OnEvent("Click", (*) => ToggleClipboardMonitor())
	clipStatus := gMain.AddText("x212 y336 w308", "Clipboard status: Not run")
	gStatus["clipboard_main"] := clipStatus
	clipMonitorStatus := gMain.AddText("x52 y372 w468", "Clipboard monitor: disabled")
	gStatus["clipboard_monitor"] := clipMonitorStatus

	clipNotes := gMain.AddEdit("x52 y404 w468 h48 ReadOnly -Wrap")
	clipNotes.Value :=
	(
		"Expected behavior:`n"
		"- Text Round Trip should preserve exact text.`n"
		"- Delayed ClipWait should return after a timer populates the clipboard.`n"
		"- Copy Image Asset should place killbill.png on the clipboard if the asset exists.`n"
		"- When the monitor is enabled, copying text in any app should increment the change counter."
	)

	gTabs.UseTab("Sound")
	gMain.AddText("x36 y86 w1040", "Use this tab to exercise sound device enumeration and default-device controls. On non-Windows platforms, differences in device backends or permissions should be logged as platform limitations rather than silent failures.")

	soundGroup := gMain.AddGroupBox("x36 y116 w500 h390", "Sound Devices")
	gMain.AddText("x52 y146 w468", "Refresh lists the default device state and attempts to enumerate numbered devices until the API stops returning names.")
	btnSoundRefresh := gMain.AddButton("x52 y182 w150 h28", "Refresh Sound")
	btnSoundRefresh.OnEvent("Click", (*) => RefreshSoundStatus())
	btnSoundBeep := gMain.AddButton("x214 y182 w150 h28", "Beep Test")
	btnSoundBeep.OnEvent("Click", (*) => RunSoundBeepTest())
	soundStatus := gMain.AddText("x52 y220 w468", "Sound status: Not run")
	gStatus["sound_main"] := soundStatus
	gSoundInfoEdit := gMain.AddEdit("x52 y254 w468 h226 ReadOnly -Wrap")

	soundControlGroup := gMain.AddGroupBox("x560 y116 w540 h390", "Default Device Controls")
	gMain.AddText("x576 y146 w508", "These controls target the default playback device. Refresh after each action to verify mute and volume state.")
	btnMute := gMain.AddButton("x576 y182 w150 h28", "Mute")
	btnMute.OnEvent("Click", (*) => SetSoundMute(true))
	btnUnmute := gMain.AddButton("x736 y182 w150 h28", "Unmute")
	btnUnmute.OnEvent("Click", (*) => SetSoundMute(false))
	btnVol25 := gMain.AddButton("x896 y182 w60 h28", "25%")
	btnVol25.OnEvent("Click", (*) => SetSoundVolumeValue(25))
	btnVol50 := gMain.AddButton("x964 y182 w60 h28", "50%")
	btnVol50.OnEvent("Click", (*) => SetSoundVolumeValue(50))
	btnVol100 := gMain.AddButton("x1032 y182 w52 h28", "100%")
	btnVol100.OnEvent("Click", (*) => SetSoundVolumeValue(100))
	gMain.AddText("x576 y226 w120", "Set volume:")
	gSoundVolumeEdit := gMain.AddEdit("x652 y222 w90", "50")
	btnApplyVolume := gMain.AddButton("x756 y222 w130 h28", "Apply Volume")
	btnApplyVolume.OnEvent("Click", (*) => ApplySoundVolume())
	gMain.AddText("x576 y266 w508 h72", "Expected behavior:`n- Mute and Unmute should toggle the default device state.`n- Set volume should change the reported percentage.`n- Device enumeration may vary by platform.")
	soundControlStatus := gMain.AddText("x576 y352 w508", "Sound control status: waiting")
	gStatus["sound_control"] := soundControlStatus

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
	SetStatus("input_hotstring", "Hotstring status: waiting for kssuite")
	SetStatus("input_hook", "InputHook status: idle")
	SetStatus("window_helper", "Helper status: Not run")
	SetStatus("window_external", "External status: waiting for a target title")
	SetStatus("pixel_color", "Pixel status: Not run")
	SetStatus("pixel_image", "Image status: Not run")
	SetStatus("clipboard_main", "Clipboard status: Not run")
	SetStatus("clipboard_monitor", "Clipboard monitor: " (gClipboardMonitorEnabled ? "enabled" : "disabled"))
	SetStatus("sound_main", "Sound status: Not run")
	SetStatus("sound_control", "Sound control status: waiting")
	AppendLog("Status text reset.")
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
	gHotstringTarget.Value := ""
	SetStatus("input_hotstring", "Hotstring status: waiting for kssuite")
	AppendLog("Hotstring probe reset.")
}

ValidateHotstringProbe() {
	global gHotstringTarget

	if InStr(gHotstringTarget.Value, "KEYSHARP-SUITE") {
		SetStatus("input_hotstring", "Hotstring status: PASS")
		AppendLog("Hotstring validation passed. The edit contains KEYSHARP-SUITE.")
	} else {
		SetStatus("input_hotstring", "Hotstring status: FAIL")
		AppendLog("Hotstring validation failed. Current edit content: " gHotstringTarget.Value)
	}
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
		sampleY := y + 30
		color := PixelGetColor(sampleX, sampleY)
		SetStatus("pixel_color", "Pixel status: PASS if the color below matches the red helper background: " color)
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
		ImageSearch(&foundX, &foundY, x, y, x + w - 1, y + h - 1, gPixelAssetPath)

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

:*:kssuite::
{
	global gHotstringHitCount

	gHotstringHitCount += 1
	SetStatus("input_hotstring", "Hotstring status: triggered (" gHotstringHitCount " hit" (gHotstringHitCount = 1 ? "" : "s") ")")
	AppendLog("Hotstring probe fired via kssuite.")
	SendText("KEYSHARP-SUITE")
}
