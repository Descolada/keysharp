#if LINUX
using System;
using System.Collections.Generic;
using System.Text;
using SharpHook.Data;
using static Keysharp.Core.Common.Keyboard.KeyboardUtils;
using static Keysharp.Core.Common.Keyboard.VirtualKeys;

namespace Keysharp.Core.Unix
{
	internal sealed class LinuxKeyboardMouseSender : UnixKeyboardMouseSender, IDisposable
	{
		private static readonly uint[] unicodeInjectionFallbackVks =
		[
			VK_F24, VK_F23, VK_F22, VK_F21, VK_F20, VK_F19, VK_F18, VK_F17,
			VK_F16, VK_F15, VK_F14, VK_F13, VK_SCROLL, VK_PAUSE, VK_SNAPSHOT
		];
		private readonly object heldUnicodeRemapGate = new();
		private readonly Dictionary<char, RemappedKeycodeSlot> heldUnicodeRemaps = [];
		private bool disposed;

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

			if (!ContainsNonAscii(payload))
				return false;

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
			var ungrabKeycodes = new HashSet<uint>();
			var ungrabButtons = new HashSet<uint>();
			var vksToRelease = CollectVksWeWillPress(state.Events);
			var logicalMods = GetModifierLRState();
			var modsToPreRelease = CollectModifierUpsForArray(state.Events, logicalMods, lht);

			void AddKeycodeForUngrab(uint vk)
			{
				var xkeycode = lht.VkToXKeycode(vk);
				if (xkeycode != 0)
					ungrabKeycodes.Add(xkeycode);
			}

			foreach (var vk in vksToRelease)
				AddKeycodeForUngrab(vk);

			foreach (var vk in modsToPreRelease)
				AddKeycodeForUngrab(vk);

			foreach (var ev in state.Events)
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
						var xbutton = lht.MouseButtonToXButton(ev.Button);
						if (xbutton != 0)
							ungrabButtons.Add(xbutton);
						break;
					}
				}
			}

			DebugLog($"[SendArray] Events={state.Events.Count} needUngrab={(ungrabKeycodes.Count != 0 || ungrabButtons.Count != 0)} ungrabKeys={ungrabKeycodes.Count} ungrabButtons={ungrabButtons.Count} vksToRelease={vksToRelease.Count} modsToPreRelease={modsToPreRelease.Count}");

			RunWithX11SendScope(lht, ungrabKeycodes, ungrabButtons, () =>
			{
				List<UnixHookThread.ReleasedKey> released = null;

				try
				{
					if (vksToRelease.Count != 0)
						released = lht.ForceReleaseKeysForSend(vksToRelease);

					if (modsToPreRelease.Count != 0)
					{
						var now = DateTime.UtcNow;

						foreach (var vk in modsToPreRelease)
							backend.KeyUp(vk, now, extraInfo);
					}

					ReplayLinuxEventArrayEvents(lht, state.Events, extraInfo, scale);
				}
				finally
				{
					if (released != null)
						lht.RestoreNonModifierKeysAfterSend(released);
				}
			});

			return true;
		}

		protected override bool TrySendPlatformKeybdEvent(UnixHookThread lht, KeyEventTypes eventType, uint vk, long extraInfo)
		{
			WithSendScope(lht, () =>
			{
				lock (lht.hkLock)
				{
					var xcode = lht.TemporarilyUngrabKey(vk);
					List<UnixHookThread.ReleasedKey> released = null;

					try
					{
						if (eventType == KeyEventTypes.KeyDown || eventType == KeyEventTypes.KeyDownAndUp)
							released = lht.ForceReleaseKeysForSend([vk]);

						SendKeyEventDirect(eventType, vk, extraInfo);
					}
					finally
					{
						if (released != null)
							lht.RestoreNonModifierKeysAfterSend(released);

						lht.RegrabKey(xcode);
					}
				}
			});

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

		private void RunWithX11SendScope(UnixHookThread lht, HashSet<uint> ungrabKeycodes, HashSet<uint> ungrabButtons, Action action)
		{
			var needUngrab = (ungrabKeycodes?.Count ?? 0) != 0 || (ungrabButtons?.Count ?? 0) != 0;

			WithSendScope(lht, () =>
			{
				if (!needUngrab)
				{
					action();
					return;
				}

				lock (lht.hkLock)
				{
					var snap = lht.BeginSendUngrab(ungrabKeycodes, ungrabButtons);

					try
					{
						action();
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
							Flow.SleepWithoutInterruption(ev.DelayMs);
						ms = DateTime.Now;
						break;

					case ArrayEventType.KeyDown:
						backend.KeyDown(ev.Vk, ms, extraInfo);
						break;

					case ArrayEventType.KeyUp:
						backend.KeyUp(ev.Vk, ms, extraInfo);
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
				CollectTextUngrabKeycodes(lht, text),
				null,
				() => EmitUnicodeTextViaX11(lht, text, extraInfo)
			);
		}

		private void EmitUnicodeTextViaX11(UnixHookThread lht, string text, long extraInfo)
		{
			var lastWasCR = false;

			foreach (var rune in text.EnumerateRunes())
			{
				if (rune.Value == '\r')
				{
					backend.KeyStroke(VK_RETURN, DateTime.UtcNow, extraInfo);
					lastWasCR = true;
					continue;
				}

				if (rune.Value == '\n')
				{
					if (!lastWasCR)
						backend.KeyStroke(VK_RETURN, DateTime.UtcNow, extraInfo);

					lastWasCR = false;
					continue;
				}

				if (TryGetTextControlVk(rune, out var controlVk))
				{
					backend.KeyStroke(controlVk, DateTime.UtcNow, extraInfo);
					lastWasCR = false;
					continue;
				}

				lastWasCR = false;

				if (UnixCharMapper.TryMapRuneToKeystroke(rune, out var vk, out var needShift, out var needAltGr) && vk != 0)
					SendMappedVkViaX11(lht, vk, needShift, needAltGr, extraInfo);
				else
					SendRemappedRuneViaX11(lht, rune, extraInfo);
			}
		}

		private HashSet<uint> CollectTextUngrabKeycodes(UnixHookThread lht, string text)
		{
			var keycodes = new HashSet<uint>();

			void AddVk(uint vk)
			{
				var keycode = lht.VkToXKeycode(vk);

				if (keycode != 0)
					keycodes.Add(keycode);
			}

			AddVk(VK_LSHIFT);
			AddVk(VK_RMENU);

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
				else if (TryGetAvailableInjectionKey(lht, out _, out var injectionKeycode))
					keycodes.Add(injectionKeycode);
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
				if (!heldUnicodeRemaps.Remove(triggerChar, out slot))
					return;
			}

			try
			{
				SendHeldUnicodeRemapEvent(lht, slot, false, extraInfo);
			}
			finally
			{
				RestoreRemappedKeycodeSlot(slot);
			}
		}

		private void SendRemappedRuneViaX11(UnixHookThread lht, Rune rune, long extraInfo)
		{
			if (!TryCreateRemappedKeycodeSlot(lht, rune, out var slot))
				return;

			try
			{
				SendXTestKeycodeStroke(lht, slot.InjectionVk, slot.InjectionKeycode, extraInfo);
				Flow.SleepWithoutInterruption(12);
			}
			finally
			{
				RestoreRemappedKeycodeSlot(slot);
			}
		}

		private bool TryCreateRemappedKeycodeSlot(UnixHookThread lht, Rune rune, out RemappedKeycodeSlot slot)
		{
			var keysym = (UIntPtr)KeysymFromRune(rune);

			if (keysym == UIntPtr.Zero)
			{
				slot = null;
				return false;
			}

			if (!TryGetAvailableInjectionKey(lht, out var injectionVk, out var injectionKeycode))
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

		private void SendXTestKeycodeEvent(UnixHookThread lht, uint vk, uint keycode, bool isPress, long extraInfo)
		{
			var code = SharpHookKeyMapper.VkToKeyCode(vk);
			lht.RegisterSyntheticEvent(code, !isPress, DateTime.UtcNow, extraInfo);
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
		{
			foreach (var candidateVk in unicodeInjectionFallbackVks)
			{
				keycode = lht.VkToXKeycode(candidateVk);

				if (keycode == 0 || IsHeldUnicodeKeycodeInUse(keycode))
					continue;

				vk = candidateVk;
				return true;
			}

			vk = 0;
			keycode = 0;
			return false;
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
			ReleaseAllHeldUnicodeRemaps(bestEffortKeyUp);
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
	}
}
#endif
