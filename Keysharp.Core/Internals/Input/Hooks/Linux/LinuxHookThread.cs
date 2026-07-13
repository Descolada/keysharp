#if LINUX
using Keysharp.Builtins;
using Keysharp.Internals.Input.Linux;
using Keysharp.Internals.Input.Hooks.Unix;
using static Keysharp.Internals.Input.Keyboard.KeyboardUtils;
using static Keysharp.Internals.Input.Keyboard.VirtualKeys;
using EventCode = Keysharp.Internals.Input.Linux.Devices.EventCode;

namespace Keysharp.Internals.Input.Hooks.Linux
{
	// Linux hook extension points backed by keysharp-inputd.
	internal sealed class LinuxHookThread : UnixHookThread
	{
		// Set to true for the duration of InputdHookLoop so callers can detect
		// they're on the hook reader thread and avoid lock acquisitions that
		// could deadlock against the script thread holding the same lock.
		[System.ThreadStatic]
		internal static bool IsHookReaderThread;
		[System.ThreadStatic]
		private static LinuxHookThread currentHookThread;
		[System.ThreadStatic]
		private static List<KeysharpInputdClient.Input> currentHookDecisionInputs;

		private KeysharpInputdClient inputdHookClient;
		private CancellationTokenSource inputdHookCancel;
		private Task inputdHookTask;
		private HookType inputdHookKinds;
		private bool usingInputdHooks;
		private bool UsingInputdHooks => usingInputdHooks;
		private const int MaxHookDecisionInputs = 1024;
		private static readonly (KeysharpInputdClient.MouseEventFlags Flag, uint Message)[] MouseHookMessages =
		[
			(KeysharpInputdClient.MouseEventFlags.Move, 0x0200u),
			(KeysharpInputdClient.MouseEventFlags.Wheel, 0x020Au),
			(KeysharpInputdClient.MouseEventFlags.HWheel, 0x020Eu),
			(KeysharpInputdClient.MouseEventFlags.LeftDown, 0x0201u),
			(KeysharpInputdClient.MouseEventFlags.LeftUp, 0x0202u),
			(KeysharpInputdClient.MouseEventFlags.RightDown, 0x0204u),
			(KeysharpInputdClient.MouseEventFlags.RightUp, 0x0205u),
			(KeysharpInputdClient.MouseEventFlags.MiddleDown, 0x0207u),
			(KeysharpInputdClient.MouseEventFlags.MiddleUp, 0x0208u),
			(KeysharpInputdClient.MouseEventFlags.XDown, 0x020Bu),
			(KeysharpInputdClient.MouseEventFlags.XUp, 0x020Cu),
		];

		// Avoid grabbing evdev before X/XWayland can receive inputd's replay device.
		private const int InputdGrabDisplayWaitMs = 5000;
		private const int InputdGrabDisplayWaitPollMs = 100;

		// Crash-loop protection for inputd hook recovery.
		private int inputdRecoveryInFlight;
		private long lastInputdRecoveryTicks;
		private int inputdRecoveryAttempts;
		private const long InputdRecoveryWindowMs = 5000;
		private const int MaxInputdRecoveryAttempts = 3;

		// Wayland cursor queries are IPC, so throttle ClipCursor correction.
		private long lastClipQueryTicks;
		private const int ClipCorrectionDelayMs = 8;
		private static readonly long ClipQueryMinIntervalTicks = System.Diagnostics.Stopwatch.Frequency * ClipCorrectionDelayMs / 1000;
		private int clipCorrectionRequest;
		private int clipCorrectionWorkerActive;

		protected override KeyboardMouseSender CreateKbdMsSender()
			=> new InputdKeyboardMouseSender();

		// Fast path for the 8 modifier VKs while the inputd hook is active: every
		// key event, physical (evdev) and synthetic (uinput, re-injected through
		// the hook), flows through UpdateKeybdState on this same process, so
		// kbdMsSender.modifiersLRLogical is always the complete, authoritative
		// modifier state -- a same-thread field read, no IPC required. Without
		// this override, the base implementation round-trips to keysharp-inputd
		// over the query socket for every call, and GetModifierLRState(true)
		// (KeyboardMouseSender.cs) calls this up to 8 times (once per modifier
		// VK) on the mainline hotkey-firing path for every hotkey-eligible
		// keystroke -- turning a zero-cost check into up to 8 blocking
		// round-trips per keystroke, serialized against any other concurrent
		// query (e.g. MouseGetPos) via the shared query-client lock.
		// Non-modifier VKs and mouse VKs fall through to the base implementation
		// unchanged.
		internal override bool IsKeyDownLogical(uint vk)
		{
			if (UsingInputdHooks)
			{
				var modMask = vk switch
				{
					VK_LCONTROL => MOD_LCONTROL,
					VK_RCONTROL => MOD_RCONTROL,
					VK_CONTROL => MOD_LCONTROL | MOD_RCONTROL,
					VK_LSHIFT => MOD_LSHIFT,
					VK_RSHIFT => MOD_RSHIFT,
					VK_SHIFT => MOD_LSHIFT | MOD_RSHIFT,
					VK_LMENU => MOD_LALT,
					VK_RMENU => MOD_RALT,
					VK_MENU => MOD_LALT | MOD_RALT,
					VK_LWIN => MOD_LWIN,
					VK_RWIN => MOD_RWIN,
					_ => 0u
				};

				if (modMask != 0)
					return (kbdMsSender.modifiersLRLogical & modMask) != 0;
			}

			return base.IsKeyDownLogical(vk);
		}

		internal static bool TryProcessReentrantHookInputs(
			IReadOnlyList<KeysharpInputdClient.Input> inputs,
			KeysharpInputdClient.SynthFlags flags)
		{
			if (flags != KeysharpInputdClient.SynthFlags.None || currentHookThread == null)
				return false;

			currentHookThread.ProcessReentrantHookInputs(inputs);
			return true;
		}

		protected override void StopPlatformHookCore(bool dispose)
		{
			StopInputdHookCore();
			base.StopPlatformHookCore(dispose);
		}

		protected override string PlatformHookDisabledMessage => "Linux hook disabled via KEYSHARP_DISABLE_HOOK=1.";

		protected override void OnPlatformHookStartFailed(string message)
			=> WarnIfWaylandHookUnavailable();

		private static bool warnedWaylandHookUnavailable;

		// Wayland has no in-process global input capture fallback. Surface the missing helper once to stderr
		// so hotkeys/hotstrings/InputHook do not silently stop working outside the debug pane.
		private static void WarnIfWaylandHookUnavailable()
		{
			if (warnedWaylandHookUnavailable || !Platform.Desktop.IsWaylandSession)
				return;

			warnedWaylandHookUnavailable = true;
			Script.WriteUncaughtErrorToStdErr(
				"Keysharp: global keyboard/mouse hooks require the keysharp-inputd helper on Wayland. " +
				"Install and enable it (re-run the installer, or " +
				"'keysharp-inputd --install-input-access') to enable global input capture on Wayland.");
		}

		// Maps a numpad key's evdev keycode (delivered by the daemon as the scan code) to the
		// navigation VK it produces when NumLock is off; returns 0 for any non-numpad key.
		private static uint NumpadNavigationVk(uint sc) => sc switch
		{
			(uint)EventCode.Kp7 => VK_HOME,   // KEY_KP7   -> NumpadHome
			(uint)EventCode.Kp8 => VK_UP,     // KEY_KP8   -> NumpadUp
			(uint)EventCode.Kp9 => VK_PRIOR,  // KEY_KP9   -> NumpadPgUp
			(uint)EventCode.Kp4 => VK_LEFT,   // KEY_KP4   -> NumpadLeft
			(uint)EventCode.Kp5 => VK_CLEAR,  // KEY_KP5   -> NumpadClear
			(uint)EventCode.Kp6 => VK_RIGHT,  // KEY_KP6   -> NumpadRight
			(uint)EventCode.Kp1 => VK_END,    // KEY_KP1   -> NumpadEnd
			(uint)EventCode.Kp2 => VK_DOWN,   // KEY_KP2   -> NumpadDown
			(uint)EventCode.Kp3 => VK_NEXT,   // KEY_KP3   -> NumpadPgDn
			(uint)EventCode.Kp0 => VK_INSERT, // KEY_KP0   -> NumpadIns
			(uint)EventCode.KpDot => VK_DELETE, // KEY_KPDOT -> NumpadDel
			_ => 0u
		};

		private static bool IsInputdInjected(uint flags, uint injectedFlag)
			=> (flags & injectedFlag) != 0;

		private bool ProcessInputdKeyboardHook(KeysharpInputdClient.KeyboardHookEvent ev)
		{
			if (!keyboardEnabled)
				return false;

			var vk = ev.VkCode;
			var sc = ev.ScanCode <= SC_MAX ? ev.ScanCode : 0u;
			var keyUp = (ev.Flags & 0x80u) != 0 || ev.Message == 0x0101u || ev.Message == 0x0105u;
			var isInjected = IsInputdInjected(ev.Flags, 0x10u);
			Keysharp.Internals.InputdKeyboard.UpdateIndicatorSnapshotFromHookFlags(ev.Flags);

			// KeyPhysIgnore tracks as physical state but is still ignored by hotkeys.
			if (ev.ExtraInfo == (ulong)KeyboardMouseSender.KeyPhysIgnore)
				isInjected = false;

			switch (vk)
			{
				case VK_SHIFT:
					vk = sc == SC_RSHIFT ? VK_RSHIFT : VK_LSHIFT;
					break;
				case VK_CONTROL:
					vk = sc == SC_RCONTROL ? VK_RCONTROL : VK_LCONTROL;
					break;
				case VK_MENU:
					vk = sc == SC_RALT ? VK_RMENU : VK_LMENU;
					break;
			}

			// Windows reports numpad navigation VKs when NumLock and Shift agree.
			var numLockOn = Keysharp.Internals.InputdKeyboard.HookFlagsNumLockOn(ev.Flags);
			var shiftDown = (kbdMsSender.modifiersLRLogical & (MOD_LSHIFT | MOD_RSHIFT)) != 0;

			if (numLockOn == shiftDown)
			{
				var navVk = NumpadNavigationVk(sc);
				if (navVk != 0)
					vk = navVk;
			}

			if (vk == 0)
				return false;

			lastHookEventWasKeyboard = true;
			lastKeyboardEventVk = vk;

			if (!isInjected)
				Script.TheScript.timeLastInputPhysical = DateTime.UtcNow;

			var args = new KeyboardHookEventArgs(
				keyUp ? EventType.KeyReleased : EventType.KeyPressed,
				vk,
				sc,
				isInjected ? EventMask.SimulatedEvent : EventMask.None,
				ev.TimeMs);
			var extraInfo = ev.ExtraInfo;

			if (extraInfo == (ulong)KeyboardMouseSender.KeyBlockThis)
				return true;

			var result = LowLevelCommon(args, vk, sc, ev.ScanCode, keyUp, extraInfo, isInjected ? HOOK_EVENT_INJECTED : 0, ev.DeviceId);
			ApplyKeyStateAfterKeyboardDecision(vk, keyUp, isInjected, result);
			return result != 0;
		}

		// Pure Wayland and normal X11-ready sessions return immediately.
		private static void WaitForDisplayServerBeforeGrab()
		{
			if (IsWaylandSession || IsX11Available)
				return;

			var deadline = Environment.TickCount64 + InputdGrabDisplayWaitMs;

			while (Environment.TickCount64 < deadline && !IsX11Available)
				Thread.Sleep(InputdGrabDisplayWaitPollMs);
		}

		protected override bool StartPlatformHookCore(bool wantKeyboard, bool wantMouse, out string message)
		{
			message = string.Empty;

			if (!wantKeyboard && !wantMouse)
				return false;

			var wantedHooks = HookType.None;

			if (wantKeyboard)
				wantedHooks |= HookType.Keyboard;

			if (wantMouse)
				wantedHooks |= HookType.Mouse;

			var hookRunning = inputdHookClient != null
				&& inputdHookTask != null
				&& !inputdHookTask.IsCompleted;

			if (hookRunning && inputdHookKinds == wantedHooks)
			{
				usingInputdHooks = true;
				return true;
			}

			WaitForDisplayServerBeforeGrab();
			StopInputdHookCore();

			var required = KeysharpInputdClient.Capabilities.None;

			if (wantKeyboard)
				required |= KeysharpInputdClient.Capabilities.HookKeyboard;

			if (wantMouse)
				required |= KeysharpInputdClient.Capabilities.HookMouse;

			var permissionRequest = KeysharpInputdManager.ExpandInputPermissionRequest(required);
			var permission = KeysharpInputdManager.EnsureCapabilities(permissionRequest, "install keyboard/mouse hooks");

			if (!permission.IsGranted)
			{
				message = $"keysharp-inputd hook unavailable; global hooks disabled. {permission.Message}";
				return false;
			}

			try
			{
				inputdHookClient = KeysharpInputdClient.Connect(permissionRequest);

				if (wantMouse)
					_ = inputdHookClient.SubscribeHook(KeysharpInputdClient.HookType.MouseLowLevel);

				if (wantKeyboard)
					_ = inputdHookClient.SubscribeHook(KeysharpInputdClient.HookType.KeyboardLowLevel);

				inputdHookCancel = new CancellationTokenSource();
				var hookToken = inputdHookCancel.Token;
				var hookClient = inputdHookClient;
				inputdHookTask = Task.Run(() => InputdHookLoop(hookClient, hookToken));

				inputdHookKinds = wantedHooks;
				usingInputdHooks = true;
				return true;
			}
			catch (Exception ex)
			{
				StopInputdHookCore();
				message = $"keysharp-inputd hook unavailable; global hooks disabled. {ex.Message}";
				return false;
			}
		}

		private void StopInputdHookCore()
		{
			usingInputdHooks = false;
			try { inputdHookCancel?.Cancel(); } catch { }
			try { inputdHookClient?.Dispose(); } catch { }
			try { if (inputdHookTask != null && !inputdHookTask.IsCompleted) inputdHookTask.Wait(50); } catch { }
			inputdHookCancel = null;
			inputdHookClient = null;
			inputdHookTask = null;
			inputdHookKinds = HookType.None;
		}

		private sealed class HookReaderLiveness
		{
			private const long StallGraceMs = 10_000;
			private long lastProgressTicks = Environment.TickCount64;
			private volatile bool waitingForEvent = true;

			internal void MarkWaiting()
			{
				Volatile.Write(ref lastProgressTicks, Environment.TickCount64);
				waitingForEvent = true;
			}

			internal void MarkProgress()
			{
				waitingForEvent = false;
				Volatile.Write(ref lastProgressTicks, Environment.TickCount64);
			}

			internal bool IsAlive()
				=> waitingForEvent
					|| Environment.TickCount64 - Volatile.Read(ref lastProgressTicks) < StallGraceMs;
		}

		private void InputdHookLoop(KeysharpInputdClient client, CancellationToken token)
		{
			IsHookReaderThread = true;
			var liveness = new HookReaderLiveness();
			client.SetLeaseLivenessProbe(liveness.IsAlive);

			try
			{
				InputdHookLoopCore(client, token, liveness);
			}
			finally
			{
				client.SetLeaseLivenessProbe(static () => false);
				IsHookReaderThread = false;
			}
		}

		private void InputdHookLoopCore(KeysharpInputdClient client, CancellationToken token, HookReaderLiveness liveness)
		{
			while (!token.IsCancellationRequested)
			{
				KeysharpInputdClient.HookEvent hookEvent;
				liveness.MarkWaiting();

				try
				{
					hookEvent = client.ReadHookEvent();
				}
				catch (ObjectDisposedException)
				{
					return;
				}
				catch (Exception ex)
				{
					if (!token.IsCancellationRequested)
					{
						Ks.OutputDebugLine($"keysharp-inputd hook reader stopped: {ex.Message}");
						HandleInputdHookReaderLoss(ex.Message);
					}

					return;
				}

				liveness.MarkProgress();
				CheckInputdPanicCombo(hookEvent);

				var block = false;
				var callbackStateLost = false;
				IReadOnlyList<KeysharpInputdClient.Input> hookDecisionPrefixInputs = null;

				try
				{
					currentHookThread = this;
					currentHookDecisionInputs ??= new(MaxHookDecisionInputs);
					currentHookDecisionInputs.Clear();
					block = hookEvent.HookType switch
					{
						KeysharpInputdClient.HookType.KeyboardLowLevel => ProcessInputdKeyboardHook(hookEvent.Keyboard),
						KeysharpInputdClient.HookType.MouseLowLevel => ProcessInputdMouseHook(hookEvent.Mouse),
						_ => false
					};
				}
				catch (Exception ex)
				{
					Ks.OutputDebugLine($"keysharp-inputd hook event processing failed: {ex}");
					callbackStateLost = true;
				}
				finally
				{
					currentHookThread = null;

					if (currentHookDecisionInputs != null && currentHookDecisionInputs.Count != 0)
					{
						hookDecisionPrefixInputs = currentHookDecisionInputs.ToArray();
						currentHookDecisionInputs.Clear();
					}
				}

				try
				{
					SendInputdHookDecision(client, hookEvent, block, hookDecisionPrefixInputs);

					if (!block
						&& CursorClipActive
						&& hookEvent.HookType == KeysharpInputdClient.HookType.MouseLowLevel
						&& hookEvent.Mouse.Message == 0x0200u
						&& (hookEvent.Mouse.Flags & 0x01u) == 0)
						RequestCursorClipCorrection();
				}
				catch (KeysharpInputdClient.RequestFailedException ex)
					when (KeysharpInputdClient.IsStaleHookDecisionFailure(ex))
				{
					Ks.OutputDebugLine($"keysharp-inputd hook decision for event {hookEvent.EventId} arrived after its deadline; continuing hooks.");
				}
				catch (Exception ex)
				{
					if (!token.IsCancellationRequested)
					{
						Ks.OutputDebugLine($"keysharp-inputd hook decision failed: {ex.Message}");
						HandleInputdHookReaderLoss(ex.Message);
					}

					return;
				}

				if (callbackStateLost)
				{
					HandleInputdHookReaderLoss("hook event processing failed");
					return;
				}
			}
		}

		private void ProcessReentrantHookInputs(IReadOnlyList<KeysharpInputdClient.Input> inputs)
		{
			if (inputs == null || currentHookDecisionInputs == null)
				return;

			for (var i = 0; i < inputs.Count; i++)
			{
				var input = inputs[i];

				if (input.Type == KeysharpInputdClient.InputType.Keyboard)
					ProcessReentrantKeyboardInput(input);
				else if (input.Type == KeysharpInputdClient.InputType.Mouse)
					ProcessReentrantMouseInput(input);
				else
					AppendHookDecisionInput(input);
			}
		}

		private void ProcessReentrantKeyboardInput(KeysharpInputdClient.Input input)
		{
			var keyboard = input.Keyboard;
			var flags = keyboard.Flags;
			var vk = (uint)keyboard.Vk;
			var sc = (uint)keyboard.Scan;

			if ((flags & KeysharpInputdClient.KeyEventFlags.Unicode) != 0)
				vk = VK_PACKET;
			else
			{
				if (vk == 0 && sc != 0)
					vk = KeyCodes.MapScToVk(sc);
				else if (sc == 0 && vk != 0)
					sc = KeyCodes.MapVkToSc(vk);
			}

			var keyUp = (flags & KeysharpInputdClient.KeyEventFlags.KeyUp) != 0;
			var hookFlags = 0x10u;

			if ((flags & KeysharpInputdClient.KeyEventFlags.ExtendedKey) != 0)
				hookFlags |= 0x01u;

			if (keyUp)
				hookFlags |= 0x80u;

			var ev = new KeysharpInputdClient.KeyboardHookEvent(
				keyUp ? 0x0101u : 0x0100u,
				vk,
				sc,
				hookFlags,
				CurrentHookTimeMs(),
				keyboard.ExtraInfo,
				0);

			if (!ProcessInputdKeyboardHook(ev))
				AppendHookDecisionInput(input);
		}

		private void ProcessReentrantMouseInput(KeysharpInputdClient.Input input)
		{
			var mouse = input.Mouse;
			var flags = mouse.Flags;
			var delivered = false;

			if ((flags & KeysharpInputdClient.MouseEventFlags.Move) != 0)
			{
				var primitiveFlags = flags
					& (KeysharpInputdClient.MouseEventFlags.Move
						| KeysharpInputdClient.MouseEventFlags.MoveNoCoalesce
						| KeysharpInputdClient.MouseEventFlags.VirtualDesk
						| KeysharpInputdClient.MouseEventFlags.Absolute);
				var primitive = KeysharpInputdClient.Input.MouseEvent(
					mouse.Dx,
					mouse.Dy,
					(flags & KeysharpInputdClient.MouseEventFlags.Absolute) != 0
						? (uint)KeysharpInputdClient.MouseEventFlags.Absolute
						: 0u,
					primitiveFlags,
					mouse.Time,
					mouse.ExtraInfo);

				ProcessReentrantMousePrimitive(primitive, 0x0200u);
				delivered = true;
			}

			foreach (var (flag, message) in MouseHookMessages)
			{
				if (flag == KeysharpInputdClient.MouseEventFlags.Move || (flags & flag) == 0)
					continue;

				ProcessReentrantMousePrimitive(
					KeysharpInputdClient.Input.MouseEvent(0, 0, mouse.MouseData, flag, mouse.Time, mouse.ExtraInfo),
					message);
				delivered = true;
			}

			if (!delivered)
				AppendHookDecisionInput(input);
		}

		private void ProcessReentrantMousePrimitive(KeysharpInputdClient.Input primitive, uint message)
		{
			var mouse = primitive.Mouse;
			var mouseData = mouse.MouseData;

			if ((mouse.Flags & (KeysharpInputdClient.MouseEventFlags.Wheel | KeysharpInputdClient.MouseEventFlags.HWheel)) != 0)
				mouseData <<= 16;

			var ev = new KeysharpInputdClient.MouseHookEvent(
				message,
				mouse.Dx,
				mouse.Dy,
				mouseData,
				0x01u,
				CurrentHookTimeMs(),
				mouse.ExtraInfo,
				0);

			if (!ProcessInputdMouseHook(ev))
				AppendHookDecisionInput(primitive);
		}

		private static ulong CurrentHookTimeMs()
			=> unchecked((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

		private static void AppendHookDecisionInput(KeysharpInputdClient.Input input)
		{
			if (currentHookDecisionInputs == null || currentHookDecisionInputs.Count >= MaxHookDecisionInputs - 1)
				return;

			currentHookDecisionInputs.Add(input);
		}

		private bool inputdPanicCtrlDown;
		private bool inputdPanicAltDown;

		private static void SendInputdHookDecision(
			KeysharpInputdClient client,
			in KeysharpInputdClient.HookEvent hookEvent,
			bool block,
			IReadOnlyList<KeysharpInputdClient.Input> prefixInputs)
		{
			if (prefixInputs == null || prefixInputs.Count == 0)
			{
				client.SendHookDecision(
					hookEvent.EventId,
					block ? KeysharpInputdClient.HookDecision.Block : KeysharpInputdClient.HookDecision.Pass);
				return;
			}

			var replacementInputs = new List<KeysharpInputdClient.Input>(Math.Min(MaxHookDecisionInputs, prefixInputs.Count + (block ? 0 : 1)));

			for (var i = 0; i < prefixInputs.Count && replacementInputs.Count < MaxHookDecisionInputs; i++)
				replacementInputs.Add(prefixInputs[i]);

			if (!block && TryInputFromHookEvent(hookEvent, out var originalInput) && replacementInputs.Count < MaxHookDecisionInputs)
				replacementInputs.Add(originalInput);

			client.SendHookDecision(hookEvent.EventId, KeysharpInputdClient.HookDecision.Modify, replacementInputs);
		}

		private static bool TryInputFromHookEvent(in KeysharpInputdClient.HookEvent hookEvent, out KeysharpInputdClient.Input input)
		{
			if (hookEvent.HookType == KeysharpInputdClient.HookType.KeyboardLowLevel)
				return TryInputFromKeyboardHookEvent(hookEvent.Keyboard, out input);

			if (hookEvent.HookType == KeysharpInputdClient.HookType.MouseLowLevel)
				return TryInputFromMouseHookEvent(hookEvent.Mouse, out input);

			input = default;
			return false;
		}

		private static bool TryInputFromKeyboardHookEvent(
			KeysharpInputdClient.KeyboardHookEvent ev,
			out KeysharpInputdClient.Input input)
		{
			var flags = KeysharpInputdClient.KeyEventFlags.ScanCode;

			if ((ev.Flags & 0x01u) != 0)
				flags |= KeysharpInputdClient.KeyEventFlags.ExtendedKey;

			if ((ev.Flags & 0x80u) != 0 || ev.Message == 0x0101u || ev.Message == 0x0105u)
				flags |= KeysharpInputdClient.KeyEventFlags.KeyUp;

			input = KeysharpInputdClient.Input.Key(
				(ushort)ev.VkCode,
				(ushort)ev.ScanCode,
				flags,
				extraInfo: ev.ExtraInfo);
			return ev.ScanCode != 0 || ev.VkCode != 0;
		}

		private static bool TryInputFromMouseHookEvent(
			KeysharpInputdClient.MouseHookEvent ev,
			out KeysharpInputdClient.Input input)
		{
			var flags = (KeysharpInputdClient.MouseEventFlags)0;

			foreach (var (flag, message) in MouseHookMessages)
			{
				if (ev.Message != message)
					continue;

				flags = flag;
				break;
			}

			if (flags == 0)
			{
				input = default;
				return false;
			}

			var dx = 0;
			var dy = 0;
			var mouseData = 0u;

			if ((flags & KeysharpInputdClient.MouseEventFlags.Move) != 0)
			{
				dx = ev.X;
				dy = ev.Y;

				if ((ev.MouseData & (uint)KeysharpInputdClient.MouseEventFlags.Absolute) != 0)
					flags |= KeysharpInputdClient.MouseEventFlags.Absolute;
			}
			else if ((flags & (KeysharpInputdClient.MouseEventFlags.Wheel | KeysharpInputdClient.MouseEventFlags.HWheel)) != 0)
				mouseData = unchecked((uint)((int)ev.MouseData >> 16));
			else if ((flags & (KeysharpInputdClient.MouseEventFlags.XDown | KeysharpInputdClient.MouseEventFlags.XUp)) != 0)
				mouseData = ev.MouseData;

			input = KeysharpInputdClient.Input.MouseEvent(dx, dy, mouseData, flags, extraInfo: ev.ExtraInfo);
			return true;
		}

		private void CheckInputdPanicCombo(in KeysharpInputdClient.HookEvent hookEvent)
		{
			if (hookEvent.HookType != KeysharpInputdClient.HookType.KeyboardLowLevel)
				return;

			var ev = hookEvent.Keyboard;
			var down = (ev.Flags & 0x80u) == 0 && ev.Message != 0x0101u && ev.Message != 0x0105u;

			switch (ev.VkCode)
			{
				case VK_CONTROL:
				case VK_LCONTROL:
				case VK_RCONTROL:
					inputdPanicCtrlDown = down;
					break;
				case VK_MENU:
				case VK_LMENU:
				case VK_RMENU:
					inputdPanicAltDown = down;
					break;
				case VK_PAUSE:
				case VK_CANCEL:
					if (down && inputdPanicCtrlDown && inputdPanicAltDown)
					{
						Ks.OutputDebugLine("keysharp-inputd: Ctrl+Alt+Pause emergency passthrough - releasing all grabs/block-input.");
						KeysharpInputdManager.EmergencyReleaseInput();
					}
					break;
			}
		}

		private void HandleInputdHookReaderLoss(string reason)
		{
			if (Interlocked.Exchange(ref inputdRecoveryInFlight, 1) == 1)
				return;

			_ = Task.Run(() =>
			{
				try
				{
					RecoverInputdHooks(reason);
				}
				catch (Exception ex)
				{
					Ks.OutputDebugLine($"keysharp-inputd hook recovery failed: {ex}");
				}
				finally
				{
					Volatile.Write(ref inputdRecoveryInFlight, 0);
				}
			});
		}

		private void RecoverInputdHooks(string reason)
		{
			HookType want;
			long recoveryGeneration;

			lock (hookStateLock)
			{
				recoveryGeneration = hookStateGeneration;
				want = HookType.None;

				if (keyboardEnabled)
					want |= HookType.Keyboard;
				if (mouseEnabled)
					want |= HookType.Mouse;

				keyboardEnabled = false;
				mouseEnabled = false;
				StopInputdHookCore();
			}

			if (want == HookType.None)
				return;

			var now = Environment.TickCount64;

			if (now - lastInputdRecoveryTicks < InputdRecoveryWindowMs)
			{
				if (++inputdRecoveryAttempts >= MaxInputdRecoveryAttempts)
				{
					if (CursorClipActive)
						ClearCursorClip();

					// Give up: mirror ChangePlatformHookState's disable path. keyboardEnabled/mouseEnabled were
					// already cleared above, but kbdHook/mouseHook stayed non-zero and SyncHookMutexes was never
					// called. Without this, HasKbdHook()/HasMouseHook() (and thus A_KeybdHookInstalled/
					// A_MouseHookInstalled) keep reporting installed, and the cross-process named
					// 'Keysharp Keybd'/'Keysharp Mouse' mutexes stay held -- making OTHER Keysharp scripts wrongly
					// think a system hook exists and push their Send onto the SendInput fallback.
					var giveUpMessage = $"keysharp-inputd hooks lost repeatedly; global hooks disabled: {reason}";

					lock (hookStateLock)
					{
						kbdHook = 0;
						mouseHook = 0;
						// Record why we gave up so GetHookActivationFailureReason()/A_*HookInstalled reflect the
						// disabled state, rather than leaving a stale message from the last activation attempt (matches
						// how the normal disable path sets lastHookActivationFailure).
						lastHookActivationFailure = giveUpMessage;
					}

					SyncHookMutexes(changeIsTemporary: false);
					Ks.OutputDebugLine(giveUpMessage);
					return;
				}
			}
			else
			{
				inputdRecoveryAttempts = 1;
			}

			lastInputdRecoveryTicks = now;
			Ks.OutputDebugLine($"keysharp-inputd hook reader lost ({reason}); re-establishing hooks.");
			ChangePlatformHookState(want, changeIsTemporary: false, expectedGeneration: recoveryGeneration);

			if (CursorClipActive && !UsingInputdHooks)
			{
				ClearCursorClip();
				Ks.OutputDebugLine("ClipCursor released because the keysharp-inputd mouse hook was lost.");
			}
		}

		private bool TryGetCursorPosThrottled(out POINT p)
		{
			if (IsWaylandSession)
			{
				var now = Stopwatch.GetTimestamp();

				if (now - lastClipQueryTicks < ClipQueryMinIntervalTicks)
				{
					p = default;
					return false;
				}

				lastClipQueryTicks = now;
			}

			return GetCursorPos(out p);
		}

		private void RequestCursorClipCorrection()
		{
			Interlocked.Increment(ref clipCorrectionRequest);

			if (Interlocked.CompareExchange(ref clipCorrectionWorkerActive, 1, 0) != 0)
				return;

			_ = Task.Run(RunCursorClipCorrectionAsync);
		}

		private async Task RunCursorClipCorrectionAsync()
		{
			var handledRequest = Volatile.Read(ref clipCorrectionRequest);

			try
			{
				while (CursorClipActive)
				{
					handledRequest = Volatile.Read(ref clipCorrectionRequest);
					await Task.Delay(ClipCorrectionDelayMs).ConfigureAwait(false);

					if (GetCursorPos(out var p))
					{
						int x = p.X, y = p.Y;

						if (ClampToCursorClip(ref x, ref y))
							_ = Platform.Mouse.TryMoveAbsolute(x, y);
					}

					if (handledRequest == Volatile.Read(ref clipCorrectionRequest))
						break;
				}
			}
			catch (Exception ex)
			{
				Ks.OutputDebugLine($"ClipCursor correction failed: {ex.Message}");
			}

			Volatile.Write(ref clipCorrectionWorkerActive, 0);

			if (CursorClipActive
				&& handledRequest != Volatile.Read(ref clipCorrectionRequest)
				&& Interlocked.CompareExchange(ref clipCorrectionWorkerActive, 1, 0) == 0)
				_ = Task.Run(RunCursorClipCorrectionAsync);
		}

		private int absNormWidth;
		private int absNormHeight;
		private long absNormDimsTicks;

		private void NormalizeAbsoluteToScreen(ref int x, ref int y)
		{
			if (!TryGetVirtualDesktopSize(out var width, out var height) || width <= 0 || height <= 0)
				return;

			x = (int)((long)Math.Clamp(x, 0, 65535) * (width - 1) / 65535);
			y = (int)((long)Math.Clamp(y, 0, 65535) * (height - 1) / 65535);
		}

		private bool TryGetVirtualDesktopSize(out int width, out int height)
		{
			var now = Environment.TickCount64;

			if (absNormWidth > 0 && absNormHeight > 0 && now - absNormDimsTicks < 1000)
			{
				width = absNormWidth;
				height = absNormHeight;
				return true;
			}

			width = height = 0;

			try
			{
				var display = Keysharp.Internals.Window.Linux.Proxies.XDisplay.Default;

				if (display == null || display.Handle == 0)
					return false;

				var root = Keysharp.Internals.Window.Linux.X11.Xlib.XDefaultRootWindow(display.Handle);

				if (!Keysharp.Internals.Window.Linux.X11.Xlib.XGetGeometry(display.Handle, (long)root,
						out _, out _, out _, out var w, out var h, out _, out _))
					return false;

				absNormWidth = width = w;
				absNormHeight = height = h;
				absNormDimsTicks = now;
				return true;
			}
			catch
			{
				return false;
			}
		}

		private bool ProcessInputdMouseHook(KeysharpInputdClient.MouseHookEvent ev)
		{
			if (!mouseEnabled)
				return false;

			var isInjected = IsInputdInjected(ev.Flags, 0x01u);
			lastHookEventWasKeyboard = false;

			if (!isInjected)
			{
				var script = Script.TheScript;
				script.timeLastInputPhysical = script.timeLastInputMouse = DateTime.UtcNow;
			}

			switch (ev.Message)
			{
				case 0x0200u:
				{
					if (!isInjected && CursorClipActive && TryGetCursorPosThrottled(out var clipPos))
					{
						int cx = clipPos.X, cy = clipPos.Y;

						if (ClampToCursorClip(ref cx, ref cy))
						{
							_ = Platform.Mouse.TryMoveAbsolute(cx, cy);
							return true;
						}
					}

					var moveBlocked = !isInjected && Script.TheScript.KeyboardData.blockMouseMove;

					if (AnyInputWantsMouseMove())
					{
						int moveX = ev.X, moveY = ev.Y;

						if ((ev.MouseData & (uint)MOUSEEVENTF.ABSOLUTE) != 0)
							NormalizeAbsoluteToScreen(ref moveX, ref moveY);

						if (!CollectMouseMove(ev.ExtraInfo, moveX, moveY, null))
							moveBlocked = true;
					}

					return moveBlocked;
				}
				case 0x020Au:
					return ProcessInputdMouseWheelHook(ev, vertical: true, isInjected);
				case 0x020Eu:
					return ProcessInputdMouseWheelHook(ev, vertical: false, isInjected);
			}

			POINT clickPos = default;
			var haveClickPos = !isInjected
				&& (CursorClipActive || Script.TheScript.input != null)
				&& GetCursorPos(out clickPos);

			if (!isInjected && CursorClipActive && haveClickPos)
			{
				int bx = clickPos.X, by = clickPos.Y;

				if (ClampToCursorClip(ref bx, ref by))
				{
					_ = Platform.Mouse.TryMoveAbsolute(bx, by);
					clickPos = new POINT(bx, by);
				}
			}

			var (vk, keyUp) = ev.Message switch
			{
				0x0201u => (VK_LBUTTON, false),
				0x0202u => (VK_LBUTTON, true),
				0x0204u => (VK_RBUTTON, false),
				0x0205u => (VK_RBUTTON, true),
				0x0207u => (VK_MBUTTON, false),
				0x0208u => (VK_MBUTTON, true),
				0x020Bu => ((ev.MouseData >> 16) == MouseUtils.XBUTTON1 ? VK_XBUTTON1 : VK_XBUTTON2, false),
				0x020Cu => ((ev.MouseData >> 16) == MouseUtils.XBUTTON1 ? VK_XBUTTON1 : VK_XBUTTON2, true),
				_ => (0u, true)
			};

			if (vk == 0)
				return false;

			var args = new MouseHookEventArgs(
				keyUp ? EventType.MouseReleased : EventType.MousePressed,
				KeyCodes.VkToMouseButton(vk),
				haveClickPos ? clickPos.X : ev.X,
				haveClickPos ? clickPos.Y : ev.Y,
				isInjected ? EventMask.SimulatedEvent : EventMask.None,
				ev.TimeMs);
			var result = LowLevelCommon(args, vk, 0, 0, keyUp, ev.ExtraInfo, isInjected ? HOOK_EVENT_INJECTED : 0, ev.DeviceId);
			return result != 0;
		}

		private bool ProcessInputdMouseWheelHook(KeysharpInputdClient.MouseHookEvent ev, bool vertical, bool isInjected)
		{
			var delta = unchecked((short)(ev.MouseData >> 16));
			var vk = vertical
				? (delta < 0 ? VK_WHEEL_DOWN : VK_WHEEL_UP)
				: (delta < 0 ? VK_WHEEL_LEFT : VK_WHEEL_RIGHT);
			var sc = (uint)delta;
			var args = new MouseWheelHookEventArgs(
				delta,
				vertical ? MouseWheelScrollDirection.Vertical : MouseWheelScrollDirection.Horizontal,
				ev.X,
				ev.Y,
				isInjected ? EventMask.SimulatedEvent : EventMask.None,
				ev.TimeMs);
			var result = LowLevelCommon(args, vk, sc, sc, keyUp: false, ev.ExtraInfo, isInjected ? HOOK_EVENT_INJECTED : 0, deviceId: ev.DeviceId);
			return result != 0;
		}

		protected override bool CanClipCursor(out string reason)
		{
			if (!UsingInputdHooks)
			{
				reason = "the keysharp-inputd mouse hook is not active";
				return false;
			}

			if (!Platform.Mouse.SupportsCursorQueryAndMove)
			{
				reason = "the cursor cannot be both queried and moved in this session";
				return false;
			}

			reason = "";
			return true;
		}
	}
}
#endif
