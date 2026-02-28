#if LINUX
using Keysharp.Core.Common.Mouse;
using Keysharp.Core.Linux.Proxies;
using SharpHook;
using SharpHook.Data;
using static Keysharp.Core.Common.Keyboard.KeyboardMouseSender;
using static Keysharp.Core.Common.Keyboard.KeyboardUtils;
using static Keysharp.Core.Common.Keyboard.VirtualKeys;
using static Keysharp.Core.Linux.X11.Xlib;
using static Keysharp.Core.Unix.PlatformManager;

namespace Keysharp.Core.Unix
{
	// Linux-only X11 extension points for UnixHookThread.
	internal sealed class LinuxHookThread : UnixHookThread
	{
		private const uint Mod5Mask = 1 << 7; // Usually ISO_Level3_Shift (AltGr) on X11.
		private static readonly uint[] ExtraGrabMasks = { ControlMask, ShiftMask, Mod1Mask, Mod4Mask, Mod5Mask };

		protected override void RebuildPlatformHotkeyGrabs()
		{
			var newKeyGrabs = new HashSet<(uint keycode, uint mods)>();
			var newButtonGrabs = new HashSet<(uint button, uint mods)>();

			if (!IsX11Available)
			{
				activeGrabs.Clear();
				activeButtonGrabs.Clear();
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
							newButtonGrabs.Add((button, m));
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
						foreach (var pair in KeyGrabVariants(keycode, mods, entry.AllowExtra))
							newKeyGrabs.Add(pair);
					}
			}

			foreach (var kvp in customPrefixSuppress)
			{
				if (!kvp.Value)
					continue;

				if (TryMapToXGrab(kvp.Key, 0, out var prefixKeycode, out _))
				{
					foreach (var pair in KeyGrabVariants(prefixKeycode, AnyModifier, anyModifier: true))
						newKeyGrabs.Add(pair);
				}
			}

			ApplyGrabDiff(activeGrabs, newKeyGrabs,
				ungrab: (kc, m) => _ = XDisplay.Default.XUngrabKey(kc, m),
				grab: (kc, m) => _ = XDisplay.Default.XGrabKey(kc, m, (nint)XDisplay.Default.Root.ID, true, GrabModeAsync, GrabModeAsync));

			ApplyGrabDiff(activeButtonGrabs, newButtonGrabs,
				ungrab: (button, m) => _ = XDisplay.Default.XUngrabButton(button, m, (nint)XDisplay.Default.Root.ID),
				grab: (button, m) => _ = XDisplay.Default.XGrabButton(button, m, (nint)XDisplay.Default.Root.ID, true,
					(uint)(EventMasks.ButtonPress | EventMasks.ButtonRelease), GrabModeAsync, GrabModeAsync, nint.Zero, nint.Zero));

			XDisplay.Default.XSync(false);
		}

		protected override void InitSnapshotFromPlatform()
		{
			System.Array.Clear(physicalKeyState, 0, physicalKeyState.Length);
			System.Array.Clear(logicalKeyState, 0, logicalKeyState.Length);
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

				var vk = VkFromKeysym((ulong)ks);
				if (vk == 0 || vk >= physicalKeyState.Length)
					continue;

				physicalKeyState[vk] = StateDown;
				logicalKeyState[vk] = StateDown;
			}

			if (!TryQueryModifierLRStatePlatform(out var modsInit, km))
				return;

			kbdMsSender.modifiersLRPhysical = modsInit;
			kbdMsSender.modifiersLRLogical = modsInit;
			kbdMsSender.modifiersLRLogicalNonIgnored = modsInit;

			void SetLogical(uint modMask, uint vk)
			{
				if ((modsInit & modMask) != 0 && vk < logicalKeyState.Length)
					logicalKeyState[vk] = StateDown;
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

			var expected = VkToKeysyms(vk);
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

		protected override bool WasKeyGrabbedPlatform(HookEventArgs e, uint vk, out bool grabbedByHotstring)
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

				uint mods = CurrentXGrabMask();
				bool hotstringGrabbed = false;

				static bool ModsMatch(uint grabbed, uint actual)
				{
					const uint lockBits = LockMask | Mod2Mask;
					return grabbed == AnyModifier || grabbed == actual || (grabbed & ~lockBits) == (actual & ~lockBits);
				}

				bool MatchList(HashSet<(uint keycode, uint mods)> list, bool markHotstring)
				{
					foreach (var (kc, m) in list)
					{
						if (kc != xcode || !ModsMatch(m, mods))
							continue;

						if (markHotstring)
							hotstringGrabbed = true;
						return true;
					}
					return false;
				}

				var grabbed = MatchList(activeGrabs, markHotstring: false)
					|| MatchList(activeHotstringGrabs, markHotstring: true)
					|| MatchList(hsActiveGrabVariants, markHotstring: true);

				grabbedByHotstring = hotstringGrabbed;
				if (!grabbed)
					return false;

				DebugLog($"[Hook] WasKeyGrabbed kc={xcode} mods={mods:X} hotstring={grabbedByHotstring}");

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

			foreach (var (keycode, mods) in hsActiveGrabVariants)
				_ = XDisplay.Default.XUngrabKey(keycode, mods);

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

				var keysym = VkToKeysym(vk);
				if (keysym == 0)
					continue;

				uint xcode = XDisplay.Default.XKeysymToKeycode((IntPtr)keysym);
				if (xcode == 0)
					continue;

				uint baseMods = 0;
				if (needShift)
					baseMods |= ShiftMask;
				if (needAltGr)
					baseMods |= Mod5Mask;

				var modsToGrab = new HashSet<uint>
				{
					baseMods,
					baseMods | LockMask,
					baseMods | Mod2Mask,
					baseMods | LockMask | Mod2Mask
				};

				if (!needShift)
				{
					modsToGrab.Add(baseMods | ShiftMask);
					modsToGrab.Add(baseMods | ShiftMask | LockMask);
					modsToGrab.Add(baseMods | ShiftMask | Mod2Mask);
					modsToGrab.Add(baseMods | ShiftMask | LockMask | Mod2Mask);
				}

				foreach (var mods in modsToGrab)
				{
					_ = XDisplay.Default.XGrabKey(xcode, mods, (nint)XDisplay.Default.Root.ID, true, GrabModeAsync, GrabModeAsync);
					hsActiveGrabVariants.Add((xcode, mods));
					activeHotstringGrabs.Add((xcode, mods));
				}

				hsArmedEnds.Add(new ArmedEnd
				{
					Keycode = xcode,
					XModsBase = baseMods,
					Vk = vk,
					EndChar = endChar,
					NeedShift = needShift,
					NeedAltGr = needAltGr
				});
			}

			if (hsActiveGrabVariants.Count == 0)
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
				foreach (var (kc, mods) in activeGrabs)
					_ = XDisplay.Default.XUngrabKey(kc, mods);
				foreach (var (button, mods) in activeButtonGrabs)
					_ = XDisplay.Default.XUngrabButton(button, mods);

				activeGrabs.Clear();
				activeHotstringGrabs.Clear();
				activeButtonGrabs.Clear();
				XDisplay.Default.XSync(false);
			}
		}

		internal override bool HasButtonGrab(uint button)
		{
			if (!IsX11Available || button == 0)
				return false;

			lock (hkLock)
			{
				foreach (var (btn, _) in activeButtonGrabs)
				{
					if (btn == button)
						return true;
				}
			}

			return false;
		}

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

			IEnumerable<(uint keycode, uint mods)> KeyMatches(HashSet<(uint keycode, uint mods)> src)
			{
				if (keycodes == null)
					return src;
				return src.Where(km => keycodes.Contains(km.keycode));
			}

			IEnumerable<(uint button, uint mods)> ButtonMatches(HashSet<(uint button, uint mods)> src)
			{
				if (buttons == null)
					return src;
				return src.Where(bm => buttons.Contains(bm.button));
			}

			snap.Grabs = new HashSet<(uint keycode, uint mods)>(KeyMatches(activeGrabs));
			snap.HotstringGrabs = new HashSet<(uint keycode, uint mods)>(KeyMatches(activeHotstringGrabs));
			snap.ButtonGrabs = new HashSet<(uint button, uint mods)>(ButtonMatches(activeButtonGrabs));

			try
			{
				foreach (var (keycode, mods) in snap.Grabs)
					_ = XDisplay.Default.XUngrabKey(keycode, mods, (nint)XDisplay.Default.Root.ID);

				foreach (var (keycode, mods) in snap.HotstringGrabs)
					_ = XDisplay.Default.XUngrabKey(keycode, mods, (nint)XDisplay.Default.Root.ID);

				foreach (var (button, mods) in snap.ButtonGrabs)
					_ = XDisplay.Default.XUngrabButton(button, mods, (nint)XDisplay.Default.Root.ID);

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

			if ((snap.Grabs == null || snap.Grabs.Count == 0)
				&& (snap.HotstringGrabs == null || snap.HotstringGrabs.Count == 0)
				&& (snap.ButtonGrabs == null || snap.ButtonGrabs.Count == 0))
				return;

			try
			{
				foreach (var (xcode, mods) in snap.Grabs)
					_ = XDisplay.Default.XGrabKey(xcode, mods, (nint)XDisplay.Default.Root.ID, true, GrabModeAsync, GrabModeAsync);

				foreach (var (xcode, mods) in snap.HotstringGrabs)
					_ = XDisplay.Default.XGrabKey(xcode, mods, (nint)XDisplay.Default.Root.ID, true, GrabModeAsync, GrabModeAsync);

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
			var targetXcode = VkToXKeycode(vk);

			if (targetXcode == 0)
				return 0;

			try
			{
				foreach (var (xcode, mods) in activeGrabs)
				{
					if (xcode == targetXcode)
					{
						_ = XDisplay.Default.XUngrabKey(xcode, mods, (nint)XDisplay.Default.Root.ID);
						deactivated = xcode;
					}
				}

				foreach (var (xcode, mods) in activeHotstringGrabs)
				{
					if (xcode == targetXcode)
					{
						_ = XDisplay.Default.XUngrabKey(xcode, mods, (nint)XDisplay.Default.Root.ID);
						deactivated = xcode;
					}
				}

				_ = XDisplay.Default.XUngrabKeyboard(CurrentTime);
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

			try
			{
				foreach (var (xcode, mods) in activeGrabs)
				{
					if (xcode == targetXcode)
						_ = XDisplay.Default.XGrabKey(xcode, mods, (nint)XDisplay.Default.Root.ID, true, GrabModeAsync, GrabModeAsync);
				}

				foreach (var (xcode, mods) in activeHotstringGrabs)
				{
					if (xcode == targetXcode)
						_ = XDisplay.Default.XGrabKey(xcode, mods, (nint)XDisplay.Default.Root.ID, true, GrabModeAsync, GrabModeAsync);
				}

				_ = XDisplay.Default.XUngrabKeyboard(CurrentTime);
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

				var vk = MapScToVk(xk);

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

				if (rk.Vk == 0 || rk.Vk >= physicalKeyState.Length)
					continue;

				if ((physicalKeyState[rk.Vk] & StateDown) == 0)
					continue;

				if (IsHotkeySuffixDown(rk.Vk))
					continue;

				ForceKeyEventX11(rk.Vk, isPress: true);
			}
		}

		internal override uint VkToXKeycode(uint vk)
		{
			return MapVkToSc(vk, false);
		}

		internal override bool TryGetIndicatorStates(out bool capsOn, out bool numOn, out bool scrollOn)
		{
			if (base.TryGetIndicatorStates(out capsOn, out numOn, out scrollOn))
				return true;

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

			numOn = (st.locked_mods & (byte)numMask) != 0;
			scrollOn = (st.locked_mods & (byte)scrollMask) != 0;
			return true;
		}

		protected override uint MapScToVkPlatform(uint sc)
		{
			if (!IsX11Available || sc == 0)
				return 0;

			var keysym = (ulong)XDisplay.Default.XKeycodeToKeysym((int)sc, 0);
			return keysym != 0 ? VkFromKeysym(keysym) : 0;
		}

		protected override uint MapVkToScPlatform(uint vk, bool returnSecondary = false)
		{
			if (!IsX11Available || vk == 0)
				return 0;

			ulong keysym = VkToKeysym(vk);
			if (keysym == 0)
				return 0;

			return (uint)XDisplay.Default.XKeysymToKeycode((IntPtr)keysym);
		}

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

			if (!IsX11Available)
				return false;

			ulong keysym = VkToKeysym(vk);
			if (keysym == 0)
				return false;

			keycode = XDisplay.Default.XKeysymToKeycode((IntPtr)keysym);
			if (keycode == 0)
				return false;

			mods = ModifiersLRToXMask(modifiersLR);
			return true;
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

		private static void ApplyGrabDiff(HashSet<(uint first, uint second)> current, HashSet<(uint first, uint second)> desired, Action<uint, uint> ungrab, Action<uint, uint> grab)
		{
			foreach (var oldVal in current)
			{
				if (!desired.Contains(oldVal))
					ungrab(oldVal.first, oldVal.second);
			}

			foreach (var add in desired)
			{
				if (!current.Contains(add))
					grab(add.first, add.second);
			}

			current.Clear();
			foreach (var v in desired)
				current.Add(v);
		}

		private bool TryQueryKeymap(out byte[] keymap)
		{
			keymap = new byte[32];

			if (!IsX11Available)
				return false;

			return XDisplay.Default.XQueryKeymap(keymap) != 0;
		}

		private static List<uint> VkToKeysyms(uint vk)
		{
			var list = new List<uint>(2);

			if (vk is >= (uint)'A' and <= (uint)'Z')
			{
				list.Add(vk);
				list.Add(vk + 32);
				return list;
			}

			if (vk is >= (uint)'0' and <= (uint)'9')
			{
				list.Add(vk);
				return list;
			}

			switch (vk)
			{
				case VK_SPACE: list.Add((uint)' '); return list;
				case VK_OEM_MINUS: list.Add((uint)'-'); list.Add((uint)'_'); return list;
				case VK_OEM_PLUS: list.Add((uint)'='); list.Add((uint)'+'); return list;
				case VK_OEM_1: list.Add((uint)';'); list.Add((uint)':'); return list;
				case VK_OEM_2: list.Add((uint)'/'); list.Add((uint)'?'); return list;
				case VK_OEM_3: list.Add((uint)'`'); list.Add((uint)'~'); return list;
				case VK_OEM_4: list.Add((uint)'['); list.Add((uint)'{'); return list;
				case VK_OEM_5: list.Add((uint)'\\'); list.Add((uint)'|'); return list;
				case VK_OEM_6: list.Add((uint)']'); list.Add((uint)'}'); return list;
				case VK_OEM_7: list.Add((uint)'\''); list.Add((uint)'"'); return list;
				case VK_OEM_COMMA: list.Add((uint)','); list.Add((uint)'<'); return list;
				case VK_OEM_PERIOD: list.Add((uint)'.'); list.Add((uint)'>'); return list;
			}

			switch (vk)
			{
				case VK_LSHIFT: list.Add(0xFFE1); break;
				case VK_RSHIFT: list.Add(0xFFE2); break;
				case VK_LCONTROL: list.Add(0xFFE3); break;
				case VK_RCONTROL: list.Add(0xFFE4); break;
				case VK_LMENU: list.Add(0xFFE9); break;
				case VK_RMENU: list.Add(0xFFEA); break;
				case VK_LWIN: list.Add(0xFFEB); break;
				case VK_RWIN: list.Add(0xFFEC); break;
				case VK_RETURN: list.Add(0xFF0D); break;
				case VK_TAB: list.Add(0xFF09); break;
				case VK_ESCAPE: list.Add(0xFF1B); break;
				case VK_BACK: list.Add(0xFF08); break;
				case VK_DELETE: list.Add(0xFFFF); break;
				case VK_INSERT: list.Add(0xFF63); break;
				case VK_HOME: list.Add(0xFF50); break;
				case VK_END: list.Add(0xFF57); break;
				case VK_PRIOR: list.Add(0xFF55); break;
				case VK_NEXT: list.Add(0xFF56); break;
				case VK_LEFT: list.Add(0xFF51); break;
				case VK_UP: list.Add(0xFF52); break;
				case VK_RIGHT: list.Add(0xFF53); break;
				case VK_DOWN: list.Add(0xFF54); break;
			}

			return list;
		}

		private static ulong VkToKeysym(uint vk)
		{
			if (vk >= 'A' && vk <= 'Z')
				return vk;
			if (vk >= '0' && vk <= '9')
				return vk;

			return vk switch
			{
				VK_LSHIFT => XStringToKeysym("Shift_L"),
				VK_RSHIFT => XStringToKeysym("Shift_R"),
				VK_LCONTROL => XStringToKeysym("Control_L"),
				VK_RCONTROL => XStringToKeysym("Control_R"),
				VK_LMENU => XStringToKeysym("Alt_L"),
				VK_RMENU => XStringToKeysym("Alt_R"),
				VK_LWIN => XStringToKeysym("Super_L"),
				VK_RWIN => XStringToKeysym("Super_R"),
				VK_CAPITAL => XStringToKeysym("Caps_Lock"),
				VK_NUMLOCK => XStringToKeysym("Num_Lock"),
				VK_SCROLL => XStringToKeysym("Scroll_Lock"),
				VK_BACK => XStringToKeysym("BackSpace"),
				VK_DELETE => XStringToKeysym("Delete"),
				VK_INSERT => XStringToKeysym("Insert"),
				VK_HOME => XStringToKeysym("Home"),
				VK_END => XStringToKeysym("End"),
				VK_PRIOR => XStringToKeysym("Prior"),
				VK_NEXT => XStringToKeysym("Next"),
				VK_SNAPSHOT => XStringToKeysym("Print"),
				VK_PAUSE => XStringToKeysym("Pause"),
				VK_APPS => XStringToKeysym("Menu"),
				VK_RETURN => XStringToKeysym("Return"),
				VK_TAB => XStringToKeysym("Tab"),
				VK_ESCAPE => XStringToKeysym("Escape"),
				VK_SPACE => XStringToKeysym("space"),
				VK_LEFT => XStringToKeysym("Left"),
				VK_RIGHT => XStringToKeysym("Right"),
				VK_UP => XStringToKeysym("Up"),
				VK_DOWN => XStringToKeysym("Down"),
				VK_OEM_MINUS => XStringToKeysym("minus"),
				VK_OEM_PLUS => XStringToKeysym("equal"),
				VK_OEM_4 => XStringToKeysym("bracketleft"),
				VK_OEM_6 => XStringToKeysym("bracketright"),
				VK_OEM_5 => XStringToKeysym("backslash"),
				VK_OEM_1 => XStringToKeysym("semicolon"),
				VK_OEM_7 => XStringToKeysym("apostrophe"),
				VK_OEM_COMMA => XStringToKeysym("comma"),
				VK_OEM_PERIOD => XStringToKeysym("period"),
				VK_OEM_2 => XStringToKeysym("slash"),
				VK_OEM_3 => XStringToKeysym("grave"),
				_ when (vk >= VK_F1 && vk <= VK_F24) => XStringToKeysym($"F{(int)(vk - VK_F1 + 1)}"),
				_ when (vk >= VK_NUMPAD0 && vk <= VK_NUMPAD9) => XStringToKeysym($"KP_{(int)(vk - VK_NUMPAD0)}"),
				VK_DIVIDE => XStringToKeysym("KP_Divide"),
				VK_MULTIPLY => XStringToKeysym("KP_Multiply"),
				VK_SUBTRACT => XStringToKeysym("KP_Subtract"),
				VK_ADD => XStringToKeysym("KP_Add"),
				VK_DECIMAL => XStringToKeysym("KP_Decimal"),
				VK_SEPARATOR => XStringToKeysym("KP_Separator"),
				_ => 0
			};
		}

		private static uint VkFromKeysym(ulong ks)
		{
			if ((ks >= 'A' && ks <= 'Z') || (ks >= 'a' && ks <= 'z'))
				return (uint)char.ToUpperInvariant((char)ks);
			if (ks >= '0' && ks <= '9')
				return (uint)ks;

			if (ks >= 0xFFB0 && ks <= 0xFFB9)
				return (uint)(VK_NUMPAD0 + (ks - 0xFFB0));

			if (ks >= 0xFFBE && ks <= 0xFFD5)
				return (uint)(VK_F1 + (ks - 0xFFBE));

			return ks switch
			{
				0xFFE1 => VK_LSHIFT,
				0xFFE2 => VK_RSHIFT,
				0xFFE3 => VK_LCONTROL,
				0xFFE4 => VK_RCONTROL,
				0xFFE9 => VK_LMENU,
				0xFFEA => VK_RMENU,
				0xFFEB => VK_LWIN,
				0xFFEC => VK_RWIN,
				0xFFE5 => VK_CAPITAL,
				0xFF7F => VK_NUMLOCK,
				0xFF14 => VK_SCROLL,
				0xFF08 => VK_BACK,
				0xFFFF => VK_DELETE,
				0xFF63 => VK_INSERT,
				0xFF50 => VK_HOME,
				0xFF57 => VK_END,
				0xFF55 => VK_PRIOR,
				0xFF56 => VK_NEXT,
				0xFF61 => VK_SNAPSHOT,
				0xFF13 => VK_PAUSE,
				0xFF67 => VK_APPS,
				0xFF0D => VK_RETURN,
				0xFF09 => VK_TAB,
				0xFF1B => VK_ESCAPE,
				0x0020 => VK_SPACE,
				0xFF51 => VK_LEFT,
				0xFF53 => VK_RIGHT,
				0xFF52 => VK_UP,
				0xFF54 => VK_DOWN,
				0x002D => VK_OEM_MINUS,
				0x003D => VK_OEM_PLUS,
				0x005B => VK_OEM_4,
				0x005D => VK_OEM_6,
				0x005C => VK_OEM_5,
				0x003B => VK_OEM_1,
				0x0027 => VK_OEM_7,
				0x002C => VK_OEM_COMMA,
				0x002E => VK_OEM_PERIOD,
				0x002F => VK_OEM_2,
				0x0060 => VK_OEM_3,
				0xFFAF => VK_DIVIDE,
				0xFFAA => VK_MULTIPLY,
				0xFFAD => VK_SUBTRACT,
				0xFFAB => VK_ADD,
				0xFFAE => VK_DECIMAL,
				0xFFAC => VK_SEPARATOR,
				_ => 0
			};
		}

		private void ForceReleaseKeyX11(uint vk)
		{
			if (!IsX11Available || vk == 0)
				return;

			if (kbdMsSender is UnixKeyboardMouseSender sender)
				sender.SimulateKeyEvent(vk, isPress: false, KeyIgnoreAllExceptModifier);

			if (vk < logicalKeyState.Length)
				logicalKeyState[vk] = 0;
		}

		private void ForceKeyEventX11(uint vk, bool isPress)
		{
			if (!IsX11Available || vk == 0)
				return;

			if (kbdMsSender is UnixKeyboardMouseSender sender)
				sender.SimulateKeyEvent(vk, isPress, KeyIgnoreAllExceptModifier);

			if (vk < logicalKeyState.Length)
				logicalKeyState[vk] = (byte)(isPress ? StateDown : 0);
		}
	}
}
#endif
