using Keysharp.Builtins;
#if LINUX
using System;
using System.Collections.Generic;
using System.Text;
using SharpHook.Data;
using static Keysharp.Internals.Input.Keyboard.KeyboardUtils;
using static Keysharp.Internals.Input.Keyboard.VirtualKeys;

namespace Keysharp.Internals.Input.Linux
{
		internal sealed class LinuxKeyboardMouseSender : UnixKeyboardMouseSender, IDisposable
		{
		private static readonly uint[] unicodeInjectionFallbackVks =
		[
			VK_F24, VK_F23, VK_F22, VK_F21, VK_F20, VK_F19, VK_F18, VK_F17,
			VK_F16, VK_F15, VK_F14, VK_F13
		];
		private static readonly int unicodeRemapDelayMs = GetUnicodeRemapDelayMs();
		private static readonly int unicodeRestoreDelayMs = GetUnicodeRestoreDelayMs();
			private readonly object heldUnicodeRemapGate = new();
			private readonly Dictionary<char, RemappedKeycodeSlot> heldUnicodeRemaps = [];
			private readonly object suspendedKeyGrabGate = new();
			private readonly Dictionary<uint, SuspendedKeyGrabState> suspendedKeyGrabs = [];
			private bool disposed;

			private struct SuspendedKeyGrabState
			{
				internal uint Xcode;
				internal int HoldCount;
			}

		private sealed class RemappedKeycodeSlot
		{
			internal required uint InjectionVk { get; init; }
			internal required uint InjectionKeycode { get; init; }
			internal required UIntPtr[] OriginalMapping { get; init; }
			internal required int KeysymsPerKeycode { get; init; }
		}

		internal LinuxKeyboardMouseSender()
		{
			AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
		}

		protected internal override bool TrySendPlatformRawText(ReadOnlySpan<char> text, ref int keyIndex, uint modifiersLR)
		{
			if (keyIndex < 0 || keyIndex >= text.Length)
				return false;

			var payload = text[keyIndex..];

			var lht = Script.TheScript.HookThread as UnixHookThread;

			if (lht == null)
				return false;

			EnsureInputSendPermission("send keyboard text");
			var extraInfo = KeyIgnoreLevel(ThreadAccessors.A_SendLevel);

			SendUnicodeTextViaX11(lht, payload.ToString(), extraInfo);

			keyIndex = text.Length - 1;
			return true;
		}

		protected override bool TrySendPlatformEventArray(UnixHookThread lht, InputArrayState state, long extraInfo, double scale)
		{
			var events = FilterLinuxModifierRestoreEvents(state.Events);

			CollectPersistentGrabTransitions(events, out var holdDownVks, out var holdUpVks);
			var ungrabKeycodes = new HashSet<uint>();
			var ungrabButtons = new HashSet<uint>();
			var hasMouseButtonEvent = false;

			if (holdDownVks.Count != 0)
			{
				lock (lht.hkLock)
				{
					foreach (var vk in holdDownVks)
						EnsureKeyGrabSuspendedForHold(lht, vk);
				}
			}

			void AddKeycodeForUngrab(uint vk)
			{
				// Keys already suspended for a held synthetic press are ungrabbed via the
				// persistent suspend path. Re-ungrabbing them in the per-send scope creates
				// avoidable refcount churn and timing windows under rapid remap alternation.
				if (IsKeyGrabSuspended(vk))
					return;

				var xkeycode = lht.VkToXKeycode(vk);
				if (xkeycode != 0)
					ungrabKeycodes.Add(xkeycode);
			}

			foreach (var ev in events)
			{
				switch (ev.Type)
				{
					case ArrayEventType.KeyDown:
					case ArrayEventType.KeyUp:
						AddKeycodeForUngrab(ev.Vk);
						break;

					case ArrayEventType.Text:
						AddKeycodeForUngrab(VK_TAB);
						AddKeycodeForUngrab(VK_BACK);
						AddKeycodeForUngrab(VK_RETURN);
						break;

					case ArrayEventType.MousePress:
					case ArrayEventType.MouseRelease:
					{
						hasMouseButtonEvent = true;
						var xbutton = lht.MouseButtonToXButton(ev.Button);
						if (xbutton != 0)
							ungrabButtons.Add(xbutton);
						break;
					}
				}
			}

			// Always release the active keyboard grab before sending, even when all target keys are
			// already suspended by EnsureKeyGrabSuspendedForHold (which leaves ungrabKeycodes empty).
			// Without this, the active keyboard grab from the triggering hotkey's passive grab would
			// intercept XTest events, preventing them from reaching the focused window.
			var ungrabAllKeys = true;

			RunWithX11SendScope(lht, ungrabKeycodes, ungrabButtons, () =>
			{
				// On X11 we avoid synthetic pre-release/re-press based on inferred physical state.
				ReplayLinuxEventArrayEvents(lht, events, extraInfo, scale);
			}, ungrabAllKeys: ungrabAllKeys, ungrabAllButtons: hasMouseButtonEvent);

			// Synchronously restore passive grabs for keys released by this send.
			// SharpHook dispatches events asynchronously; without this, a suspended grab
			// (set by the matching {key DownR}) would outlive the {key Up} send, and the
			// next remap cycle could start while the target key's grab is still absent.
			// Safety: XTest events are ordered in the server queue, so SharpHook will
			// process the synthetic up BEFORE the next physical tap's down event, ensuring
			// IsKeyGrabSuspended returns false by the time the next cycle re-arms the hold.
			if (holdUpVks.Count != 0)
			{
				foreach (var vk in holdUpVks)
					RestoreSuspendedGrabOnPhysicalKeyUp(lht, vk);
			}

			return true;
		}

		protected override bool TrySendPlatformKeybdEvent(UnixHookThread lht, KeyEventTypes eventType, uint vk, long extraInfo)
		{
			RunWithX11SendScope(lht, null, null, () =>
			{
				var tempSuspendForDownUp = false;
				var tempSuspendXcode = 0u;

				try
				{
					switch (eventType)
					{
						case KeyEventTypes.KeyDown:
							lock (lht.hkLock)
								EnsureKeyGrabSuspendedForHold(lht, vk);
							break;

						case KeyEventTypes.KeyUp:
							lock (lht.hkLock)
								(tempSuspendForDownUp, tempSuspendXcode) = EnsureTemporarySuspendForStroke(lht, vk);
							break;

						case KeyEventTypes.KeyDownAndUp:
							lock (lht.hkLock)
								(tempSuspendForDownUp, tempSuspendXcode) = EnsureTemporarySuspendForStroke(lht, vk);
							break;
					}

					SendKeyEventViaX11OrBackend(lht, eventType, vk, extraInfo);
				}
				finally
				{
					if (tempSuspendForDownUp && tempSuspendXcode != 0)
					{
						lock (lht.hkLock)
						{
							RestoreTemporarySuspendAfterStroke(lht, vk, tempSuspendXcode);
						}
					}
				}
			}, ungrabAllKeys: true);

			return true;
		}

		protected override bool TrySendPlatformUnicodeChar(UnixHookThread lht, char ch, long extraInfo, bool hasMappedKeystroke, uint vk, bool needShift, bool needAltGr)
		{
			if (ch <= 0x7F && hasMappedKeystroke)
				return false;

			SendUnicodeTextViaX11(lht, ch.ToString(), extraInfo);

			return true;
		}

		protected internal override bool TrySendPlatformSpecialCharKeyEvent(char ch, KeyEventTypes eventType, uint modifiersLR)
		{
			if (eventType == KeyEventTypes.KeyDownAndUp)
				return false;

			if (!Rune.TryCreate(ch, out _))
				return false;

			var lht = Script.TheScript.HookThread as UnixHookThread;

			if (lht == null)
				return false;

			EnsureInputSendPermission("send keyboard input");
			var extraInfo = KeyIgnoreLevel(ThreadAccessors.A_SendLevel);

			switch (eventType)
			{
				case KeyEventTypes.KeyDown:
					PressHeldUnicodeChar(lht, ch, modifiersLR, extraInfo);
					return true;

				case KeyEventTypes.KeyUp:
					ReleaseHeldUnicodeChar(lht, ch, extraInfo);
					return true;

				default:
					return false;
			}
		}

		protected override bool TryQueuePlatformMappedTextKey(char ch, uint modifiers, long extraInfo)
		{
			if (!Rune.TryCreate(ch, out var rune)
				|| !UnixCharMapper.TryMapRuneToKeystroke(rune, out var vk, out var needShift, out var needAltGr)
				|| vk == 0)
				return false;

			uint transientModifiers = 0;

			if (needShift)
				transientModifiers |= MOD_LSHIFT;

			if (needAltGr)
				transientModifiers |= MOD_RALT;

			var targetModifiers = modifiers | transientModifiers;
			SetModifierLRState(targetModifiers, eventModifiersLR, 0, false, true, extraInfo);
			PutKeybdEventIntoArray(0, vk, 0, 0, extraInfo);
			PutKeybdEventIntoArray(0, vk, 0, (uint)KEYEVENTF_KEYUP, extraInfo);
			SetModifierLRState(modifiers, eventModifiersLR, 0, false, true, extraInfo);
			return true;
		}

			private void RunWithX11SendScope(UnixHookThread lht, HashSet<uint> ungrabKeycodes, HashSet<uint> ungrabButtons, Action action, bool ungrabAllKeys = false, bool ungrabAllButtons = false)
			{
				var needUngrab = ungrabAllKeys || ungrabAllButtons || (ungrabKeycodes?.Count ?? 0) != 0 || (ungrabButtons?.Count ?? 0) != 0;

				WithSendScope(lht, () =>
				{
					if (!needUngrab)
					{
						action();
						_ = XDisplay.Default.XFlush();
						return;
					}

				lock (lht.hkLock)
				{
					var snap = lht.BeginSendUngrab(ungrabAllKeys ? null : ungrabKeycodes, ungrabAllButtons ? null : ungrabButtons);

					try
					{
						action();
						// Ensure synthetic events are submitted while grabs are still released.
						_ = XDisplay.Default.XSync(false);
						_ = XDisplay.Default.XFlush();
					}
					finally
					{
						lht.EndSendUngrab(snap);
					}
				}
			});
		}

		private void ReplayLinuxEventArrayEvents(UnixHookThread lht, List<ArrayEvent> events, long extraInfo, double scale)
		{
			var ms = DateTime.UtcNow;
			var textBatch = new StringBuilder();

			void FlushText()
			{
				if (textBatch.Length == 0)
					return;

				var text = textBatch.ToString();

				if (ContainsNonAscii(text.AsSpan()))
					SendUnicodeTextViaX11(lht, text, extraInfo);
				else
					EmitTextInjectedWithControls(text, extraInfo);

				textBatch.Clear();
				ms = DateTime.Now;
			}

			foreach (var ev in events)
			{
				if (ev.Type == ArrayEventType.Text)
				{
					textBatch.Append(ev.Text);
					continue;
				}

				FlushText();

				switch (ev.Type)
				{
					case ArrayEventType.DelayMs:
						if (ev.DelayMs > 0)
							Keysharp.Internals.Flow.SleepWithoutInterruption(ev.DelayMs);
						ms = DateTime.Now;
						break;

					case ArrayEventType.KeyDown:
						SendKeyEventViaX11OrBackend(lht, KeyEventTypes.KeyDown, ev.Vk, extraInfo);
						break;

					case ArrayEventType.KeyUp:
						SendKeyEventViaX11OrBackend(lht, KeyEventTypes.KeyUp, ev.Vk, extraInfo);
						break;

					case ArrayEventType.MouseMoveRel:
						sim.SimulateMouseMovementRelative((short)(ev.X * scale), (short)(ev.Y * scale));
						break;

					case ArrayEventType.MouseMoveAbs:
					{
						int mx = ev.X, my = ev.Y;
						EnsureCoords(ref mx, ref my);
						sim.SimulateMouseMovement((short)(mx * scale), (short)(my * scale));
						break;
					}

					case ArrayEventType.MousePress:
					{
						int mx = ev.X, my = ev.Y;

						if (mx == CoordUnspecified && my == CoordUnspecified)
							sim.SimulateMousePress(ev.Button);
						else
						{
							EnsureCoords(ref mx, ref my);
							sim.SimulateMousePress((short)(mx * scale), (short)(my * scale), ev.Button);
						}

						break;
					}

					case ArrayEventType.MouseRelease:
					{
						int mx = ev.X, my = ev.Y;

						if (mx == CoordUnspecified && my == CoordUnspecified)
							sim.SimulateMouseRelease(ev.Button);
						else
						{
							EnsureCoords(ref mx, ref my);
							sim.SimulateMouseRelease((short)(mx * scale), (short)(my * scale), ev.Button);
						}

						break;
					}

					case ArrayEventType.MouseWheelV:
						sim.SimulateMouseWheel(ev.WheelDelta, MouseWheelScrollDirection.Vertical, MouseWheelScrollType.UnitScroll);
						break;

					case ArrayEventType.MouseWheelH:
						sim.SimulateMouseWheel(ev.WheelDelta, MouseWheelScrollDirection.Horizontal, MouseWheelScrollType.UnitScroll);
						break;
				}
			}

			FlushText();
		}

		private void SendUnicodeTextViaX11(UnixHookThread lht, string text, long extraInfo)
		{
			if (string.IsNullOrEmpty(text))
				return;

			RunWithX11SendScope(
				lht,
				null,
				null,
				() => EmitUnicodeTextViaX11(lht, text, extraInfo),
				ungrabAllKeys: true
			);
		}

		private void EmitUnicodeTextViaX11(UnixHookThread lht, string text, long extraInfo)
		{
			var remappedSlotsByRune = new Dictionary<int, RemappedKeycodeSlot>();
			var reservedInjectionKeycodes = CollectMappedTextKeycodesInUse(lht, text);
			var lastWasCR = false;

			try
			{
				foreach (var rune in text.EnumerateRunes())
				{
					if (rune.Value == '\r' || rune.Value == '\n' || TryGetTextControlVk(rune, out _))
						continue;

					if (UnixCharMapper.TryMapRuneToKeystroke(rune, out var mappedVk, out _, out _) && mappedVk != 0)
						continue;

					if (remappedSlotsByRune.ContainsKey(rune.Value))
						continue;

					if (!TryCreateRemappedKeycodeSlot(lht, rune, reservedInjectionKeycodes, out var slot))
						continue;

					remappedSlotsByRune[rune.Value] = slot;
					reservedInjectionKeycodes.Add(slot.InjectionKeycode);
				}

				if (unicodeRemapDelayMs > 0 && remappedSlotsByRune.Count > 0)
					Keysharp.Internals.Flow.SleepWithoutInterruption(unicodeRemapDelayMs);

				foreach (var rune in text.EnumerateRunes())
				{
					if (rune.Value == '\r')
					{
						backend.KeyStroke(VK_RETURN, DateTime.UtcNow, extraInfo);
						lastWasCR = true;
						continue;
					}

					lastWasCR = false;

					if (rune.Value == '\n')
					{
						if (!lastWasCR)
							backend.KeyStroke(VK_RETURN, DateTime.UtcNow, extraInfo);

						continue;
					}

					if (TryGetTextControlVk(rune, out var controlVk))
					{
						backend.KeyStroke(controlVk, DateTime.UtcNow, extraInfo);
						continue;
					}

					if (UnixCharMapper.TryMapRuneToKeystroke(rune, out var vk, out var needShift, out var needAltGr) && vk != 0)
					{
						SendMappedVkViaX11(lht, vk, needShift, needAltGr, extraInfo);
						continue;
					}

					if (remappedSlotsByRune.TryGetValue(rune.Value, out var slot))
					{
						SendXTestKeycodeStroke(lht, slot.InjectionVk, slot.InjectionKeycode, extraInfo);
					}
				}
			}
			finally
			{
				if (unicodeRestoreDelayMs > 0 && remappedSlotsByRune.Count > 0)
					Keysharp.Internals.Flow.SleepWithoutInterruption(unicodeRestoreDelayMs);

				foreach (var slot in remappedSlotsByRune.Values)
					RestoreRemappedKeycodeSlot(slot);
			}
		}

		private HashSet<uint> CollectMappedTextKeycodesInUse(UnixHookThread lht, string text)
		{
			var keycodes = new HashSet<uint>();

			void AddVk(uint vk)
			{
				var keycode = lht.VkToXKeycode(vk);

				if (keycode != 0)
					keycodes.Add(keycode);
			}

			foreach (var rune in text.EnumerateRunes())
			{
				if (rune.Value == '\r' || rune.Value == '\n')
				{
					AddVk(VK_RETURN);
					continue;
				}

				if (TryGetTextControlVk(rune, out var controlVk))
				{
					AddVk(controlVk);
					continue;
				}

				if (UnixCharMapper.TryMapRuneToKeystroke(rune, out var vk, out _, out _) && vk != 0)
					AddVk(vk);
			}

			return keycodes;
		}

		private void SendMappedVkViaX11(UnixHookThread lht, uint vk, bool needShift, bool needAltGr, long extraInfo)
		{
			if (!TryGetKeycodeForVk(lht, vk, out var keycode))
				return;

			if (needAltGr)
				SendXTestKeyForVk(lht, VK_RMENU, true, extraInfo);

			if (needShift)
				SendXTestKeyForVk(lht, VK_LSHIFT, true, extraInfo);

			SendXTestKeycodeStroke(lht, vk, keycode, extraInfo);

			if (needShift)
				SendXTestKeyForVk(lht, VK_LSHIFT, false, extraInfo);

			if (needAltGr)
				SendXTestKeyForVk(lht, VK_RMENU, false, extraInfo);
		}

		private void PressHeldUnicodeChar(UnixHookThread lht, char triggerChar, uint modifiersLR, long extraInfo)
		{
			RemappedKeycodeSlot slot;

			lock (heldUnicodeRemapGate)
			{
				if (heldUnicodeRemaps.TryGetValue(triggerChar, out slot))
					return;

				if (!TryCreateRemappedKeycodeSlot(lht, ResolveHeldUnicodeRune(triggerChar, modifiersLR), out slot))
					return;

				heldUnicodeRemaps[triggerChar] = slot;
			}

			SendHeldUnicodeRemapEvent(lht, slot, true, extraInfo);
		}

		private void ReleaseHeldUnicodeChar(UnixHookThread lht, char triggerChar, long extraInfo)
		{
			RemappedKeycodeSlot slot = null;

			lock (heldUnicodeRemapGate)
			{
				if (!heldUnicodeRemaps.TryGetValue(triggerChar, out slot))
					return;
			}

			try
			{
				SendHeldUnicodeRemapEvent(lht, slot, false, extraInfo);
			}
			finally
			{
				lock (heldUnicodeRemapGate)
					heldUnicodeRemaps.Remove(triggerChar);

				RestoreRemappedKeycodeSlot(slot);
			}
		}

		private bool TryCreateRemappedKeycodeSlot(UnixHookThread lht, Rune rune, out RemappedKeycodeSlot slot)
		{
			if (!TryGetAvailableHoldInjectionKey(lht, out var injectionVk, out var injectionKeycode))
			{
				slot = null;
				return false;
			}

			return TryCreateRemappedKeycodeSlot(lht, rune, injectionVk, injectionKeycode, out slot);
		}

		private bool TryCreateRemappedKeycodeSlot(UnixHookThread lht, Rune rune, HashSet<uint> reservedInjectionKeycodes, out RemappedKeycodeSlot slot)
		{
			if (!TryGetAvailableInjectionKey(lht, reservedInjectionKeycodes, out var injectionVk, out var injectionKeycode))
			{
				slot = null;
				return false;
			}

			return TryCreateRemappedKeycodeSlot(lht, rune, injectionVk, injectionKeycode, out slot);
		}

		private bool TryCreateRemappedKeycodeSlot(UnixHookThread lht, Rune rune, uint injectionVk, uint injectionKeycode, out RemappedKeycodeSlot slot)
		{
			var keysym = (UIntPtr)KeysymFromRune(rune);

			if (keysym == UIntPtr.Zero)
			{
				slot = null;
				return false;
			}

			var original = XDisplay.Default.XGetKeyboardMapping((byte)injectionKeycode, 1, out var keysymsPerKeycode);

			if (original.Length == 0 || keysymsPerKeycode <= 0)
			{
				slot = null;
				return false;
			}

			var remapped = new UIntPtr[original.Length];

				for (var i = 0; i < remapped.Length; i++)
					remapped[i] = keysym;

				XDisplay.Default.XChangeKeyboardMapping((int)injectionKeycode, keysymsPerKeycode, remapped, 1);
				XDisplay.Default.XSync(false);

			if (unicodeRemapDelayMs > 0)
				Keysharp.Internals.Flow.SleepWithoutInterruption(unicodeRemapDelayMs);

			slot = new RemappedKeycodeSlot
			{
				InjectionVk = injectionVk,
				InjectionKeycode = injectionKeycode,
				OriginalMapping = original,
				KeysymsPerKeycode = keysymsPerKeycode
			};
			return true;
		}

		private bool IsHeldUnicodeKeycodeInUse(uint keycode)
		{
			foreach (var slot in heldUnicodeRemaps.Values)
			{
				if (slot.InjectionKeycode == keycode)
					return true;
			}

			return false;
		}

		private void SendHeldUnicodeRemapEvent(UnixHookThread lht, RemappedKeycodeSlot slot, bool isPress, long extraInfo)
		{
			RunWithX11SendScope(
				lht,
				[slot.InjectionKeycode],
				null,
				() => SendXTestKeycodeEvent(lht, slot.InjectionVk, slot.InjectionKeycode, isPress, extraInfo)
			);
		}

		private static Rune ResolveHeldUnicodeRune(char ch, uint modifiersLR)
		{
			if ((modifiersLR & (MOD_LSHIFT | MOD_RSHIFT)) != 0)
			{
				var upper = char.ToUpperInvariant(ch);

				if (upper != ch && Rune.TryCreate(upper, out var upperRune))
					return upperRune;
			}

			return new Rune(ch);
		}

		private static void RestoreRemappedKeycodeSlot(RemappedKeycodeSlot slot)
		{
				if (slot == null)
					return;

				XDisplay.Default.XChangeKeyboardMapping((int)slot.InjectionKeycode, slot.KeysymsPerKeycode, slot.OriginalMapping, 1);
				XDisplay.Default.XSync(false);
			}

		private void SendXTestKeyForVk(UnixHookThread lht, uint vk, bool isPress, long extraInfo)
		{
			if (!TryGetKeycodeForVk(lht, vk, out var keycode))
				return;

			SendXTestKeycodeEvent(lht, vk, keycode, isPress, extraInfo);
		}

		private void SendKeyEventViaX11OrBackend(UnixHookThread lht, KeyEventTypes eventType, uint vk, long extraInfo)
		{
			if (TryGetKeycodeForVk(lht, vk, out var keycode))
			{
				if (eventType == KeyEventTypes.KeyDown || eventType == KeyEventTypes.KeyDownAndUp)
					SendXTestKeycodeEvent(lht, vk, keycode, true, extraInfo);

				if (eventType == KeyEventTypes.KeyUp || eventType == KeyEventTypes.KeyDownAndUp)
					SendXTestKeycodeEvent(lht, vk, keycode, false, extraInfo);

				return;
			}

			// Fallback for keys that don't currently resolve to an X11 keycode.
			SendKeyEventDirect(eventType, vk, extraInfo);
		}

			private void SendXTestKeycodeEvent(UnixHookThread lht, uint vk, uint keycode, bool isPress, long extraInfo)
			{
				var code = SharpHookKeyMapper.VkToKeyCode(vk);
				lht.RegisterSyntheticEvent(code, !isPress, DateTime.UtcNow, extraInfo, vk);
				_ = XDisplay.Default.XTestFakeKeyEvent(keycode, isPress, 0);
				XDisplay.Default.XSync(false);
			}

		private void SendXTestKeycodeStroke(UnixHookThread lht, uint vk, uint keycode, long extraInfo)
		{
			SendXTestKeycodeEvent(lht, vk, keycode, true, extraInfo);
			SendXTestKeycodeEvent(lht, vk, keycode, false, extraInfo);
		}

		private static bool ContainsNonAscii(ReadOnlySpan<char> text)
		{
			foreach (var ch in text)
			{
				if (ch > 0x7F)
					return true;
			}

			return false;
		}

		private static uint KeysymFromRune(Rune rune)
		{
			uint cp = (uint)rune.Value;

			if (cp <= 0x7F)
				return cp;

			if (cp >= 0x00A0 && cp <= 0x00FF)
				return cp;

			if (cp >= 0x0100 && cp <= 0x10FFFF)
				return 0x01000000u | cp;

			return 0;
		}

		private static bool TryGetKeycodeForVk(UnixHookThread lht, uint vk, out uint keycode)
		{
			keycode = lht.VkToXKeycode(vk);
			return keycode != 0;
		}

		private bool TryGetAvailableInjectionKey(UnixHookThread lht, out uint vk, out uint keycode)
			=> TryGetAvailableInjectionKey(lht, null, out vk, out keycode);

		private bool TryGetAvailableHoldInjectionKey(UnixHookThread lht, out uint vk, out uint keycode)
		{
			// Held remap keys (DownR/Up) need a stable mapped donor so X11 repeat and hook identity stay consistent.
			foreach (var candidateVk in unicodeInjectionFallbackVks)
			{
				keycode = lht.VkToXKeycode(candidateVk);

				if (keycode == 0 || IsHeldUnicodeKeycodeInUse(keycode))
					continue;

				vk = candidateVk;
				return true;
			}

			// Last resort when no fallback donor is available.
			return TryGetAvailableInjectionKey(lht, null, out vk, out keycode);
		}

		private bool TryGetAvailableInjectionKey(UnixHookThread lht, HashSet<uint> reservedInjectionKeycodes, out uint vk, out uint keycode)
		{
			// Prefer truly unused keycodes first to minimize interference with any mapped keys.
			if (TryGetUnusedInjectionKeycode(lht, reservedInjectionKeycodes, out keycode))
			{
				vk = lht.MapScToVk(keycode);
				return true;
			}

			// Fallback to uncommon mapped donors when no unused keycode is available.
			foreach (var candidateVk in unicodeInjectionFallbackVks)
			{
				keycode = lht.VkToXKeycode(candidateVk);

				if (keycode == 0
					|| IsHeldUnicodeKeycodeInUse(keycode)
					|| (reservedInjectionKeycodes?.Contains(keycode) ?? false))
					continue;

				vk = candidateVk;
				return true;
			}

			vk = 0;
			keycode = 0;
			return false;
		}

		private static int GetUnicodeRemapDelayMs()
		{
			var raw = Environment.GetEnvironmentVariable("KS_HARNESS_UNICODE_REMAP_DELAY_MS");

			if (!int.TryParse(raw, out var parsed))
				return 0;

			if (parsed < 0)
				return 0;

			return parsed > 500 ? 500 : parsed;
		}

		private static int GetUnicodeRestoreDelayMs()
		{
			var raw = Environment.GetEnvironmentVariable("KS_HARNESS_UNICODE_RESTORE_DELAY_MS");

			if (!int.TryParse(raw, out var parsed))
				return 80;

			if (parsed < 0)
				return 0;

			return parsed > 500 ? 500 : parsed;
		}

			private bool TryGetUnusedInjectionKeycode(UnixHookThread lht, HashSet<uint> reservedInjectionKeycodes, out uint keycode)
			{
				for (uint kc = 8; kc < 256; kc++)
				{
					if (IsHeldUnicodeKeycodeInUse(kc) || (reservedInjectionKeycodes?.Contains(kc) ?? false))
						continue;

					var mapping = XDisplay.Default.XGetKeyboardMapping((byte)kc, 1, out var keysymsPerKeycode);

					if (mapping.Length == 0 || keysymsPerKeycode <= 0)
						continue;

					var hasAnySymbol = false;

					for (var i = 0; i < mapping.Length; i++)
					{
						if (mapping[i] != UIntPtr.Zero)
						{
							hasAnySymbol = true;
							break;
						}
					}

					if (hasAnySymbol)
						continue;

					keycode = kc;
					return true;
				}

				keycode = 0;
				return false;
			}

			private void EnsureKeyGrabSuspendedForHold(UnixHookThread lht, uint vk)
			{
				if (vk == 0 || IsModifierVk(vk))
					return;

				lock (suspendedKeyGrabGate)
				{
					if (suspendedKeyGrabs.TryGetValue(vk, out var existing))
					{
						// Idempotent: mirror the SyntheticEventQueue "owner-wins" rule.
						// Multiple {key DownR} sends from auto-repeat of the trigger hotkey must
						// not accumulate HoldCount, because only one {key Up} is ever sent to
						// balance them.  Incrementing here would leave HoldCount permanently > 0
						// after the single decrement on {key Up}, keeping the passive grab
						// suspended indefinitely and causing subsequent physical presses of the
						// target key to bypass the passive grab (reaching the focused window
						// unsuppressed while also firing the hotkey a second time).
						return;
					}

					var xcode = lht.TemporarilyUngrabKey(vk);

					if (xcode != 0)
					{
						suspendedKeyGrabs[vk] = new SuspendedKeyGrabState
						{
							Xcode = xcode,
							HoldCount = 1
						};
					}
				}
			}

			private (bool suspended, uint xcode) EnsureTemporarySuspendForStroke(UnixHookThread lht, uint vk)
			{
				if (vk == 0)
					return (false, 0);

				lock (suspendedKeyGrabGate)
				{
					if (suspendedKeyGrabs.ContainsKey(vk))
						return (false, 0);

					var xcode = lht.TemporarilyUngrabKey(vk);

					if (xcode == 0)
						return (false, 0);

					return (true, xcode);
				}
			}

			private void RestoreTemporarySuspendAfterStroke(UnixHookThread lht, uint vk, uint xcode)
			{
				if (vk == 0 || xcode == 0)
					return;

				lht.RegrabKey(xcode);
			}

			private void RestoreKeyGrabOnKeyUp(UnixHookThread lht, uint vk)
			{
				if (vk == 0)
					return;

				if (TryReleaseSuspendedHold(vk, out var xcode, out var remainingHoldCount))
				{
					if (remainingHoldCount <= 0)
						lht.RegrabKey(xcode);
				}
			}

			internal void RestoreSuspendedGrabOnPhysicalKeyUp(UnixHookThread lht, uint vk)
			{
				if (lht == null || vk == 0)
					return;

				lock (lht.hkLock)
					RestoreKeyGrabOnKeyUp(lht, vk);
			}

			internal bool IsKeyGrabSuspended(uint vk)
			{
				if (vk == 0)
					return false;

				lock (suspendedKeyGrabGate)
					return suspendedKeyGrabs.ContainsKey(vk);
			}

			internal override bool IsKeyGrabSuspendedForReplay(uint vk) => IsKeyGrabSuspended(vk);

			private bool TryReleaseSuspendedHold(uint vk, out uint xcode, out int remainingHoldCount)
			{
				xcode = 0;
				remainingHoldCount = 0;

				lock (suspendedKeyGrabGate)
				{
					if (!suspendedKeyGrabs.TryGetValue(vk, out var state))
						return false;

					xcode = state.Xcode;

					if (state.HoldCount > 1)
					{
						state.HoldCount--;
						remainingHoldCount = state.HoldCount;
						suspendedKeyGrabs[vk] = state;
						return true;
					}

					suspendedKeyGrabs.Remove(vk);

					return true;
				}
			}

		private static bool TryGetTextControlVk(Rune rune, out uint vk)
		{
			vk = rune.Value switch
			{
				'\t' => VK_TAB,
				'\b' => VK_BACK,
				_ => 0
			};

				return vk != 0;
			}

		private static void CollectPersistentGrabTransitions(List<ArrayEvent> events, out HashSet<uint> holdDownVks, out HashSet<uint> holdUpVks)
		{
				holdDownVks = [];
				holdUpVks = [];
				Dictionary<uint, bool> finalDownState = [];

				foreach (var ev in events)
				{
					if (ev.Type != ArrayEventType.KeyDown && ev.Type != ArrayEventType.KeyUp)
						continue;

					var vk = ev.Vk;

					if (vk == 0 || IsModifierVk(vk))
						continue;

					if (ev.Type == ArrayEventType.KeyDown)
						finalDownState[vk] = true;
					else
						finalDownState[vk] = false;
				}

				foreach (var kv in finalDownState)
				{
					if (kv.Value)
						holdDownVks.Add(kv.Key);
					else
						holdUpVks.Add(kv.Key);
				}
			}

			public void Dispose()
			{
				AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
				ShutdownHeldUnicodeRemaps(bestEffortKeyUp: true);
			}

		private void OnProcessExit(object sender, EventArgs e) => ShutdownHeldUnicodeRemaps(bestEffortKeyUp: false);

			private void ShutdownHeldUnicodeRemaps(bool bestEffortKeyUp)
			{
				if (disposed)
					return;

				disposed = true;
				RestoreAllSuspendedKeyGrabs();
				ReleaseAllHeldUnicodeRemaps(bestEffortKeyUp);
			}

			private void RestoreAllSuspendedKeyGrabs()
			{
				var lht = Script.TheScript?.HookThread as UnixHookThread;

				if (lht == null)
					return;

				List<(uint vk, uint xcode)> pending = [];

				lock (suspendedKeyGrabGate)
				{
						foreach (var kv in suspendedKeyGrabs)
							pending.Add((kv.Key, kv.Value.Xcode));

						suspendedKeyGrabs.Clear();
					}

				if (pending.Count == 0)
					return;

				lock (lht.hkLock)
				{
					foreach (var (vk, xcode) in pending)
					{
						try
						{
							if (xcode != 0)
							{
								lht.RegrabKey(xcode);
							}
						}
						catch
						{
						}
					}
				}
			}

		private void ReleaseAllHeldUnicodeRemaps(bool bestEffortKeyUp)
		{
			List<RemappedKeycodeSlot> slots;

			lock (heldUnicodeRemapGate)
			{
				if (heldUnicodeRemaps.Count == 0)
					return;

				slots = [.. heldUnicodeRemaps.Values];
				heldUnicodeRemaps.Clear();
			}

			var lht = Script.TheScript?.HookThread as UnixHookThread;
			var extraInfo = KeyIgnoreLevel(ThreadAccessors.A_SendLevel);

			foreach (var slot in slots)
			{
				try
				{
					if (bestEffortKeyUp && lht != null)
						SendHeldUnicodeRemapEvent(lht, slot, false, extraInfo);
				}
				catch
				{
				}

				try
				{
					RestoreRemappedKeycodeSlot(slot);
				}
				catch
				{
				}
			}
		}

		private static bool IsModifierVk(uint vk) => vk is
			VK_SHIFT or VK_LSHIFT or VK_RSHIFT or
			VK_CONTROL or VK_LCONTROL or VK_RCONTROL or
			VK_MENU or VK_LMENU or VK_RMENU or
			VK_LWIN or VK_RWIN;

		private static List<ArrayEvent> FilterLinuxModifierRestoreEvents(List<ArrayEvent> events)
		{
			if (events == null || events.Count == 0)
				return events;

			var nonModifierKeyEventCounter = 0;
			var modUpCounterSnapshot = new Dictionary<uint, int>();
			var filtered = new List<ArrayEvent>(events.Count);

			foreach (var ev in events)
			{
				if (ev.Type != ArrayEventType.KeyDown && ev.Type != ArrayEventType.KeyUp)
				{
					filtered.Add(ev);
					continue;
				}

				var vk = ev.Vk;
				var isModifier = IsModifierVk(vk);

				if (!isModifier)
				{
					nonModifierKeyEventCounter++;
					filtered.Add(ev);
					continue;
				}

				if (ev.Type == ArrayEventType.KeyUp)
				{
					modUpCounterSnapshot[vk] = nonModifierKeyEventCounter;
					filtered.Add(ev);
					continue;
				}

				// KeyDown modifier.
				if (modUpCounterSnapshot.TryGetValue(vk, out var counterAtUp)
					&& nonModifierKeyEventCounter > counterAtUp)
				{
					// Linux/X11 cannot reliably infer physical modifier state after synthetic release.
					// Drop the automatic "restore" down that follows real payload keys.
					modUpCounterSnapshot.Remove(vk);
					continue;
				}

				modUpCounterSnapshot.Remove(vk);
				filtered.Add(ev);
			}

			return filtered;
		}

	}
}
#endif
