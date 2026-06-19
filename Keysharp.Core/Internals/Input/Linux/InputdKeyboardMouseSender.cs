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
	/// </summary>
	internal sealed class InputdKeyboardMouseSender : KeyboardMouseSender
	{
		// KEYEVENTF_* constants equal KSI_KEYEVENTF_* by design.
		private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
		private const uint KEYEVENTF_KEYUP = 0x0002;
		private const uint KEYEVENTF_UNICODE = 0x0004;
		private const uint KEYEVENTF_SCANCODE = 0x0008;
		private const int MaxInputdBatchSize = 1024;
		private const int MaxMouseMoveChunk = 1000;

		private readonly List<InputdQueuedEvent> eventQueue = new(MaxInitialEventsSI);

		private readonly struct InputdQueuedEvent
		{
			internal readonly KeysharpInputdClient.Input Input;
			internal readonly int DelayMs;
			internal readonly bool IsDelay;

			private InputdQueuedEvent(KeysharpInputdClient.Input input, int delayMs, bool isDelay)
			{
				Input = input;
				DelayMs = delayMs;
				IsDelay = isDelay;
			}

			internal static InputdQueuedEvent FromInput(KeysharpInputdClient.Input input) => new(input, 0, false);
			internal static InputdQueuedEvent Delay(int delayMs) => new(default, delayMs, true);
		}

		internal override bool MouseButtonsSwapped => false;

		protected internal override bool TrySendPlatformRawText(ReadOnlySpan<char> text, ref int keyIndex, uint modifiersLR)
		{
			if (keyIndex < 0 || keyIndex >= text.Length)
				return false;

			var localEvents = sendMode == SendModes.Event ? new List<KeysharpInputdClient.Input>() : null;
			var extraInfo = KeyIgnoreLevel(ThreadAccessors.A_SendLevel);

			for (var i = keyIndex; i < text.Length; i++)
			{
				var ch = text[i];

				if (ch == '\r')
				{
					if (i + 1 < text.Length && text[i + 1] == '\n')
						i++;

					QueueKey(localEvents, KeyEventTypes.KeyDownAndUp, VK_RETURN, extraInfo);
					continue;
				}

				if (ch == '\n')
				{
					QueueKey(localEvents, KeyEventTypes.KeyDownAndUp, VK_RETURN, extraInfo);
					continue;
				}

				if (ch == '\b')
				{
					QueueKey(localEvents, KeyEventTypes.KeyDownAndUp, VK_BACK, extraInfo);
					continue;
				}

				if (ch == '\t')
				{
					QueueKey(localEvents, KeyEventTypes.KeyDownAndUp, VK_TAB, extraInfo);
					continue;
				}

				if (Rune.DecodeFromUtf16(text[i..], out var rune, out var charsConsumed) != OperationStatus.Done)
					continue;

				i += charsConsumed - 1;
				QueueTextRune(localEvents, rune, modifiersLR, extraInfo);
			}

			if (localEvents != null && localEvents.Count != 0)
				SendInputBatches(localEvents);

			keyIndex = text.Length - 1;
			return true;
		}

		internal override void InitEventArray(int maxEvents, uint modifiersLR)
		{
			eventQueue.Clear();
			base.maxEvents = maxEvents;
			eventModifiersLR = modifiersLR;
			sendInputCursorPos.X = CoordUnspecified;
			sendInputCursorPos.Y = CoordUnspecified;
			abortArraySend = false;
		}

		internal override void CleanupEventArray(long finalKeyDelay)
		{
			eventQueue.Clear();

			// Do this before any final corrective delay/keystrokes so they are emitted immediately.
			sendMode = SendModes.Event;
			DoKeyDelay(finalKeyDelay);
		}

		internal override int SiEventCount() => eventQueue.Count;
		internal override int PbEventCount() => 0;

		// No journal-playback hook via uinput/inputd; SendPlay is sent as SendEvent (see WarnIfPlayUnsupported).
		protected override bool SupportsPlayMode => false;

		internal override void PutKeybdEventIntoArray(uint keyAsModifiersLR, uint vk, uint sc, uint eventFlags, long extraInfo)
		{
			if (vk == 0 && sc == 0 && eventFlags == 0)
			{
				eventQueue.Add(InputdQueuedEvent.Delay((int)extraInfo));
				return;
			}

			var flags = (KeysharpInputdClient.KeyEventFlags)eventFlags;
			var isKeyUp = (eventFlags & KEYEVENTF_KEYUP) != 0;
			var isUnicode = (eventFlags & KEYEVENTF_UNICODE) != 0;

			if (isKeyUp)
				eventModifiersLR &= ~keyAsModifiersLR;
			else
				eventModifiersLR |= keyAsModifiersLR;

			if (!isUnicode)
			{
				if ((sc & 0x100) != 0)
					flags |= (KeysharpInputdClient.KeyEventFlags)KEYEVENTF_EXTENDEDKEY;

				if (vk == 0 && (sc & 0xFF) != 0)
					flags |= (KeysharpInputdClient.KeyEventFlags)KEYEVENTF_SCANCODE;

				sc &= 0xFF;
			}

			QueueInput(KeysharpInputdClient.Input.Key(
				vk: (ushort)vk,
				scan: (ushort)sc,
				flags: flags,
				extraInfo: (ulong)extraInfo));
		}

		internal override void PutMouseEventIntoArray(uint eventFlags, uint data, int x, int y)
		{
			QueueInput(KeysharpInputdClient.Input.MouseEvent(
				dx: x == CoordUnspecified ? 0 : x,
				dy: y == CoordUnspecified ? 0 : y,
				mouseData: data,
				flags: (KeysharpInputdClient.MouseEventFlags)eventFlags,
				extraInfo: (ulong)KeyIgnoreLevel(ThreadAccessors.A_SendLevel)));
		}

		internal override void SendEventArray(ref long finalKeyDelay, uint modsDuringSend)
		{
			if (eventQueue.Count == 0)
				return;

			// AHK's SendInput removes the keyboard hook for the duration so the
			// LL hook never sees the synth events; AHK then reconciles
			// g_modifiersLR_logical from the OS state afterward (see
			// keyboard_mouse.cpp:2755-2776). Mirror that here: ask the daemon
			// to bypass the hook lane for these events (so they reach uinput
			// but no client hook callback fires), then reconcile state once
			// the batch is fully flushed.
			try
			{
				var batch = new List<KeysharpInputdClient.Input>(Math.Min(eventQueue.Count, MaxInputdBatchSize));

				for (var i = 0; i < eventQueue.Count; i++)
				{
					var ev = eventQueue[i];

					if (!ev.IsDelay)
					{
						batch.Add(ev.Input);
						continue;
					}

					SendInputBatches(batch, KeysharpInputdClient.SynthFlags.BypassHook);
					batch.Clear();

					if (i == eventQueue.Count - 1)
						finalKeyDelay = ev.DelayMs;
					else if (ev.DelayMs > 0)
						Keysharp.Internals.Flow.SleepWithoutInterruption(ev.DelayMs);
				}

				SendInputBatches(batch, KeysharpInputdClient.SynthFlags.BypassHook);
			}
			finally
			{
				eventQueue.Clear();
			}

			ReconcileLogicalModifiersFromOs();
		}

		/// <summary>
		/// Mirrors AHK's post-SendInput state reconciliation: after a bypass-hook
		/// batch the daemon's hook lane did not echo the synth events back to us,
		/// so modifiersLRLogical may be stale. Query inputd for the ground-truth
		/// physical modifier state (which is X11-independent and always correct),
		/// update logical state to match, and also refresh the indicator snapshot.
		/// </summary>
		private static void ReconcileLogicalModifiersFromOs()
		{
			var ht = Script.TheScript.HookThread;
			var sender = ht.kbdMsSender;

			if (sender == null)
				return;

			if (KeysharpInputdManager.TryGetKeyState(out var physMods, out var capsOn, out var numOn, out var scrollOn))
			{
				// inputd returns only physical key state. Preserve any logical-only bits
				// (synthetic modifiers that were explicitly placed by the caller and not
				// yet released) by keeping bits that are in logical but not physical only
				// if they're also in the current event-mode modifier set. In practice,
				// after a bypass-hook batch, all transient synthetic modifiers should have
				// been released by SetModifierLRState before the batch finished, so the
				// physical state IS the correct post-Send state.
				sender.modifiersLRLogical = physMods;
				sender.modifiersLRLogicalNonIgnored = physMods;

				// Refresh the indicator snapshot while we have fresh data.
				if (ht is Keysharp.Internals.Input.Hooks.Unix.UnixHookThread uht)
					uht.SetIndicatorSnapshot(capsOn, numOn, scrollOn);
			}
			else
			{
				// inputd unavailable: fall back to OS-level query (X11 or physical snapshot).
				var modsCurrent = sender.GetModifierLRState(true);
				sender.modifiersLRLogical = sender.modifiersLRLogicalNonIgnored = modsCurrent;
			}
		}

		/// <summary>
		/// Overrides the base toggle-key logic to use a live inputd hardware query
		/// for the post-toggle check instead of the snapshot + Thread.Sleep(1).
		///
		/// The base class sends a CapsLock/NumLock/ScrollLock keypress and then
		/// waits 1 ms before re-checking IsKeyToggledOn to confirm the toggle.
		/// On the inputd path that 1 ms is far too short: the synthetic event must
		/// travel Keysharp→inputd→uinput→compositor→EV_LED→inputd before the
		/// cached indicator state updates. Reading the hardware LED directly via
		/// GET_KEY_STATE (which calls EVIOCGLED) gives the correct answer
		/// immediately because the kernel updates the LED synchronously when it
		/// processes the uinput event.
		/// </summary>
		internal override ToggleValueType ToggleKeyState(uint vk, ToggleValueType toggleValue)
		{
			var script = Script.TheScript;

			// IsKeyToggledOn now always queries live (X11 → inputd → snapshot fallback),
			// so startingState is always the compositor's actual current state.
			var startingState = script.HookThread.IsKeyToggledOn(vk) ? ToggleValueType.On : ToggleValueType.Off;

			if (toggleValue != ToggleValueType.On && toggleValue != ToggleValueType.Off)
				return startingState;

			if (startingState == toggleValue)
				return startingState;

			// Release if held (prevents the toggle being swallowed by the OS).
			if (script.HookThread.IsKeyDown(vk))
				SendKeyEvent(KeyEventTypes.KeyUp, vk);

			SendKeyEvent(KeyEventTypes.KeyDownAndUp, vk);

			// Give the compositor a moment to update its XKB state, then verify.
			// XkbGetState returns the post-toggle state within a few ms.
			System.Threading.Thread.Sleep(5);

			if (vk == VK_CAPITAL && toggleValue == ToggleValueType.Off && script.HookThread.IsKeyToggledOn(vk))
			{
				// Some keyboard layouts only toggle CapsLock off via Shift.
				SendKeyEvent(KeyEventTypes.KeyDownAndUp, VK_SHIFT);
			}

			return startingState;
		}

		/// <summary>
		/// Immediate single-event send, equivalent to keybd_event() on Windows.
		/// The event always round-trips through the daemon's hook lane so
		/// AllowIt's UpdateKeybdState updates modifiersLRLogical — matching
		/// AHK's keybd_event semantics, where the LL hook still sees the
		/// injected event (and routes it through AllowIt because of the
		/// ignore marker) rather than skipping it. The bypass-hook flag is
		/// reserved for the SendInput-mode batch path.
		/// </summary>
		internal override void SendKeybdEvent(KeyEventTypes eventType, uint vk, uint sc, uint flags, long extraInfo)
		{
			var keyFlags = (KeysharpInputdClient.KeyEventFlags)flags;

			if ((sc & 0x100) != 0)
				keyFlags |= (KeysharpInputdClient.KeyEventFlags)KEYEVENTF_EXTENDEDKEY;

			if (vk == 0 && (sc & 0xFF) != 0)
				keyFlags |= (KeysharpInputdClient.KeyEventFlags)KEYEVENTF_SCANCODE;

			sc &= 0xFF;

			if (eventType == KeyEventTypes.KeyDownAndUp)
			{
				SendInputBatches(
				[
					KeysharpInputdClient.Input.Key((ushort)vk, (ushort)sc, keyFlags, extraInfo: (ulong)extraInfo),
					KeysharpInputdClient.Input.Key((ushort)vk, (ushort)sc, keyFlags | (KeysharpInputdClient.KeyEventFlags)KEYEVENTF_KEYUP, extraInfo: (ulong)extraInfo),
				]);
				return;
			}

			if (eventType == KeyEventTypes.KeyUp)
				keyFlags |= (KeysharpInputdClient.KeyEventFlags)KEYEVENTF_KEYUP;

			SendInputBatches(
			[
				KeysharpInputdClient.Input.Key((ushort)vk, (ushort)sc, keyFlags, extraInfo: (ulong)extraInfo),
			]);
		}

		internal override void SendUnicodeChar(char ch, uint modifiers)
		{
			var extraInfo = KeyIgnoreLevel(ThreadAccessors.A_SendLevel);

			if (!Rune.TryCreate(ch, out var rune)
				|| !rune.IsAscii
				|| !KeyCodes.TryMapRuneToKeystroke(rune, out var vk, out var needShift, out var needAltGr)
				|| vk == 0)
			{
				SendDaemonUnicodeChar(ch, modifiers, extraInfo);
				return;
			}

			uint transientModifiers = 0;

			if (needShift)
				transientModifiers |= MOD_LSHIFT;

			if (needAltGr)
				transientModifiers |= MOD_RALT;

			var targetModifiers = modifiers | transientModifiers;
			SetModifierLRState(targetModifiers, sendMode != SendModes.Event ? eventModifiersLR : GetModifierLRState(), 0, false, true, extraInfo);
			SendKeyEvent(KeyEventTypes.KeyDownAndUp, vk, 0, 0, false, extraInfo);
			SetModifierLRState(modifiers, sendMode != SendModes.Event ? eventModifiersLR : targetModifiers, 0, false, true, extraInfo);
		}

		private void SendDaemonUnicodeChar(char ch, uint modifiers, long extraInfo)
		{
			SetModifierLRState(modifiers, sendMode != SendModes.Event ? eventModifiersLR : GetModifierLRState(), 0, false, true, extraInfo);

			var inputs = new[]
			{
				KeysharpInputdClient.Input.Key(0, (ushort)ch, (KeysharpInputdClient.KeyEventFlags)KEYEVENTF_UNICODE, extraInfo: (ulong)extraInfo),
				KeysharpInputdClient.Input.Key(0, (ushort)ch, (KeysharpInputdClient.KeyEventFlags)(KEYEVENTF_UNICODE | KEYEVENTF_KEYUP), extraInfo: (ulong)extraInfo),
			};

			if (sendMode != SendModes.Event)
			{
				foreach (var input in inputs)
					QueueInput(input);
			}
			else
				SendInputBatches(inputs);

			SetModifierLRState(modifiers, sendMode != SendModes.Event ? eventModifiersLR : GetModifierLRState(), 0, false, true, extraInfo);
		}

		private void QueueTextRune(List<KeysharpInputdClient.Input> events, Rune rune, uint modifiersLR, long extraInfo)
		{
			if (rune.IsAscii
				&& KeyCodes.TryMapRuneToKeystroke(rune, out var vk, out var needShift, out var needAltGr)
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

		private void QueueUnicodeUnit(List<KeysharpInputdClient.Input> events, char ch, long extraInfo)
		{
			AddOrQueue(events, KeysharpInputdClient.Input.Key(
				0,
				ch,
				(KeysharpInputdClient.KeyEventFlags)KEYEVENTF_UNICODE,
				extraInfo: (ulong)extraInfo));
			AddOrQueue(events, KeysharpInputdClient.Input.Key(
				0,
				ch,
				(KeysharpInputdClient.KeyEventFlags)(KEYEVENTF_UNICODE | KEYEVENTF_KEYUP),
				extraInfo: (ulong)extraInfo));
		}

		private void QueueModifierTransition(List<KeysharpInputdClient.Input> events, uint modifiers, bool down, long extraInfo)
		{
			if ((modifiers & MOD_LSHIFT) != 0)
				QueueKey(events, down ? KeyEventTypes.KeyDown : KeyEventTypes.KeyUp, VK_LSHIFT, extraInfo);

			if ((modifiers & MOD_RALT) != 0)
				QueueKey(events, down ? KeyEventTypes.KeyDown : KeyEventTypes.KeyUp, VK_RMENU, extraInfo);
		}

		private void QueueKey(List<KeysharpInputdClient.Input> events, KeyEventTypes eventType, uint vk, long extraInfo)
		{
			var downFlags = (KeysharpInputdClient.KeyEventFlags)0;
			var upFlags = (KeysharpInputdClient.KeyEventFlags)KEYEVENTF_KEYUP;

			if (eventType != KeyEventTypes.KeyUp)
				AddOrQueue(events, KeysharpInputdClient.Input.Key((ushort)vk, 0, downFlags, extraInfo: (ulong)extraInfo));

			if (eventType != KeyEventTypes.KeyDown)
				AddOrQueue(events, KeysharpInputdClient.Input.Key((ushort)vk, 0, upFlags, extraInfo: (ulong)extraInfo));
		}

		internal override void MouseEvent(uint eventFlags, uint data, int x = CoordUnspecified, int y = CoordUnspecified)
		{
			// Route button and scroll events through the compositor backend when available,
			// regardless of sendMode. MOVE events are handled in MouseMove below.
			// This must run before the sendMode check so that Input-mode batches don't
			// bypass the compositor path and end up using inputd's uinput absolute
			// coordinates, which do not map correctly on Wayland compositors.
			if ((eventFlags & (uint)MOUSEEVENTF.MOVE) == 0)
			{
				var gnomeMouse = WaylandMouseBackend();

				if (gnomeMouse != null && TryRouteToGnome(gnomeMouse, eventFlags, data))
					return;
			}

			if (sendMode != SendModes.Event)
			{
				PutMouseEventIntoArray(eventFlags, data, x, y);
				return;
			}

			SendInputBatches(
			[
				KeysharpInputdClient.Input.MouseEvent(
					dx: x == CoordUnspecified ? 0 : x,
					dy: y == CoordUnspecified ? 0 : y,
					mouseData: data,
					flags: (KeysharpInputdClient.MouseEventFlags)eventFlags,
					extraInfo: (ulong)KeyIgnoreLevel(ThreadAccessors.A_SendLevel)),
			]);
		}

		internal override void MouseMove(ref int x, ref int y, ref uint eventFlags, long speed, bool moveOffset)
		{
			if (x == CoordUnspecified || y == CoordUnspecified)
				return;

			// Relative moves: keep using inputd (uinput relative events work fine on Wayland).
			if (moveOffset)
			{
				if (sendMode == SendModes.Event)
					SendRelativeMouseMove(x, y, (ulong)KeyIgnoreLevel(ThreadAccessors.A_SendLevel));
				else
					QueueRelativeMouseMove(x, y);

				DoMouseDelay();
				eventFlags = 0;
				x = CoordUnspecified;
				y = CoordUnspecified;
				return;
			}

			var targetX = x;
			var targetY = y;
			CoordToScreen(ref targetX, ref targetY, CoordMode.Mouse);

			// Absolute moves: prefer the compositor backend for all send modes so the
			// cursor lands at the exact screen-pixel position. inputd's uinput path
			// normalises coordinates to 0-65535 and the round-trip back to screen
			// pixels is unreliable on Wayland compositors. This must be checked before
			// the sendMode branch so that Input-mode MouseMove calls use the compositor
			// path rather than queuing a normalised event for inputd.
			{
				var gnomeMouse = WaylandMouseBackend();

				if (gnomeMouse != null)
				{
					if (speed < 0)
						speed = 0;
					else if (speed > MaxMouseSpeed)
						speed = MaxMouseSpeed;

					if (speed == 0 || !GetCursorPos(out var current))
					{
						gnomeMouse.TrySendMouseMoveAbsolute(targetX, targetY);
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

							if (nextX != previousX || nextY != previousY)
								gnomeMouse.TrySendMouseMoveAbsolute(nextX, nextY);

							previousX = nextX;
							previousY = nextY;
							DoMouseDelay();
						}
					}

					eventFlags = (uint)MOUSEEVENTF.MOVE | (uint)MOUSEEVENTF.ABSOLUTE;
					x = targetX;
					y = targetY;
					return;
				}
			}

			// inputd / uinput fallback: normalise to the 0-65535 absolute range. The daemon's uinput
			// absolute pointer maps [0,65535] across the WHOLE virtual desktop (the union of all
			// monitors), so normalise against the virtual bounds and offset by its (possibly negative)
			// origin rather than the primary screen size -- otherwise the cursor lands at the wrong
			// place on a multi-monitor setup. For a single monitor at the origin this is identical
			// to the previous A_ScreenWidth/Height behaviour.
			var vb = Keysharp.Builtins.Monitor.GetVirtualScreenBounds();
			var absTargetX = MouseCoordToAbs(targetX - (int)vb.Left, (int)vb.Width);
			var absTargetY = MouseCoordToAbs(targetY - (int)vb.Top, (int)vb.Height);

			if (sendMode == SendModes.Input)
			{
				sendInputCursorPos.X = targetX;
				sendInputCursorPos.Y = targetY;
				PutMouseEventIntoArray(
					(uint)MOUSEEVENTF.MOVE | (uint)MOUSEEVENTF.ABSOLUTE,
					0,
					absTargetX,
					absTargetY);
				DoMouseDelay();
				eventFlags = (uint)MOUSEEVENTF.MOVE | (uint)MOUSEEVENTF.ABSOLUTE;
				x = absTargetX;
				y = absTargetY;
				return;
			}

			if (speed < 0)
				speed = 0;
			else if (speed > MaxMouseSpeed)
				speed = MaxMouseSpeed;

			if (speed == 0 || !GetCursorPos(out var cur))
			{
				MouseEvent(
					(uint)MOUSEEVENTF.MOVE | (uint)MOUSEEVENTF.ABSOLUTE,
					0,
					absTargetX,
					absTargetY);
				DoMouseDelay();
			}
			else
			{
				var steps = Math.Max(1, (int)speed);
				var previousX = cur.X;
				var previousY = cur.Y;

				for (var step = 1; step <= steps; step++)
				{
					var nextX = cur.X + ((targetX - cur.X) * step / steps);
					var nextY = cur.Y + ((targetY - cur.Y) * step / steps);
					var stepDx = nextX - previousX;
					var stepDy = nextY - previousY;

					if (stepDx != 0 || stepDy != 0)
					{
						MouseEvent(
							(uint)MOUSEEVENTF.MOVE | (uint)MOUSEEVENTF.ABSOLUTE,
							0,
							MouseCoordToAbs(nextX - (int)vb.Left, (int)vb.Width),
							MouseCoordToAbs(nextY - (int)vb.Top, (int)vb.Height));
					}

					previousX = nextX;
					previousY = nextY;
					DoMouseDelay();
				}
			}

			eventFlags = (uint)MOUSEEVENTF.MOVE | (uint)MOUSEEVENTF.ABSOLUTE;
			x = absTargetX;
			y = absTargetY;
		}

		// Returns the GNOME compositor backend if it supports mouse simulation,
		// null otherwise (non-Wayland session, or extension not running).
		private static Keysharp.Internals.Window.Linux.Wayland.IWaylandBackend WaylandMouseBackend()
		{
			if (!PlatformManager.IsWaylandSession)
				return null;

			var b = Keysharp.Internals.Window.Linux.Wayland.WaylandBackend.Current;
			return b?.SupportsMouse == true ? b : null;
		}

		// Maps MOUSEEVENTF button/scroll flags to GNOME D-Bus calls.
		// Returns false if the flag is not handled here (e.g. MOVE-only events).
		private static bool TryRouteToGnome(
			Keysharp.Internals.Window.Linux.Wayland.IWaylandBackend gnomeMouse,
			uint flags, uint data)
		{
			if ((flags & (uint)MOUSEEVENTF.LEFTDOWN) != 0)   return gnomeMouse.TrySendMouseButton(1, true);
			if ((flags & (uint)MOUSEEVENTF.LEFTUP) != 0)     return gnomeMouse.TrySendMouseButton(1, false);
			if ((flags & (uint)MOUSEEVENTF.RIGHTDOWN) != 0)  return gnomeMouse.TrySendMouseButton(3, true);
			if ((flags & (uint)MOUSEEVENTF.RIGHTUP) != 0)    return gnomeMouse.TrySendMouseButton(3, false);
			if ((flags & (uint)MOUSEEVENTF.MIDDLEDOWN) != 0) return gnomeMouse.TrySendMouseButton(2, true);
			if ((flags & (uint)MOUSEEVENTF.MIDDLEUP) != 0)   return gnomeMouse.TrySendMouseButton(2, false);
			if ((flags & (uint)MOUSEEVENTF.XDOWN) != 0)
				return gnomeMouse.TrySendMouseButton((data & 0x0001u) != 0 ? 8u : 9u, true);
			if ((flags & (uint)MOUSEEVENTF.XUP) != 0)
				return gnomeMouse.TrySendMouseButton((data & 0x0001u) != 0 ? 8u : 9u, false);
			// Wheel delta is a signed 16-bit value packed into the low word of data.
			if ((flags & (uint)MOUSEEVENTF.WHEEL) != 0)
				return gnomeMouse.TrySendMouseScroll(unchecked((short)(ushort)(data & 0xFFFF)), true);
			if ((flags & (uint)MOUSEEVENTF.HWHEEL) != 0)
				return gnomeMouse.TrySendMouseScroll(unchecked((short)(ushort)(data & 0xFFFF)), false);
			return false;
		}

		private static void SendRelativeMouseMove(int dx, int dy, ulong extraInfo)
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
						KeysharpInputdClient.MouseEventFlags.Move,
						extraInfo: extraInfo));
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


		internal override int MouseCoordToAbs(int coord, int widthOrHeight)
			=> widthOrHeight <= 0 ? 0 : ((65536 * coord) / widthOrHeight) + (coord < 0 ? -1 : 1);

		internal override nint GetFocusedKeybdLayout(nint window) => nint.Zero;
		internal override ResultType LayoutHasAltGrDirect(nint layout) => ResultType.ConditionFalse;

		internal override void AttachTargetWindowThread(
			ref bool threadsAreAttached, ref uint keybdLayoutThread,
			ref WindowItemBase tempitem, nint targetWindow) { }

		internal override void DetachTargetWindowThread(uint mainThread, uint targetThread) { }

		protected internal override void LongOperationUpdate() { }
		protected internal override void LongOperationUpdateForSendKeys() { }

		protected override void RegisterHook() { }

		private void QueueInput(KeysharpInputdClient.Input input)
			=> eventQueue.Add(InputdQueuedEvent.FromInput(input));

		private void AddOrQueue(List<KeysharpInputdClient.Input> events, KeysharpInputdClient.Input input)
		{
			if (events != null)
				events.Add(input);
			else
				QueueInput(input);
		}

		private static void SendInputBatches(IReadOnlyList<KeysharpInputdClient.Input> inputs, KeysharpInputdClient.SynthFlags flags = KeysharpInputdClient.SynthFlags.None)
		{
			if (inputs.Count == 0)
				return;

			if (inputs.Count <= MaxInputdBatchSize)
			{
				KeysharpInputdManager.SendInputViaSynthesisChannel(inputs, flags);
				return;
			}

			for (var offset = 0; offset < inputs.Count; offset += MaxInputdBatchSize)
			{
				var count = Math.Min(MaxInputdBatchSize, inputs.Count - offset);
				var batch = new KeysharpInputdClient.Input[count];

				for (var i = 0; i < count; i++)
					batch[i] = inputs[offset + i];

				KeysharpInputdManager.SendInputViaSynthesisChannel(batch, flags);
			}
		}
	}
}
#endif
