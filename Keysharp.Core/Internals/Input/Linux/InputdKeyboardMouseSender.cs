using Keysharp.Builtins;
#if LINUX
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using Keysharp.Internals.Input.Keyboard;
using Keysharp.Internals.Input.Mouse;
using Keysharp.Internals.Input.Unix;
using static Keysharp.Internals.Input.Keyboard.KeyboardUtils;
using static Keysharp.Internals.Input.Keyboard.VirtualKeys;

namespace Keysharp.Internals.Input.Linux
{
	/// <summary>
	/// Keyboard/mouse sender that routes input through keysharp-inputd.
	/// Mirrors WindowsKeyboardMouseSender semantics: batched SendInput-mode events bypass the hook
	/// chain (KSI_SYNTH_FLAG_BYPASS_HOOK), while single SendEvent-mode events pass through it so
	/// other hook clients and send-level filtering see them.
	/// </summary>
	internal sealed class InputdKeyboardMouseSender : KeyboardMouseSender
	{
		// KEYEVENTF_* / MOUSEEVENTF_* constants equal KSI_KEYEVENTF_* / KSI_MOUSEEVENTF_* by design.
		private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
		private const uint KEYEVENTF_KEYUP       = 0x0002;
		private const uint KEYEVENTF_UNICODE     = 0x0004;
		private const uint KEYEVENTF_SCANCODE    = 0x0008;
		private const uint MOUSEEVENTF_ABSOLUTE  = 0x8000;

		private readonly List<KeysharpInputdClient.Input> eventSi = new();
		private readonly Stack<uint> eventModifierStack = new();

		private static KeysharpInputdClient Client => KeysharpInputdManager.Client;
		private const int MaxMouseMoveChunk = 1000;

		// ── Abstract overrides ────────────────────────────────────────────────

		internal override bool MouseButtonsSwapped => false;

		protected internal override bool TrySendPlatformRawText(ReadOnlySpan<char> text, ref int keyIndex, uint modifiersLR)
		{
			if (keyIndex < 0 || keyIndex >= text.Length)
				return false;

			var localEvents = sendMode == SendModes.Event ? new List<KeysharpInputdClient.Input>() : null;
			var events = localEvents ?? eventSi;
			var extraInfo = KeyIgnoreLevel(ThreadAccessors.A_SendLevel);

			for (var i = keyIndex; i < text.Length; i++)
			{
				var ch = text[i];

				if (ch == '\r')
				{
					if (i + 1 < text.Length && text[i + 1] == '\n')
						i++;

					QueueKey(events, KeyEventTypes.KeyDownAndUp, VK_RETURN, extraInfo);
					continue;
				}

				if (ch == '\n')
				{
					QueueKey(events, KeyEventTypes.KeyDownAndUp, VK_RETURN, extraInfo);
					continue;
				}

				if (ch == '\b')
				{
					QueueKey(events, KeyEventTypes.KeyDownAndUp, VK_BACK, extraInfo);
					continue;
				}

				if (ch == '\t')
				{
					QueueKey(events, KeyEventTypes.KeyDownAndUp, VK_TAB, extraInfo);
					continue;
				}

				if (Rune.DecodeFromUtf16(text[i..], out var rune, out var charsConsumed) != OperationStatus.Done)
					continue;

				i += charsConsumed - 1;

				QueueTextRune(events, rune, modifiersLR, extraInfo);
			}

			if (localEvents != null && localEvents.Count != 0)
				KeysharpInputdManager.SendInputViaSynthesisChannel(localEvents, KeysharpInputdClient.SynthFlags.BypassHook);

			keyIndex = text.Length - 1;
			return true;
		}

		internal override void InitEventArray(int maxEvents, uint modifiersLR)
		{
			eventModifierStack.Push(eventModifiersLR);
			eventModifiersLR = modifiersLR;
			eventSi.Clear();
		}

		internal override void CleanupEventArray(long finalKeyDelay)
		{
			eventSi.Clear();

			if (eventModifierStack.Count != 0)
				eventModifiersLR = eventModifierStack.Pop();

			// Reset before hook-side corrective keystrokes, such as CapsLock
			// prefix cleanup, so they are emitted immediately instead of
			// being queued into an event array that no caller will flush.
			sendMode = SendModes.Event;
			DoKeyDelay(finalKeyDelay);
		}

		internal override int SiEventCount() => eventSi.Count;
		internal override int PbEventCount() => 0;

		internal override void PutKeybdEventIntoArray(uint keyAsModifiersLR, uint vk, uint sc, uint eventFlags, long extraInfo)
		{
			var flags = (KeysharpInputdClient.KeyEventFlags)eventFlags;
			var isKeyUp = (eventFlags & KEYEVENTF_KEYUP) != 0;
			var isUnicode = (eventFlags & KEYEVENTF_UNICODE) != 0;

			if (isKeyUp)
				eventModifiersLR &= ~keyAsModifiersLR;
			else
				eventModifiersLR |= keyAsModifiersLR;

			if (isUnicode)
			{
				eventSi.Add(KeysharpInputdClient.Input.Key(
					vk: (ushort)vk,
					scan: (ushort)sc,
					flags: flags,
					extraInfo: (ulong)extraInfo));
			}
			else if ((sc & 0x100) != 0)
			{
				// Mirror Windows: extended bit lives in high byte of SC; promote to EXTENDEDKEY flag.
				flags |= (KeysharpInputdClient.KeyEventFlags)KEYEVENTF_EXTENDEDKEY;

				// When no VK is available the daemon must use the scan-code path.
				if (vk == 0 && (sc & 0xFF) != 0)
					flags |= (KeysharpInputdClient.KeyEventFlags)KEYEVENTF_SCANCODE;

				eventSi.Add(KeysharpInputdClient.Input.Key(
					vk: (ushort)vk,
					scan: (ushort)(sc & 0xFF),
					flags: flags,
					extraInfo: (ulong)extraInfo));
			}
			else
			{
				// When no VK is available the daemon must use the scan-code path.
				if (vk == 0 && (sc & 0xFF) != 0)
					flags |= (KeysharpInputdClient.KeyEventFlags)KEYEVENTF_SCANCODE;

				eventSi.Add(KeysharpInputdClient.Input.Key(
					vk: (ushort)vk,
					scan: (ushort)(sc & 0xFF),
					flags: flags,
					extraInfo: (ulong)extraInfo));
			}
		}

		internal override void PutMouseEventIntoArray(uint eventFlags, uint data, int x, int y)
		{
			eventSi.Add(KeysharpInputdClient.Input.MouseEvent(
				dx: x,
				dy: y,
				mouseData: data,
				flags: (KeysharpInputdClient.MouseEventFlags)eventFlags));
		}

		internal override void SendEventArray(ref long finalKeyDelay, uint modsDuringSend)
		{
			if (eventSi.Count == 0)
				return;

			try
			{
				// Keep inputd sends visible to the hook chain. SendLevel/extra_info
				// decides whether hotkeys ignore or consume the event; bypassing here
				// would prevent remaps such as g::s from passing generated s through
				// the same Windows-like filtering path as other synthesized input.
				KeysharpInputdManager.SendInputViaSynthesisChannel(eventSi);
			}
			finally
			{
				eventSi.Clear();
			}
		}

		/// <summary>
		/// Immediate single-event send — mirrors keybd_event(). Events with user-level extraInfo
		/// pass through the hook chain with the INJECTED flag set, allowing send-level filtering
		/// by other hook clients. Internal events (IsIgnored extraInfo) use BypassHook to avoid
		/// unnecessary loopback round-trips that delay subsequent event processing.
		/// </summary>
		internal override void SendKeybdEvent(KeyEventTypes eventType, uint vk, uint sc, uint flags, long extraInfo)
		{
			var keyFlags = (KeysharpInputdClient.KeyEventFlags)0;

			if ((sc & 0x100) != 0 || (flags & KEYEVENTF_EXTENDEDKEY) != 0)
				keyFlags |= (KeysharpInputdClient.KeyEventFlags)KEYEVENTF_EXTENDEDKEY;

			// When no VK is available the daemon must use the scan-code path.
			if (vk == 0 && (sc & 0xFF) != 0)
				keyFlags |= (KeysharpInputdClient.KeyEventFlags)KEYEVENTF_SCANCODE;

			// Internal events (KeyPhysIgnore, KeyIgnore, KeyIgnoreAllExceptModifier) bypass
			// the hook chain so they are delivered to X11 directly without looping back as
			// new hook events. This eliminates the round-trips that would otherwise delay
			// processing of subsequent events (e.g. mouse clicks queued behind them).
			var synthFlags = IsIgnored((ulong)extraInfo)
				? KeysharpInputdClient.SynthFlags.BypassHook
				: KeysharpInputdClient.SynthFlags.None;

			if (eventType == KeyEventTypes.KeyUp || eventType == KeyEventTypes.KeyDownAndUp)
			{
				var upFlags = keyFlags | (KeysharpInputdClient.KeyEventFlags)KEYEVENTF_KEYUP;

				if (eventType == KeyEventTypes.KeyDownAndUp)
				{
					KeysharpInputdManager.SendInputViaSynthesisChannel(new[]
					{
						KeysharpInputdClient.Input.Key((ushort)vk, (ushort)(sc & 0xFF), keyFlags, extraInfo: (ulong)extraInfo),
						KeysharpInputdClient.Input.Key((ushort)vk, (ushort)(sc & 0xFF), upFlags,  extraInfo: (ulong)extraInfo),
					}, synthFlags);
					return;
				}

				keyFlags = upFlags;
			}

			KeysharpInputdManager.SendInputViaSynthesisChannel(new[]
			{
				KeysharpInputdClient.Input.Key((ushort)vk, (ushort)(sc & 0xFF), keyFlags, extraInfo: (ulong)extraInfo),
			}, synthFlags);
		}

		internal override void SendUnicodeChar(char ch, uint modifiers)
		{
			if (!Rune.TryCreate(ch, out var rune)
				|| !rune.IsAscii
				|| !UnixKeyboardMouseSender.UnixCharMapper.TryMapRuneToKeystroke(rune, out var vk, out var needShift, out var needAltGr)
				|| vk == 0)
			{
				SendDaemonUnicodeChar(ch, modifiers, KeyIgnoreLevel(ThreadAccessors.A_SendLevel));
				return;
			}

			var extraInfo = KeyIgnoreLevel(ThreadAccessors.A_SendLevel);
			uint transientModifiers = 0;

			if (needShift)
				transientModifiers |= MOD_LSHIFT;

			if (needAltGr)
				transientModifiers |= MOD_RALT;

			var targetModifiers = modifiers | transientModifiers;
			SetModifierLRState(
				targetModifiers,
				sendMode != SendModes.Event ? eventModifiersLR : GetModifierLRState(),
				0,
				false,
				true,
				extraInfo);
			SendKeyEvent(KeyEventTypes.KeyDownAndUp, vk, 0, 0, false, extraInfo);
			SetModifierLRState(
				modifiers,
				sendMode != SendModes.Event ? eventModifiersLR : targetModifiers,
				0,
				false,
				true,
				extraInfo);
		}

		private void SendDaemonUnicodeChar(char ch, uint modifiers, long extraInfo)
		{
			SetModifierLRState(
				modifiers,
				sendMode != SendModes.Event ? eventModifiersLR : GetModifierLRState(),
				0,
				false,
				true,
				extraInfo);

			var inputs = new[]
			{
				KeysharpInputdClient.Input.Key(0, (ushort)ch, (KeysharpInputdClient.KeyEventFlags)KEYEVENTF_UNICODE, extraInfo: (ulong)extraInfo),
				KeysharpInputdClient.Input.Key(0, (ushort)ch, (KeysharpInputdClient.KeyEventFlags)(KEYEVENTF_UNICODE | KEYEVENTF_KEYUP), extraInfo: (ulong)extraInfo),
			};

			KeysharpInputdManager.SendInputViaSynthesisChannel(inputs, KeysharpInputdClient.SynthFlags.BypassHook);

			SetModifierLRState(
				modifiers,
				sendMode != SendModes.Event ? eventModifiersLR : GetModifierLRState(),
				0,
				false,
				true,
				extraInfo);
		}

		private void QueueTextRune(List<KeysharpInputdClient.Input> events, Rune rune, uint modifiersLR, long extraInfo)
		{
			if (rune.IsAscii
				&& UnixKeyboardMouseSender.UnixCharMapper.TryMapRuneToKeystroke(rune, out var vk, out var needShift, out var needAltGr)
				&& vk != 0)
			{
				uint transientModifiers = 0;

				if (needShift)
					transientModifiers |= MOD_LSHIFT;

				if (needAltGr)
					transientModifiers |= MOD_RALT;

				QueueModifierTransition(events, transientModifiers & ~modifiersLR, true, extraInfo);
				QueueKey(events, KeyEventTypes.KeyDownAndUp, vk, extraInfo);
				QueueModifierTransition(events, transientModifiers & ~modifiersLR, false, extraInfo);
				return;
			}

			Span<char> chars = stackalloc char[2];
			var length = rune.EncodeToUtf16(chars);

			for (var i = 0; i < length; i++)
				QueueUnicodeUnit(events, chars[i], extraInfo);
		}

		private static void QueueUnicodeUnit(List<KeysharpInputdClient.Input> events, char ch, long extraInfo)
		{
			events.Add(KeysharpInputdClient.Input.Key(
				0,
				ch,
				(KeysharpInputdClient.KeyEventFlags)KEYEVENTF_UNICODE,
				extraInfo: (ulong)extraInfo));
			events.Add(KeysharpInputdClient.Input.Key(
				0,
				ch,
				(KeysharpInputdClient.KeyEventFlags)(KEYEVENTF_UNICODE | KEYEVENTF_KEYUP),
				extraInfo: (ulong)extraInfo));
		}

		private static void QueueModifierTransition(List<KeysharpInputdClient.Input> events, uint modifiers, bool down, long extraInfo)
		{
			if ((modifiers & MOD_LSHIFT) != 0)
				QueueKey(events, down ? KeyEventTypes.KeyDown : KeyEventTypes.KeyUp, VK_LSHIFT, extraInfo);

			if ((modifiers & MOD_RALT) != 0)
				QueueKey(events, down ? KeyEventTypes.KeyDown : KeyEventTypes.KeyUp, VK_RMENU, extraInfo);
		}

		private static void QueueKey(List<KeysharpInputdClient.Input> events, KeyEventTypes eventType, uint vk, long extraInfo)
		{
			var downFlags = (KeysharpInputdClient.KeyEventFlags)0;
			var upFlags = (KeysharpInputdClient.KeyEventFlags)KEYEVENTF_KEYUP;

			if (eventType != KeyEventTypes.KeyUp)
				events.Add(KeysharpInputdClient.Input.Key((ushort)vk, 0, downFlags, extraInfo: (ulong)extraInfo));

			if (eventType != KeyEventTypes.KeyDown)
				events.Add(KeysharpInputdClient.Input.Key((ushort)vk, 0, upFlags, extraInfo: (ulong)extraInfo));
		}

		internal override void MouseEvent(uint eventFlags, uint data, int x = CoordUnspecified, int y = CoordUnspecified)
		{
			if (sendMode != SendModes.Event)
			{
				PutMouseEventIntoArray(eventFlags, data, x, y);
				return;
			}

			// Immediate single mouse event — no bypass, visible in hook chain.
			var single = new[]
			{
				KeysharpInputdClient.Input.MouseEvent(
					dx: x == CoordUnspecified ? 0 : x,
					dy: y == CoordUnspecified ? 0 : y,
					mouseData: data,
					flags: (KeysharpInputdClient.MouseEventFlags)eventFlags),
			};

			KeysharpInputdManager.SendInputViaSynthesisChannel(single);
		}

		internal override void MouseMove(ref int x, ref int y, ref uint eventFlags, long speed, bool moveOffset)
		{
			if (x == CoordUnspecified || y == CoordUnspecified)
				return;

			if (moveOffset)
			{
				if (sendMode == SendModes.Event)
					SendRelativeMouseMove(x, y);
				else
					QueueRelativeMouseMove(x, y);

				DoMouseDelay();
				eventFlags = 0;
				x = CoordUnspecified;
				y = CoordUnspecified;
				return;
			}

			POINT current = default;
			var haveCurrent = false;
			var targetX = x;
			var targetY = y;

			if (sendMode == SendModes.Input && sendInputCursorPos.X != CoordUnspecified)
			{
				current = sendInputCursorPos;
				haveCurrent = true;
			}
			else if (GetCursorPos(out current))
			{
				haveCurrent = true;
			}

			CoordToScreen(ref targetX, ref targetY, CoordMode.Mouse);

			if (!haveCurrent)
				return;

			if (sendMode == SendModes.Input)
			{
				if (haveCurrent)
				{
					sendInputCursorPos.X = targetX;
					sendInputCursorPos.Y = targetY;
				}

				PutMouseEventIntoArray(
					(uint)MOUSEEVENTF.MOVE | (uint)MOUSEEVENTF.ABSOLUTE,
					0,
					MouseCoordToAbs(targetX, (int)A_ScreenWidth),
					MouseCoordToAbs(targetY, (int)A_ScreenHeight));
				DoMouseDelay();
				eventFlags = 0;
				x = CoordUnspecified;
				y = CoordUnspecified;
				return;
			}

			if (speed < 0)
				speed = 0;
			else if (speed > MaxMouseSpeed)
				speed = MaxMouseSpeed;

			if (speed == 0 || !haveCurrent)
			{
				MouseEvent(
					(uint)MOUSEEVENTF.MOVE | (uint)MOUSEEVENTF.ABSOLUTE,
					0,
					MouseCoordToAbs(targetX, (int)A_ScreenWidth),
					MouseCoordToAbs(targetY, (int)A_ScreenHeight));
				DoMouseDelay();
			}
			else
			{
				var steps = Math.Max(1, (int)speed);
				var previousX = current.X;
				var previousY = current.Y;

				for (var step = 1; step <= steps; step++)
				{
					var nextX = current.X + ((targetX - current.X) * step / steps);
					var nextY = current.Y + ((targetY - current.Y) * step / steps);
					var stepDx = nextX - previousX;
					var stepDy = nextY - previousY;

					if (stepDx != 0 || stepDy != 0)
					{
						MouseEvent(
							(uint)MOUSEEVENTF.MOVE | (uint)MOUSEEVENTF.ABSOLUTE,
							0,
							MouseCoordToAbs(nextX, (int)A_ScreenWidth),
							MouseCoordToAbs(nextY, (int)A_ScreenHeight));
					}

					previousX = nextX;
					previousY = nextY;
					DoMouseDelay();
				}
			}

			eventFlags = 0;
			x = CoordUnspecified;
			y = CoordUnspecified;
		}

		private static void SendRelativeMouseMove(int dx, int dy)
		{
			if (dx == 0 && dy == 0)
				return;

			var stepCount = Math.Max(Math.Abs(dx), Math.Abs(dy));
			var events = new List<KeysharpInputdClient.Input>(Math.Min(stepCount, MaxMouseMoveChunk));
			var previousX = 0;
			var previousY = 0;

			for (var step = 1; step <= stepCount; step++)
			{
				var nextX = dx * step / stepCount;
				var nextY = dy * step / stepCount;
				var stepDx = nextX - previousX;
				var stepDy = nextY - previousY;

				if (stepDx != 0 || stepDy != 0)
				{
					events.Add(KeysharpInputdClient.Input.MouseEvent(
						stepDx,
						stepDy,
						0,
						KeysharpInputdClient.MouseEventFlags.Move));
				}

				if (events.Count == MaxMouseMoveChunk)
				{
					KeysharpInputdManager.SendInputViaSynthesisChannel(events);
					events.Clear();
				}

				previousX = nextX;
				previousY = nextY;
			}

			if (events.Count != 0)
				KeysharpInputdManager.SendInputViaSynthesisChannel(events);
		}

		private void QueueRelativeMouseMove(int dx, int dy)
		{
			if (dx == 0 && dy == 0)
				return;

			var stepCount = Math.Max(Math.Abs(dx), Math.Abs(dy));
			var previousX = 0;
			var previousY = 0;

			for (var step = 1; step <= stepCount; step++)
			{
				var nextX = dx * step / stepCount;
				var nextY = dy * step / stepCount;
				var stepDx = nextX - previousX;
				var stepDy = nextY - previousY;

				if (stepDx != 0 || stepDy != 0)
					PutMouseEventIntoArray((uint)MOUSEEVENTF.MOVE, 0, stepDx, stepDy);

				previousX = nextX;
				previousY = nextY;
			}
		}

		internal override ToggleValueType ToggleKeyState(uint vk, ToggleValueType toggleValue)
		{
			// Read current state from the hook thread's tracked key state.
			var ht = Script.TheScript.HookThread;
			var current = ht != null && ht.IsKeyToggledOn(vk)
				? ToggleValueType.On
				: ToggleValueType.Off;

			if (toggleValue == ToggleValueType.On && current == ToggleValueType.Off
				|| toggleValue == ToggleValueType.Off && current == ToggleValueType.On)
			{
				// Send a down+up to toggle.
				SendKeybdEvent(KeyEventTypes.KeyDownAndUp, vk, 0, 0, KeyIgnore);
			}

			return current;
		}

		// Absolute mouse coordinate normalisation: 0..65535 range, same as Windows MOUSEEVENTF_ABSOLUTE.
		internal override int MouseCoordToAbs(int coord, int widthOrHeight)
			=> widthOrHeight <= 0 ? 0 : (int)((long)coord * 65535 / widthOrHeight);

		// These have no meaningful equivalent on Linux with the daemon.
		internal override nint GetFocusedKeybdLayout(nint window) => nint.Zero;
		internal override ResultType LayoutHasAltGrDirect(nint layout) => ResultType.Fail;

		// No-ops: Windows-specific thread attachment for cross-process sends.
		internal override void AttachTargetWindowThread(
			ref bool threadsAreAttached, ref uint keybdLayoutThread,
			ref WindowItemBase tempitem, nint targetWindow) { }

		internal override void DetachTargetWindowThread(uint mainThread, uint targetThread) { }

		// Progress callbacks — no UI pump needed.
		protected internal override void LongOperationUpdate() { }
		protected internal override void LongOperationUpdateForSendKeys() { }

		// No hook registration needed — daemon manages the hook subscription separately.
		protected override void RegisterHook() { }
	}
}
#endif
