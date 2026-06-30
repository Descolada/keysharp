#if LINUX
using Keysharp.Builtins;
using Keysharp.Internals.Input.Keyboard;
using Keysharp.Internals.Input.Mouse;
using Keysharp.Internals.Input.Linux;
using Keysharp.Internals.Window.Linux.Proxies;
using SharpHook;
using SharpHook.Data;
using static Keysharp.Internals.Input.Keyboard.KeyboardMouseSender;
using static Keysharp.Internals.Input.Keyboard.KeyboardUtils;
using static Keysharp.Internals.Input.Keyboard.VirtualKeys;
using static Keysharp.Internals.Window.Linux.X11.Xlib;

namespace Keysharp.Internals.Input.Hooks.Linux
{
	// Linux-only X11 extension points for UnixHookThread.
	internal sealed class LinuxHookThread : UnixHookThread
	{
		protected override bool UsePlatformHotstringArming => true;
		protected override bool UseSyntheticEventQueue => true;
		protected override bool KeepSyntheticDownTokensForRepeats => true;
		private const uint Mod5Mask = 1 << 7; // Usually ISO_Level3_Shift (AltGr) on X11.
		private static readonly uint[] ExtraGrabMasks = { ControlMask, ShiftMask, Mod1Mask, Mod4Mask, Mod5Mask };
		private readonly LinuxX11InputState inputState;
		private readonly Dictionary<uint, int> temporarilyUngrabbedKeycodes = [];

		internal LinuxHookThread() : base()
		{
			inputState = new LinuxX11InputState(physicalKeyState, logicalKeyState);
		}

		protected override long SyntheticEventTimeoutMs => 250;

		protected override bool ShouldSkipSyntheticQueueMatch(uint vk, bool keyUp) => false;

		protected override bool ShouldConsumePlatformHotstringKeyDown(uint vk) => inputState.ShouldSuppressPendingHotstringKeyDown(vk);

		protected override bool ShouldConsumePlatformHotstringKeyUp(uint vk, bool isInjected) => inputState.TrySuppressPendingHotstringKeyUp(vk, updatePhysical: !isInjected);

		protected override void TrackPlatformHotstringTrigger(uint triggerVk) => inputState.TrackPendingHotstringTrigger(triggerVk);

		protected override bool MarkSimulatedIfNeeded(HookEventArgs e, uint vk, KeyCode keyCode, bool keyUp, out ulong extraInfo)
		{
			var simulated = base.MarkSimulatedIfNeeded(e, vk, keyCode, keyUp, out extraInfo);

			if (simulated
				|| vk == 0
				|| IsModifierVk(vk)
				|| !TryGetHeldSyntheticDownOwnerExtra(keyCode, vk, out var ownerExtraInfo))
				return simulated;

			var raw = e.RawEvent;
			raw.Mask |= EventMask.SimulatedEvent;
			SetRawEvent(e, raw);
			extraInfo = ownerExtraInfo != 0
				? ownerExtraInfo
				: (ulong)KeyIgnoreAllExceptModifier;
			return true;
		}

		// Synthetic key-up grab restoration is handled synchronously by Fix B in
		// DispatchEventArray (RestoreSuspendedGrabOnPhysicalKeyUp after the send).
		// Calling it here from the SharpHook thread would use a different [ThreadStatic]
		// XDisplay connection than the one that established the grabs, causing cross-client
		// XGrabKey/XUngrabKey failures and BadAccess errors on subsequent cycles.

		protected override void OnPlatformPhysicalKeyUpObserved(uint vk)
		{
			if (kbdMsSender is LinuxKeyboardMouseSender sender
				&& sender.IsKeyGrabSuspended(vk))
			{
				sender.RestoreSuspendedGrabOnPhysicalKeyUp(this, vk);
			}
		}

		protected override void AddPlatformHotstringArmEnds(HotstringManager hm, ReadOnlySpan<char> hsBuf, HashSet<char> ends)
		{
			inputState.AddPredictedHotstringCompletionEnds(hm, hsBuf, ends, IsHotstringWordChar);
		}

		protected override void RebuildPlatformHotkeyGrabs()
		{
			var newKeyGrabs = new Dictionary<(uint keycode, uint mods), GrabKinds>();
			var newButtonGrabs = new Dictionary<(uint button, uint mods), GrabKinds>();

				if (!IsX11Available)
				{
					activeKeyGrabs.Clear();
					activeButtonGrabs.Clear();
					temporarilyUngrabbedKeycodes.Clear();
					return;
				}

			if (UsingInputdHooks)
			{
				dynamicPrefixGrabs.Clear();
				ApplyGrabDiff(activeKeyGrabs, newKeyGrabs,
					pair => _ = XDisplay.Default.XUngrabKey(pair.Item1, pair.Item2),
					pair => _ = XDisplay.Default.XGrabKey(pair.Item1, pair.Item2, (nint)XDisplay.Default.Root.ID, true, GrabModeAsync, GrabModeAsync));
				ApplyGrabDiff(activeButtonGrabs, newButtonGrabs,
					pair => _ = XDisplay.Default.XUngrabButton(pair.Item1, pair.Item2, (nint)XDisplay.Default.Root.ID),
					pair => _ = XDisplay.Default.XGrabButton(pair.Item1, pair.Item2, (nint)XDisplay.Default.Root.ID, true,
						(uint)(EventMasks.ButtonPress | EventMasks.ButtonRelease), GrabModeAsync, GrabModeAsync, nint.Zero, nint.Zero));
				temporarilyUngrabbedKeycodes.Clear();
				XDisplay.Default.XSync(false);
				return;
			}

			foreach (var entry in linuxHotkeys)
			{
					if (entry.PassThrough || entry.Vk == 0)
						continue;

					var modsForGrab = entry.ModifiersLR;
					bool? neutral = null;
					if (entry.ModifierVK != 0)
						modsForGrab |= KeyToModifiersLR(entry.ModifierVK, 0, ref neutral);

					var xmods = ModifiersLRToXMask(modsForGrab);

					if (MouseUtils.IsMouseVK(entry.Vk) && TryMapMouseVkToButton(entry.Vk, out var button))
					{
						foreach (var m in ButtonGrabMaskVariants(xmods, entry.AllowExtra))
							AddGrabKind(newButtonGrabs, (button, m), GrabKinds.Hotkey);
					}
					else if (MouseUtils.IsWheelVK(entry.Vk))
					{
						// Wheel hotkeys aren't grabbed; handled via hook suppression.
					}
					else if (entry.ModifierVK != 0 && modsForGrab == 0)
					{
						if (TryMapToXGrab(entry.Vk, modsForGrab, out var keycode, out var mods))
						{
							if (!dynamicPrefixGrabs.TryGetValue(entry.ModifierVK, out var list))
								dynamicPrefixGrabs[entry.ModifierVK] = list = new();
							list.Add((keycode, mods));
						}
					}
					else if (TryMapToXGrab(entry.Vk, modsForGrab, out var keycode, out var mods))
					{
						var selfMod = VkToSelfXModifierMask(entry.Vk);
						var grabMods = mods | selfMod;
						foreach (var pair in entry.AllowExtra ? KeyGrabVariantsWithExtra(keycode, grabMods) : KeyGrabVariants(keycode, grabMods))
							AddGrabKind(newKeyGrabs, pair, GrabKinds.Hotkey);
					}
			}

			foreach (var kvp in customPrefixSuppress)
			{
				if (!kvp.Value)
					continue;

				if (TryMapToXGrab(kvp.Key, 0, out var prefixKeycode, out _))
				{
					foreach (var pair in KeyGrabVariants(prefixKeycode, AnyModifier, anyModifier: true))
						AddGrabKind(newKeyGrabs, pair, GrabKinds.Hotkey);
				}
			}

			AddForcedLockKeyGrabs(newKeyGrabs);
			BuildInputGrabs(newKeyGrabs);
			AddArmedHotstringGrabs(newKeyGrabs);

			ApplyGrabDiff(activeKeyGrabs, newKeyGrabs,
				ungrab: pair => _ = XDisplay.Default.XUngrabKey(pair.Item1, pair.Item2),
				grab: pair => _ = XDisplay.Default.XGrabKey(pair.Item1, pair.Item2, (nint)XDisplay.Default.Root.ID, true, GrabModeAsync, GrabModeAsync));

			ApplyGrabDiff(activeButtonGrabs, newButtonGrabs,
				ungrab: pair => _ = XDisplay.Default.XUngrabButton(pair.Item1, pair.Item2, (nint)XDisplay.Default.Root.ID),
				grab: pair => _ = XDisplay.Default.XGrabButton(pair.Item1, pair.Item2, (nint)XDisplay.Default.Root.ID, true,
					(uint)(EventMasks.ButtonPress | EventMasks.ButtonRelease), GrabModeAsync, GrabModeAsync, nint.Zero, nint.Zero));

			XDisplay.Default.XSync(false);
		}

		private void AddForcedLockKeyGrabs(Dictionary<(uint keycode, uint mods), GrabKinds> target)
		{
			var toggleStates = Script.TheScript.KeyboardData.toggleStates;

			AddForcedLockKeyGrab(VK_CAPITAL, toggleStates.forceCapsLock);
			AddForcedLockKeyGrab(VK_NUMLOCK, toggleStates.forceNumLock);
			AddForcedLockKeyGrab(VK_SCROLL, toggleStates.forceScrollLock);

			void AddForcedLockKeyGrab(uint vk, ToggleValueType forceState)
			{
				if (forceState == ToggleValueType.Neutral || !TryMapToXGrab(vk, 0, out var keycode, out _))
					return;

				foreach (var pair in KeyGrabVariants(keycode, AnyModifier, anyModifier: true))
					AddGrabKind(target, pair, GrabKinds.Hotkey);
			}
		}

		protected override void InitSnapshotFromPlatform()
		{
			inputState.Reset();
			kbdMsSender.modifiersLRPhysical = 0;
			kbdMsSender.modifiersLRLogical = 0;
			kbdMsSender.modifiersLRLogicalNonIgnored = 0;

			if (!TryQueryKeymap(out var km))
				return;

			for (int keycode = 8; keycode < 256; keycode++)
			{
				var byteIndex = keycode >> 3;
				var bitMask = 1 << (keycode & 7);
				if ((km[byteIndex] & bitMask) == 0)
					continue;

				var ks = XDisplay.Default.XKeycodeToKeysym(keycode, 0);
				if (ks == 0)
					continue;

				var vk = KeyCodes.KeysymToVk((ulong)ks);
				inputState.RecordInitialPhysicalKeyDown(vk);
			}

			if (!TryQueryModifierLRStatePlatform(out var modsInit, km))
				return;

			kbdMsSender.modifiersLRPhysical = modsInit;
			kbdMsSender.modifiersLRLogical = modsInit;
			kbdMsSender.modifiersLRLogicalNonIgnored = modsInit;

			void SetLogical(uint modMask, uint vk)
			{
				if ((modsInit & modMask) != 0)
					inputState.RecordInitialLogicalKeyDown(vk);
			}

			SetLogical(MOD_LSHIFT, VK_LSHIFT);
			SetLogical(MOD_RSHIFT, VK_RSHIFT);
			SetLogical(MOD_LCONTROL, VK_LCONTROL);
			SetLogical(MOD_RCONTROL, VK_RCONTROL);
			SetLogical(MOD_LALT, VK_LMENU);
			SetLogical(MOD_RALT, VK_RMENU);
			SetLogical(MOD_LWIN, VK_LWIN);
			SetLogical(MOD_RWIN, VK_RWIN);
		}

		protected override bool TryQueryModifierLRStatePlatform(out uint mods, byte[] keymapBuffer = null)
		{
			mods = 0u;

			if (!IsX11Available)
				return false;

			byte[] keymap = keymapBuffer is { Length: 32 } kb ? kb : new byte[32];

			if (XDisplay.Default.XQueryKeymap(keymap) == 0)
				return false;

			for (int keycode = 8; keycode < 256; keycode++)
			{
				var byteIndex = keycode >> 3;
				var bitMask = 1 << (keycode & 7);

				if ((keymap[byteIndex] & bitMask) == 0)
					continue;

				var keysym = (ulong)XDisplay.Default.XKeycodeToKeysym(keycode, 0);
				if (keysym == 0)
					continue;

				switch (keysym)
				{
					case 0xFFE1: mods |= MOD_LSHIFT; break;
					case 0xFFE2: mods |= MOD_RSHIFT; break;
					case 0xFFE3: mods |= MOD_LCONTROL; break;
					case 0xFFE4: mods |= MOD_RCONTROL; break;
					case 0xFFE9: mods |= MOD_LALT; break;
					case 0xFFEA: mods |= MOD_RALT; break;
					case 0xFFEB: mods |= MOD_LWIN; break;
					case 0xFFEC: mods |= MOD_RWIN; break;
				}
			}

			return true;
		}

		protected override bool TryQueryKeyStatePlatform(uint vk, out bool isDown)
		{
			isDown = false;

			if (!IsX11Available)
				return false;

			if (!TryQueryKeymap(out var keymap))
				return false;

			var expected = KeyCodes.VkToKeysyms(vk);
			if (expected.Count == 0)
				return false;

			for (int keycode = 8; keycode < 256; keycode++)
			{
				var byteIndex = keycode >> 3;
				var bitMask = 1 << (keycode & 7);
				if ((keymap[byteIndex] & bitMask) == 0)
					continue;

				var keysym = (ulong)XDisplay.Default.XKeycodeToKeysym(keycode, 0);
				if (keysym == 0)
					continue;

				foreach (var ks in expected)
				{
					if ((ulong)ks != keysym)
						continue;

					isDown = true;
					return true;
				}
			}

			return true;
		}

			protected override bool WasKeyGrabbedPlatform(HookEventArgs e, uint vk, bool keyUp, out bool grabbedByHotstring)
			{
			grabbedByHotstring = false;
			if (!IsX11Available)
				return false;

			lock (hkLock)
			{
				uint xcode = e.RawEvent.Keyboard.RawCode;

				if (xcode == 0 && vk != 0 && TryMapToXGrab(vk, 0, out var kcFromVk, out _))
					xcode = kcFromVk;

					if (xcode == 0)
						return false;

					if (temporarilyUngrabbedKeycodes.ContainsKey(xcode))
						return false;

				uint mods = CurrentXGrabMask();
				bool hotstringGrabbed = false;

				static bool ModsMatch(uint grabbed, uint actual)
				{
					const uint lockBits = LockMask | Mod2Mask;
					return grabbed == AnyModifier || grabbed == actual || (grabbed & ~lockBits) == (actual & ~lockBits);
				}

				bool MatchActiveKeyGrab()
				{
					foreach (var ((kc, m), kinds) in activeKeyGrabs)
					{
						if (kc != xcode || !ModsMatch(m, mods))
							continue;

						if ((kinds & GrabKinds.Hotstring) != 0)
							hotstringGrabbed = true;
						return true;
					}

					return false;
				}

				var grabbed = MatchActiveKeyGrab();

				grabbedByHotstring = hotstringGrabbed;
				if (!grabbed)
					return false;

				var raw = e.RawEvent;
				if ((raw.Mask & EventMask.SuppressEvent) == 0)
				{
					raw.Mask |= EventMask.SuppressEvent;
					SetRawEvent(e, raw);
				}

				return true;
			}
		}

		protected override void DisarmHotstringPlatform()
		{
			if (!IsX11Available)
				return;

			var hotstringGrabs = activeKeyGrabs
				.Where(kvp => (kvp.Value & GrabKinds.Hotstring) != 0)
				.Select(kvp => kvp.Key)
				.ToList();

			foreach (var pair in hotstringGrabs)
			{
				if (activeKeyGrabs.TryGetValue(pair, out var kinds) && kinds == GrabKinds.Hotstring)
					_ = XDisplay.Default.XUngrabKey(pair.xCode, pair.mods);

				RemoveGrabKind(activeKeyGrabs, pair, GrabKinds.Hotstring);
			}

			_ = XDisplay.Default.XUngrabKeyboard(CurrentTime);
			_ = XDisplay.Default.XSync(false);
		}

		protected override bool ArmHotstringForEndsPlatform(HashSet<char> endsToArm)
		{
			if (!IsX11Available)
				return false;

			foreach (var endChar in endsToArm)
			{
				if (!MapEndCharToVkAndNeeds(endChar, out var vk, out var needShift, out var needAltGr))
					continue;

				if (!KeyCodes.ResolveVkToXKeycode(vk, out var xcode))
					continue;

				uint baseMods = 0;
				if (needShift)
					baseMods |= ShiftMask;
				if (needAltGr)
					baseMods |= Mod5Mask;

				var armedEnd = new ArmedEnd
				{
					Keycode = xcode,
					XModsBase = baseMods,
					Vk = vk,
					EndChar = endChar,
					NeedShift = needShift,
					NeedAltGr = needAltGr
				};

				foreach (var mods in EnumerateArmedHotstringModifierVariants(armedEnd))
				{
					var pair = (xcode, mods);
					var alreadyGrabbed = activeKeyGrabs.ContainsKey(pair);
					AddGrabKind(activeKeyGrabs, pair, GrabKinds.Hotstring);

					if (!alreadyGrabbed)
						_ = XDisplay.Default.XGrabKey(xcode, mods, (nint)XDisplay.Default.Root.ID, true, GrabModeAsync, GrabModeAsync);
				}

				hsArmedEnds.Add(armedEnd);
			}

			if (hsArmedEnds.Count == 0)
				return false;

			XDisplay.Default.XSync(false);
			return true;
		}

			protected override void PlatformUngrabAll()
			{
			if (!IsX11Available)
				return;

			lock (hkLock)
			{
				foreach (var ((kc, mods), _) in activeKeyGrabs)
					_ = XDisplay.Default.XUngrabKey(kc, mods);
				foreach (var ((button, mods), _) in activeButtonGrabs)
					_ = XDisplay.Default.XUngrabButton(button, mods);

					activeKeyGrabs.Clear();
					activeButtonGrabs.Clear();
					temporarilyUngrabbedKeycodes.Clear();
					XDisplay.Default.XSync(false);
				}
			}

			private void IncrementTemporarilyUngrabbedKeycode(uint xcode)
			{
				if (xcode == 0)
					return;

				temporarilyUngrabbedKeycodes.TryGetValue(xcode, out var count);
				temporarilyUngrabbedKeycodes[xcode] = count + 1;
			}

		private bool DecrementTemporarilyUngrabbedKeycode(uint xcode)
			{
				if (xcode == 0)
					return false;

				if (!temporarilyUngrabbedKeycodes.TryGetValue(xcode, out var count))
					return false;

				count--;

				if (count <= 0)
				{
					temporarilyUngrabbedKeycodes.Remove(xcode);
					return true;
				}

				temporarilyUngrabbedKeycodes[xcode] = count;
				return false;
			}

		internal override bool HasButtonGrab(uint button)
		{
			if (!IsX11Available || button == 0)
				return false;

			lock (hkLock)
			{
				foreach (var ((btn, _), _) in activeButtonGrabs)
				{
					if (btn == button)
						return true;
				}
			}

			return false;
		}

		private static bool IsModifierVk(uint vk) => vk is
			VK_SHIFT or VK_LSHIFT or VK_RSHIFT or
			VK_CONTROL or VK_LCONTROL or VK_RCONTROL or
			VK_MENU or VK_LMENU or VK_RMENU or
			VK_LWIN or VK_RWIN;

		internal override uint MouseButtonToXButton(MouseButton btn) => btn switch
		{
			MouseButton.Button1 => 1u,
			MouseButton.Button2 => 3u, // X uses 3 for right.
			MouseButton.Button3 => 2u,
			MouseButton.Button4 => 8u,
			MouseButton.Button5 => 9u,
			_ => 0u
		};

		internal override GrabSnapshot BeginSendUngrab(HashSet<uint> keycodes = null, HashSet<uint> buttons = null)
		{
			var snap = new GrabSnapshot { Active = IsX11Available };

			if (!snap.Active)
				return snap;

			IEnumerable<(uint keycode, uint mods)> KeyMatches()
			{
				if (keycodes == null)
					return activeKeyGrabs.Keys.Select(pair => (pair.xCode, pair.mods));

				return activeKeyGrabs.Keys
					.Where(km => keycodes.Contains(km.xCode))
					.Select(pair => (pair.xCode, pair.mods));
			}

			IEnumerable<(uint button, uint mods)> ButtonMatches()
			{
				if (buttons == null)
					return activeButtonGrabs.Keys;

				return activeButtonGrabs.Keys.Where(bm => buttons.Contains(bm.button));
			}

				snap.KeyGrabs = new HashSet<(uint keycode, uint mods)>(KeyMatches());
				snap.ButtonGrabs = new HashSet<(uint button, uint mods)>(ButtonMatches());

				foreach (var keycode in snap.KeyGrabs.Select(item => item.Item1).Distinct())
					IncrementTemporarilyUngrabbedKeycode(keycode);

			try
			{
				foreach (var (keycode, mods) in snap.KeyGrabs)
					_ = XDisplay.Default.XUngrabKey(keycode, mods, (nint)XDisplay.Default.Root.ID);

				foreach (var (button, mods) in snap.ButtonGrabs)
					_ = XDisplay.Default.XUngrabButton(button, mods, (nint)XDisplay.Default.Root.ID);

				_ = XDisplay.Default.XUngrabPointer(CurrentTime);
				_ = XDisplay.Default.XUngrabKeyboard(CurrentTime);
				_ = XDisplay.Default.XSync(false);
				_ = XDisplay.Default.XFlush();
			}
			catch
			{
				// Best-effort.
			}

			return snap;
		}

			internal override void EndSendUngrab(GrabSnapshot snap)
			{
			if (!snap.Active || !IsX11Available)
				return;

				if ((snap.KeyGrabs == null || snap.KeyGrabs.Count == 0)
					&& (snap.ButtonGrabs == null || snap.ButtonGrabs.Count == 0))
					return;

				var keycodesToRestore = new HashSet<uint>();

				if (snap.KeyGrabs != null)
				{
					foreach (var keycode in snap.KeyGrabs.Select(item => item.Item1).Distinct())
					{
						if (DecrementTemporarilyUngrabbedKeycode(keycode))
							keycodesToRestore.Add(keycode);
					}
				}

				try
				{
					foreach (var (xcode, mods) in snap.KeyGrabs)
					{
						if (keycodesToRestore.Contains(xcode))
							_ = XDisplay.Default.XGrabKey(xcode, mods, (nint)XDisplay.Default.Root.ID, true, GrabModeAsync, GrabModeAsync);
					}

				foreach (var (button, mods) in snap.ButtonGrabs)
				{
					_ = XDisplay.Default.XGrabButton(button, mods, (nint)XDisplay.Default.Root.ID, true,
						(uint)(EventMasks.ButtonPress | EventMasks.ButtonRelease), GrabModeAsync, GrabModeAsync, nint.Zero, nint.Zero);
				}

				_ = XDisplay.Default.XSync(false);
				_ = XDisplay.Default.XFlush();
			}
			catch
			{
				// Best-effort.
			}
		}

		internal override uint TemporarilyUngrabKey(uint vk)
		{
			if (!IsX11Available)
				return 0;

			uint deactivated = 0;
			var targetXcode = KeyCodes.MapVkToSc(vk);

			try
			{
					foreach (var ((xcode, mods), _) in activeKeyGrabs)
					{
						if (targetXcode != 0 && xcode == targetXcode)
						{
							_ = XDisplay.Default.XUngrabKey(xcode, mods, (nint)XDisplay.Default.Root.ID);
							deactivated = xcode;
						}
					}

					// Fallback for layouts/keysym mappings where VkToXKeycode doesn't resolve
					// to the same keycode used by active grabs.
					if (deactivated == 0)
					{
						foreach (var ((xcode, mods), _) in activeKeyGrabs)
						{
							if (KeyCodes.MapScToVk(xcode) != vk)
								continue;

							_ = XDisplay.Default.XUngrabKey(xcode, mods, (nint)XDisplay.Default.Root.ID);
							deactivated = xcode;
						}
					}

					if (deactivated != 0)
						IncrementTemporarilyUngrabbedKeycode(deactivated);

				_ = XDisplay.Default.XSync(false);
				_ = XDisplay.Default.XFlush();
			}
			catch
			{
				// Best-effort.
			}

			return deactivated;
		}

			internal override void RegrabKey(uint targetXcode)
			{
				if (targetXcode == 0)
					return;

				if (!DecrementTemporarilyUngrabbedKeycode(targetXcode))
					return;

				try
				{
				foreach (var ((xcode, mods), _) in activeKeyGrabs)
				{
					if (xcode == targetXcode)
						_ = XDisplay.Default.XGrabKey(xcode, mods, (nint)XDisplay.Default.Root.ID, true, GrabModeAsync, GrabModeAsync);
				}

				_ = XDisplay.Default.XSync(false);
				_ = XDisplay.Default.XFlush();
			}
			catch
			{
				// Best-effort.
			}
		}

		internal override void ForceReleaseEndKeyX11(uint vk) => ForceReleaseKeyX11(vk);

		internal override List<ReleasedKey> ForceReleaseKeysForSend(HashSet<uint> vks)
		{
			var released = new List<ReleasedKey>(16);
			var releasedVks = new HashSet<uint>();

			if (!IsX11Available)
				return released;

			var keymap = new byte[32];
			if (XDisplay.Default.XQueryKeymap(keymap) == 0)
				return released;

			for (uint xk = 8; xk < 256; xk++)
			{
				var byteIndex = (int)(xk >> 3);
				var bitMask = 1 << (int)(xk & 7);

				if ((keymap[byteIndex] & bitMask) == 0)
					continue;

				var vk = KeyCodes.MapScToVk(xk);

				if (vk == 0 || IsModifierVk(vk))
					continue;

				if (MouseUtils.IsMouseVK(vk) || MouseUtils.IsWheelVK(vk))
					continue;

				if (!vks.Contains(vk))
					continue;

				if (!releasedVks.Add(vk))
					continue;

				ForceKeyEventX11(vk, isPress: false);
				released.Add(new ReleasedKey(vk));
			}

			return released;
		}

		internal override void RestoreNonModifierKeysAfterSend(List<ReleasedKey> released)
		{
			if (released == null || released.Count == 0)
				return;

			for (int i = 0; i < released.Count; i++)
			{
				var rk = released[i];

				if (!inputState.ShouldRestoreReleasedKey(rk.Vk, IsHotkeySuffixDown(rk.Vk)))
					continue;

				ForceKeyEventX11(rk.Vk, isPress: true);
			}
		}

		/// <summary>
		/// Always queries the live indicator state, bypassing the cached snapshot.
		/// The snapshot is driven by ev.Flags embedded in hook events, which carry
		/// the daemon's PRE-event LED state (EV_LED arrives after EV_KEY), so it is
		/// always one event behind and unsuitable for toggle-key decisions.
		///
		/// Priority: X11/XkbGetState (compositor's actual XKB lock state, always
		/// current even for synthetic uinput events) → inputd GET_KEY_STATE with
		/// a live EVIOCGLED refresh (script thread only, never from hook thread to
		/// avoid deadlock on queryGate) → cached snapshot as a last resort.
		/// </summary>
		internal override bool TryGetIndicatorStates(out bool capsOn, out bool numOn, out bool scrollOn)
		{
			// X11/XWayland reflects the compositor's XKB state and is updated
			// synchronously when the compositor processes any CapsLock event
			// (physical or uinput-synthesised). Safe to call from any thread.
			if (TryGetIndicatorStatesFromX11(out capsOn, out numOn, out scrollOn))
				return true;

			// Pure Wayland without XWayland: query inputd, which does a live
			// EVIOCGLED ioctl on the grabbed keyboard devices.
			// TryGetKeyState acquires queryGate, which the script thread may hold
			// during SendInputViaSynthesisChannel. Calling it from the hook reader
			// thread would deadlock (hook lane waits for HOOK_DECISION while script
			// thread waits for SynthesisResult). Only query from the script thread.
			// Pure Wayland without XWayland: query inputd (EVIOCGLED, fast round-trip).
			if (!UsingInputdHooks)
				return base.TryGetIndicatorStates(out capsOn, out numOn, out scrollOn);

			if (KeysharpInputdManager.TryGetKeyState(out _, out capsOn, out numOn, out scrollOn))
				return true;

			// Last resort: the snapshot (may be stale, but better than nothing).
			return base.TryGetIndicatorStates(out capsOn, out numOn, out scrollOn);
		}

		private static bool TryGetIndicatorStatesFromX11(out bool capsOn, out bool numOn, out bool scrollOn)
		{
			capsOn = numOn = scrollOn = false;
			var display = XDisplay.Default.Handle;

			if (display == IntPtr.Zero)
				return false;

			const uint XkbUseCoreKbd = 0x0100;
			const uint XK_Num_Lock = 0xff7f;
			const uint XK_Scroll_Lock = 0xff14;

			if (XkbGetState(display, XkbUseCoreKbd, out var st) != 0)
				return false;

			capsOn = (st.locked_mods & (byte)LockMask) != 0;

			var numMask = XkbKeysymToModifiers(display, XK_Num_Lock);
			var scrollMask = XkbKeysymToModifiers(display, XK_Scroll_Lock);

			numOn    = (st.locked_mods & (byte)numMask)    != 0;
			scrollOn = (st.locked_mods & (byte)scrollMask) != 0;
			return true;
		}

		internal override void RefreshPlatformKeyGrabs() => RebuildPlatformHotkeyGrabs();

		private static bool TryMapMouseVkToButton(uint vk, out uint button)
		{
			button = vk switch
			{
				VK_LBUTTON => 1u,
				VK_MBUTTON => 2u,
				VK_RBUTTON => 3u,
				VK_XBUTTON1 => 8u,
				VK_XBUTTON2 => 9u,
				_ => 0u
			};
			return button != 0;
		}

		private bool TryMapToXGrab(uint vk, uint modifiersLR, out uint keycode, out uint mods)
		{
			keycode = 0;
			mods = 0;

			if (!KeyCodes.ResolveVkToXKeycode(vk, out keycode))
				return false;

			mods = ModifiersLRToXMask(modifiersLR);
			return true;
		}

		private void BuildInputGrabs(Dictionary<(uint keycode, uint mods), GrabKinds> target)
		{
			var script = Script.TheScript;
			bool grabAllNonModifierKeys = false;

			for (var input = script.input; input != null; input = input.prev)
			{
				if (!input.InProgress())
					continue;

				if (!input.visibleText || !input.visibleNonText || HasSuppressedInputKeys(input))
				{
					grabAllNonModifierKeys = true;
					break;
				}
			}

			if (!grabAllNonModifierKeys)
			{
				for (var input = script.input; input != null; input = input.prev)
				{
					if (!input.InProgress())
						continue;

					AddSpecificSuppressedInputGrabs(target, input);
				}

				return;
			}

			for (uint keycode = 8; keycode < 256; keycode++)
			{
				if (!ShouldGrabForInputSuppression(keycode))
					continue;

				foreach (var pair in KeyGrabVariants(keycode, AnyModifier, anyModifier: true))
					AddGrabKind(target, pair, GrabKinds.Input);
			}
		}

		// Broad invisible InputHook suppression should be keyed by the physical X11 keycode, not solely by
		// whether the current layout can round-trip that key through a known VK. Otherwise layout-specific
		// printable keys can be missed and leak through to the focused app.
		private bool ShouldGrabForInputSuppression(uint keycode)
		{
			if (keycode < 8 || keycode > 255)
				return false;

			var vk = KeyCodes.MapScToVk(keycode);

			if (vk != 0 && kvk != null && vk < kvk.Length)
			{
				if (kvk[vk].asModifiersLR != 0 || kvk[vk].forceToggle != null)
					return false;

				return true;
			}

			for (int index = 0; index < 4; index++)
			{
				var keysym = (ulong)XDisplay.Default.XKeycodeToKeysym((int)keycode, index);

				if (keysym == 0)
					continue;

				if (!IsModifierOrToggleKeysym(keysym))
					return true;
			}

			return false;
		}

		private static bool IsModifierOrToggleKeysym(ulong keysym) => keysym switch
		{
			0xFFE1 or // Shift_L
			0xFFE2 or // Shift_R
			0xFFE3 or // Control_L
			0xFFE4 or // Control_R
			0xFFE5 or // Caps_Lock
			0xFFE6 or // Shift_Lock
			0xFFE7 or // Meta_L
			0xFFE8 or // Meta_R
			0xFFE9 or // Alt_L
			0xFFEA or // Alt_R
			0xFFEB or // Super_L
			0xFFEC or // Super_R
			0xFFED or // Hyper_L
			0xFFEE or // Hyper_R
			0xFF7E or // Mode_switch
			0xFF7F or // Num_Lock
			0xFF14 or // Scroll_Lock
			0xFE03 or // ISO_Level3_Shift
			0xFE11    // ISO_Level5_Shift
				=> true,
			_ => false
		};

		private static bool HasSuppressedInputKeys(InputType input)
		{
			for (int i = 0; i < input.keyVK.Length; i++)
			{
				if ((input.keyVK[i] & INPUT_KEY_SUPPRESS) != 0)
					return true;
			}

			for (int i = 0; i < input.keySC.Length; i++)
			{
				if ((input.keySC[i] & INPUT_KEY_SUPPRESS) != 0)
					return true;
			}

			return false;
		}

		private void AddSpecificSuppressedInputGrabs(Dictionary<(uint keycode, uint mods), GrabKinds> target, InputType input)
		{
			for (uint vk = 0; vk < input.keyVK.Length; vk++)
			{
				if ((input.keyVK[vk] & INPUT_KEY_SUPPRESS) == 0)
					continue;

				if (TryMapToXGrab(vk, 0, out var keycode, out _))
				{
					foreach (var pair in KeyGrabVariants(keycode, AnyModifier, anyModifier: true))
						AddGrabKind(target, pair, GrabKinds.Input);
				}
			}

			for (uint sc = 0; sc < input.keySC.Length; sc++)
			{
				if ((input.keySC[sc] & INPUT_KEY_SUPPRESS) == 0 || sc < 8 || sc > 255)
					continue;

				foreach (var pair in KeyGrabVariants(sc, AnyModifier, anyModifier: true))
					AddGrabKind(target, pair, GrabKinds.Input);
			}
		}

		private void AddArmedHotstringGrabs(Dictionary<(uint keycode, uint mods), GrabKinds> target)
		{
			foreach (var pair in EnumerateArmedHotstringGrabVariants())
				AddGrabKind(target, pair, GrabKinds.Hotstring);
		}

		private IEnumerable<(uint keycode, uint mods)> EnumerateArmedHotstringGrabVariants()
		{
			foreach (var armed in hsArmedEnds)
			{
				foreach (var mods in EnumerateArmedHotstringModifierVariants(armed))
					yield return (armed.Keycode, mods);
			}
		}

		private static IEnumerable<uint> EnumerateArmedHotstringModifierVariants(ArmedEnd armed)
		{
			var modsToGrab = new HashSet<uint>
			{
				armed.XModsBase,
				armed.XModsBase | LockMask,
				armed.XModsBase | Mod2Mask,
				armed.XModsBase | LockMask | Mod2Mask
			};

			if (!armed.NeedShift)
			{
				modsToGrab.Add(armed.XModsBase | ShiftMask);
				modsToGrab.Add(armed.XModsBase | ShiftMask | LockMask);
				modsToGrab.Add(armed.XModsBase | ShiftMask | Mod2Mask);
				modsToGrab.Add(armed.XModsBase | ShiftMask | LockMask | Mod2Mask);
			}

			return modsToGrab;
		}

		private uint CurrentXGrabMask()
		{
			var mods = ModifiersLRToXMask(CurrentModifiersLR());

			if (TryGetIndicatorStates(out var capsOn, out var numOn, out var scrollOn))
			{
				if (capsOn)
					mods |= LockMask;
				if (numOn)
					mods |= Mod2Mask;
				if (scrollOn)
					mods |= Mod5Mask;
			}

			return mods;
		}

		private static uint ModifiersLRToXMask(uint modifiersLR)
		{
			uint mods = 0;

			if ((modifiersLR & (MOD_LCONTROL | MOD_RCONTROL)) != 0)
				mods |= ControlMask;
			if ((modifiersLR & (MOD_LSHIFT | MOD_RSHIFT)) != 0)
				mods |= ShiftMask;
			if ((modifiersLR & (MOD_LALT | MOD_RALT)) != 0)
				mods |= Mod1Mask;
			if ((modifiersLR & (MOD_LWIN | MOD_RWIN)) != 0)
				mods |= Mod4Mask;

			return mods;
		}

		private static uint VkToSelfXModifierMask(uint vk) => vk switch
		{
			VK_LSHIFT or VK_RSHIFT or VK_SHIFT => ShiftMask,
			VK_LCONTROL or VK_RCONTROL or VK_CONTROL => ControlMask,
			VK_LMENU or VK_RMENU or VK_MENU => Mod1Mask,
			VK_LWIN or VK_RWIN => Mod4Mask,
			_ => 0
		};

		private static IEnumerable<uint> KeyGrabMaskVariants(uint modifiers, bool anyModifier = false)
		{
			if (anyModifier)
			{
				yield return AnyModifier;
				yield break;
			}

			yield return modifiers;
			yield return modifiers | LockMask;
			yield return modifiers | Mod2Mask;
			yield return modifiers | LockMask | Mod2Mask;
		}

		private static IEnumerable<(uint keycode, uint mods)> KeyGrabVariants(uint keycode, uint modifiers, bool anyModifier = false)
		{
			foreach (var m in KeyGrabMaskVariants(modifiers, anyModifier))
				yield return (keycode, m);
		}

		// allowExtra hotkeys (e.g. *^a) must keep required modifiers while allowing extras.
		// AnyModifier would also match missing required modifiers and break pass-through.
		private static IEnumerable<(uint keycode, uint mods)> KeyGrabVariantsWithExtra(uint keycode, uint requiredMods)
		{
			var seen = new HashSet<uint>();
			int comboCount = 1 << ExtraGrabMasks.Length;
			var requiredNoLocks = requiredMods & ~(LockMask | Mod2Mask);

			for (int maskBits = 0; maskBits < comboCount; maskBits++)
			{
				uint mods = requiredNoLocks;

				for (int i = 0; i < ExtraGrabMasks.Length; i++)
				{
					if ((maskBits & (1 << i)) != 0)
						mods |= ExtraGrabMasks[i];
				}

				if (!seen.Add(mods))
					continue;

				yield return (keycode, mods);
				yield return (keycode, mods | LockMask);
				yield return (keycode, mods | Mod2Mask);
				yield return (keycode, mods | LockMask | Mod2Mask);
			}
		}

		private static IEnumerable<uint> ButtonGrabMaskVariants(uint modifiers, bool allowExtra)
		{
			if (!allowExtra)
			{
				yield return modifiers;
				yield return modifiers | LockMask;
				yield return modifiers | Mod2Mask;
				yield return modifiers | LockMask | Mod2Mask;
				yield break;
			}

			var seen = new HashSet<uint>();
			int comboCount = 1 << ExtraGrabMasks.Length;
			for (int maskBits = 0; maskBits < comboCount; maskBits++)
			{
				uint mods = modifiers;
				for (int i = 0; i < ExtraGrabMasks.Length; i++)
				{
					if ((maskBits & (1 << i)) != 0)
						mods |= ExtraGrabMasks[i];
				}

				if (seen.Add(mods))
					yield return mods;
			}
		}

		private static void ApplyGrabDiff<TKey>(Dictionary<TKey, GrabKinds> current, Dictionary<TKey, GrabKinds> desired, Action<TKey> ungrab, Action<TKey> grab) where TKey : notnull
		{
			foreach (var oldVal in current.Keys)
			{
				if (!desired.ContainsKey(oldVal))
					ungrab(oldVal);
			}

			foreach (var add in desired.Keys)
			{
				if (!current.ContainsKey(add))
					grab(add);
			}

			current.Clear();
			foreach (var kvp in desired)
				current[kvp.Key] = kvp.Value;
		}

		private bool TryQueryKeymap(out byte[] keymap)
		{
			keymap = new byte[32];

			if (!IsX11Available)
				return false;

			return XDisplay.Default.XQueryKeymap(keymap) != 0;
		}

		private void ForceReleaseKeyX11(uint vk)
		{
			if (!IsX11Available || vk == 0)
				return;

			if (kbdMsSender is UnixKeyboardMouseSender sender)
				sender.SimulateKeyEvent(vk, isPress: false, KeyIgnoreAllExceptModifier);

			inputState.ApplySyntheticKeyEvent(vk, isPress: false);
		}

		private void ForceKeyEventX11(uint vk, bool isPress)
		{
			if (!IsX11Available || vk == 0)
				return;

			if (kbdMsSender is UnixKeyboardMouseSender sender)
				sender.SimulateKeyEvent(vk, isPress, KeyIgnoreAllExceptModifier);

			inputState.ApplySyntheticKeyEvent(vk, isPress);
		}
	}
}
#endif
