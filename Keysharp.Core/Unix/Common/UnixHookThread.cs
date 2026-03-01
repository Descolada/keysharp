#if !WINDOWS
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using SharpHook;
using SharpHook.Data;
using static Keysharp.Core.Common.Keyboard.KeyboardUtils;
using static Keysharp.Core.Common.Keyboard.VirtualKeys;
using Keysharp.Core.Common.Mouse;
using static Keysharp.Core.Unix.SharpHookKeyMapper;
using static Keysharp.Core.Common.Keyboard.KeyboardMouseSender;
using static Keysharp.Core.Unix.PlatformManager;

namespace Keysharp.Core.Unix
{
	/// <summary>
	/// Shared Unix hook thread implementation.
	/// Platform-specific grabbing/query behavior is delegated to overrides.
	/// </summary>
	internal class UnixHookThread : Keysharp.Core.Common.Threading.HookThread
	{
		private static readonly bool HookDisabled = ShouldDisableHook();
		[Conditional("DEBUG")]
		protected static void DebugLog(string message) => _ = Ks.OutputDebugLine(message);

		// --- SharpHook ---
		private SimpleGlobalHook globalHook;
		private Task hookRunTask;

		private readonly Lock hookStateLock = new();

		// Cached on/off
		private volatile bool keyboardEnabled;
		private volatile bool mouseEnabled;

		private IndicatorSnapshot indicatorSnapshot = new(false, false, false);
		private volatile bool indicatorSnapshotValid;

		private readonly struct IndicatorSnapshot
		{
			public readonly bool Caps;
			public readonly bool Num;
			public readonly bool Scroll;
			public IndicatorSnapshot(bool caps, bool num, bool scroll)
			{
				Caps = caps; Num = num; Scroll = scroll;
			}
		}

		private void ResetIndicatorSnapshot()
		{
			indicatorSnapshot = new IndicatorSnapshot(false, false, false);
			indicatorSnapshotValid = false;
		}

		private void UpdateIndicatorSnapshotFromMask(EventMask mask)
		{
			indicatorSnapshot = new IndicatorSnapshot(
				(mask & EventMask.CapsLock) != 0,
				(mask & EventMask.NumLock) != 0,
				(mask & EventMask.ScrollLock) != 0);
			indicatorSnapshotValid = true;
		}

		protected readonly Dictionary<uint, bool> customPrefixSuppress = new();
		protected readonly Dictionary<uint, List<(uint keycode, uint mods)>> dynamicPrefixGrabs = new();
		private uint activeHotkeyVk;
		private KeyCode activeHotkeyKc;
		private bool activeHotkeyDown;
		protected readonly byte[] logicalKeyState = new byte[VK_ARRAY_COUNT];
		internal uint ActiveHotkeyVk => activeHotkeyVk;
		internal KeyCode ActiveHotkeyKc => activeHotkeyKc;
		internal bool SendInProgress => sendInProgress;
		internal bool IsHotkeySuffixDown(uint vk) => activeHotkeyDown && activeHotkeyVk == vk;

		internal readonly struct SyntheticToken
		{
			internal readonly DateTimeOffset EnqueueTime;   // TickCount64-based
			internal readonly KeyCode KeyCode;
			internal readonly bool KeyUp;
			internal readonly long ExtraInfo;     // what you want hook to see
			internal SyntheticToken(KeyCode keyCode, bool keyUp, DateTimeOffset ms, long extraInfo)
			{
				KeyCode = keyCode;
				KeyUp = keyUp;
				EnqueueTime = ms;
				ExtraInfo = extraInfo;
			}
		}

		private readonly List<SyntheticToken> pendingSynthetic = new();
		internal const long SyntheticEventTimeoutMs = 200;
		private readonly Dictionary<uint, int> suppressedHotkeyReleases = new();
		private char lastTypedChar;
		private uint lastKeyboardEventVk;
		private bool lastHookEventWasKeyboard;


		// Hotstring press-time trigger helpers
		private ulong lastTypedExtraInfo = (ulong)(SendLevelMax + 1); // tracks last KeyTyped extra info for InputLevel checks

		private static CaseConformModes ComputeCaseMode(HotstringDefinition hs, List<char> buf)
		{
			if (!hs.conformToCase || buf.Count == 0)
				return CaseConformModes.None;

			var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(buf);
			int end = span.Length - (hs.endCharRequired ? 1 : 0);
			if (end <= 0)
				return CaseConformModes.None;

			int start = Math.Max(0, end - hs.str.Length);
			int cap = 0, up = 0;

			for (int i = start; i < end; i++)
			{
				char c = span[i];
				if (char.IsLetter(c)) { cap++; if (char.IsUpper(c)) up++; }
			}

			if (cap == 0)
				return CaseConformModes.None;

			if (up == cap)
				return CaseConformModes.AllCaps;
			if (up == 0)
				return CaseConformModes.None;
			if (char.IsLetter(span[start]) && char.IsUpper(span[start]))
				return CaseConformModes.FirstCap;

			return CaseConformModes.None;
		}

		// Hotstring arming state
		protected bool hsArmed;   // any end-keys are armed right now?

		// Each armed end-key we grab (base grab; variants for Caps/NumLock are stored in hsActiveGrabVariants)
		protected readonly HashSet<ArmedEnd> hsArmedEnds = new();

		protected readonly HashSet<(uint keycode, uint mods)> hsActiveGrabVariants = new(); // to ungrab fast

		protected struct ArmedEnd
		{
			public uint Keycode;             // X11 keycode we grabbed
			public uint XModsBase;          // base X11 modifiers used for grab (without Lock/NumLock variants)
			public uint Vk;                 // engine VK for that end key
			public char EndChar;            // actual end character we will append to the hs buffer
			public bool NeedShift;          // required to produce EndChar
			public bool NeedAltGr;          // required to produce EndChar (ISO_Level3 / Mod5)
		}

		private enum LockKeyKind
		{
			None,
			CapsLock,
			NumLock,
			ScrollLock
		}

		private struct CustomPrefixState
		{
			public uint Vk;
			public bool Suppressed;
			public LockKeyKind LockKind;
			public bool LockState;
			public bool LockCaptured;
			public bool Used;           // any other key pressed while held?
			public bool FireOnRelease;  // whether to fire the prefix hotkey on release
			public void Reset()
			{
				Vk = 0;
				Suppressed = false;
				LockKind = LockKeyKind.None;
				LockState = false;
				LockCaptured = false;
				Used = false;
				FireOnRelease = false;
			}
		}

		// --- Simple hotkey map for Linux (vk + LR-mods, up/down) ---
		internal readonly Lock hkLock = new();
		protected readonly List<LinuxHotkey> linuxHotkeys = new(); // minimal matcher

		protected struct LinuxHotkey
		{
			public uint IdWithFlags;
			public uint Vk;
			public uint ModifiersLR; // LR-specific mask (MOD_* bits)
			public uint ModifierVK;   // custom prefix VK (0 if none)
			public bool KeyUp;       // is a key-up hotkey
			public bool PassThrough; // ~ tilde present => don't grab
			public bool AllowExtra;  // wildcard (*) hotkey: allow extra modifiers
		}

		// Tracks plain hotkeys grabbed via XGrabKey as well as dynamic grabs for custom prefixes.
		protected readonly HashSet<(uint xCode, uint mods)> activeGrabs = new();
		protected readonly HashSet<(uint xCode, uint mods)> activeHotstringGrabs = new();
		protected readonly HashSet<(uint button, uint mods)> activeButtonGrabs = new();
		private bool sendInProgress;
		private int sendDepth;
		internal readonly struct SendScope : IDisposable
		{
			private readonly UnixHookThread owner;
			public SendScope(UnixHookThread owner)
			{
				this.owner = owner;
				owner.sendDepth++;
				owner.sendInProgress = true;
			}

			public void Dispose()
			{
				owner.sendDepth = Math.Max(0, owner.sendDepth - 1);
				owner.sendInProgress = owner.sendDepth > 0;
				if (!owner.sendInProgress)
					owner.SyncModifiersAfterSend();
			}
		}
		internal struct GrabSnapshot
		{
			public bool Active;
			public HashSet<(uint xcode, uint mods)> Grabs;
			public HashSet<(uint xcode, uint mods)> HotstringGrabs;
			public HashSet<(uint button, uint mods)> ButtonGrabs;
		}

		internal UnixHookThread()
		{
			kbdMsSender = new UnixKeyboardMouseSender();
		}

		internal SendScope EnterSendScope() => new(this);

		private void SyncModifiersAfterSend()
		{
			// Ensure modifiers don't stay logically down after Send manipulates them.
			_ = kbdMsSender?.GetModifierLRState(true);
		}

		// -------------------- lifecycle --------------------
		protected internal override void DeregisterHooks()
		{
			StopGlobalHook();
			PlatformUngrabAll();
		}

		public override void SimulateKeyPress(uint key)
		{
			uint vk = 0;

			// On non-Windows tests, callers may pass Eto.Forms.Keys values.
			// Prefer parsing the platform key enum name first (e.g. "B", "Enter").
			var formKeyName = ((Forms.Keys)key).ToString();
			if (!string.IsNullOrEmpty(formKeyName) && !char.IsDigit(formKeyName[0]))
			{
				uint? mods = null;
				vk = TextToVK(formKeyName.AsSpan(), ref mods, false, false, 0);
			}

			// Fallback: treat as WinForms virtual-key value.
			if (vk == 0)
				vk = (uint)((Keys)key & Keys.KeyCode);

			if (vk == 0)
				return;

			var kc = VkToKeyCode(vk);
			if (kc == KeyCode.VcUndefined)
				return;

			var sc = VkToXKeycode(vk);
			if (sc == 0)
				sc = MapVkToSc(vk);

			var raw = new UioHookEvent
			{
				Type = EventType.KeyPressed,
				Time = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
				Mask = EventMask.None,
				Keyboard = new KeyboardEventData
				{
					KeyCode = kc,
					RawCode = (ushort)sc,
					RawKeyChar = KeyboardEventData.RawUndefinedChar
				}
			};

			var e = new KeyboardHookEventArgs(raw);
			OnKeyPressed(this, e);
		}

		// -------------------- enable/disable --------------------

		internal override void AddRemoveHooks(HookType hooksToBeActive, bool changeIsTemporary = false)
		{
			ChangeHookStateLinux(hooksToBeActive & (HookType.Keyboard | HookType.Mouse), changeIsTemporary);
		}

		private void ChangeHookStateLinux(HookType req, bool changeIsTemporary)
		{
			if (HookDisabled)
			{
				lock (hookStateLock)
				{
					keyboardEnabled = false;
					mouseEnabled = false;
					kbdHook = 0;
					mouseHook = 0;

					// permanent stop if hook is disabled via env var
					StopGlobalHookCore(dispose: true);
				}

				Ks.OutputDebugLine("Linux hook disabled via KEYSHARP_DISABLE_HOOK=1.");
				return;
			}

			bool wantKeyboard = (req & HookType.Keyboard) != 0;
			bool wantMouse    = (req & HookType.Mouse) != 0;

			lock (hookStateLock)
			{
				// Remember previous "caller wants it" state (since these bools are now dual-purpose).
				bool hadKeyboard = keyboardEnabled;
				bool hadMouse    = mouseEnabled;

				// ---- transition gate OFF (prevents racing init / mid-transition events) ----


				// If nothing requested:
				if (!wantKeyboard && !wantMouse)
				{
					keyboardEnabled = false; kbdHook = 0;
					mouseEnabled = false; mouseHook = 0;
					
					if (changeIsTemporary)
					{
						// Keep SimpleGlobalHook running, just stop processing events.
						// (Do NOT reset state here; we’ll resync on re-enable.)
						return;
					}

					// Permanent removal: stop + clear.
					StopGlobalHookCore(dispose: true);
					return;
				}

				// Ensure the hook is running (only start/attach once).
				if (globalHook == null || !globalHook.IsRunning)
				{
					StopGlobalHookCore(dispose: true); // clean restart if something half-exists

					globalHook = new SimpleGlobalHook();

					globalHook.KeyPressed += OnKeyPressed;
					globalHook.KeyReleased += OnKeyReleased;

					globalHook.MousePressed += OnMousePressed;
					globalHook.MouseReleased += OnMouseReleased;
					globalHook.MouseMoved += OnMouseMoved;
					globalHook.MouseWheel += OnMouseWheel;

					// Start hook thread
					hookRunTask = globalHook.RunAsync();
				}

				// Respect Windows semantics: ResetHook only for non-temporary transitions
				// and only when something is newly enabled.
				if (!changeIsTemporary)
				{
					if (wantKeyboard && !hadKeyboard)
					{
						keyboardEnabled = false; kbdHook = 0;
						// Always resync snapshots right before enabling processing.
						// This prevents “stuck key”/wrong modifier state after temporary disable,
						// and also ensures no stale state after restart.
						InitSnapshotFromX11();
						ResetHook(false, HookType.Keyboard, true);
					}
					if (wantMouse && !hadMouse)
					{
						mouseEnabled = false; mouseHook = 0;
						ResetHook(false, HookType.Mouse, true);
					}
				}

				// ---- transition complete: enable processing AND indicate desired state ----
				keyboardEnabled = wantKeyboard;
				mouseEnabled = wantMouse;

				kbdHook = wantKeyboard ? 1 : 0;   // sentinel for HasKbdHook()
				mouseHook = wantMouse ? 1 : 0;
			}
		}

		private void StopGlobalHookCore(bool dispose)
		{
			// Must be called under hookStateLock.

			if (dispose)
			{
				try { globalHook?.Dispose(); } catch { }
				try { if (hookRunTask != null && !hookRunTask.IsCompleted) hookRunTask.Wait(50); } catch { }
				globalHook = null;
				hookRunTask = null;

				// When permanently disposing, clear state like before.
				System.Array.Clear(physicalKeyState, 0, physicalKeyState.Length);
				System.Array.Clear(logicalKeyState, 0, logicalKeyState.Length);
				kbdMsSender.modifiersLRLogical = 0;
				kbdMsSender.modifiersLRLogicalNonIgnored = 0;
				kbdMsSender.modifiersLRPhysical = 0;
				ResetIndicatorSnapshot();
			}
			// If dispose==false: we intentionally keep the hook alive and keep current
			// state around; the processing gate is controlled by keyboardEnabled/mouseEnabled.
		}


		internal override void ChangeHookState(List<HotkeyDefinition> hk, HookType whichHook, HookType whichHookAlways)
		{
			base.ChangeHookState(hk, whichHook, whichHookAlways);

			// Rebuild minimal hotkey matcher.
			lock (hkLock)
			{
				linuxHotkeys.Clear();
				customPrefixSuppress.Clear();
				dynamicPrefixGrabs.Clear();

				if (hk != null)
				{
					foreach (var def in hk)
					{
						var entry = new LinuxHotkey
						{
							IdWithFlags = def.keyUp ? (def.id | HotkeyDefinition.HOTKEY_KEY_UP) : def.id,
							Vk = def.vk,
							ModifiersLR = def.modifiersConsolidatedLR,
							ModifierVK = def.modifierVK,
							KeyUp = def.keyUp,
							PassThrough = (def.noSuppress & HotkeyDefinition.AT_LEAST_ONE_VARIANT_HAS_TILDE) != 0,
							AllowExtra = def.allowExtraModifiers
						};
						linuxHotkeys.Add(entry);

						if (def.modifierVK != 0)
						{
							// By default, a custom prefix is suppressed unless the tilde prefix was used.
							var suppressPrefix = (def.noSuppress & HotkeyDefinition.NO_SUPPRESS_PREFIX) == 0;

							if (customPrefixSuppress.TryGetValue(def.modifierVK, out var suppressExisting))
								customPrefixSuppress[def.modifierVK] = suppressExisting || suppressPrefix;
							else
								customPrefixSuppress[def.modifierVK] = suppressPrefix;
						}
					}
				}

				RebuildPlatformHotkeyGrabs();
			}
		}

		protected virtual void RebuildPlatformHotkeyGrabs()
		{
		}

		internal override void Unhook() => DeregisterHooks();
		internal override void Unhook(nint hook) => DeregisterHooks();

		private void InitSnapshotFromX11()
		{
			ResetIndicatorSnapshot();
			InitSnapshotFromPlatform();
		}

		protected virtual void InitSnapshotFromPlatform()
		{
			System.Array.Clear(physicalKeyState, 0, physicalKeyState.Length);
			System.Array.Clear(logicalKeyState, 0, logicalKeyState.Length);
			kbdMsSender.modifiersLRPhysical = 0;
			kbdMsSender.modifiersLRLogical = 0;
			kbdMsSender.modifiersLRLogicalNonIgnored = 0;
		}

		private void StopGlobalHook()
		{
			try { globalHook?.Dispose(); } catch { }
			try { if (hookRunTask != null && !hookRunTask.IsCompleted) hookRunTask.Wait(50); } catch { }
			globalHook = null;
			hookRunTask = null;

			System.Array.Clear(physicalKeyState, 0, physicalKeyState.Length);
			System.Array.Clear(logicalKeyState, 0, logicalKeyState.Length);
			kbdMsSender.modifiersLRLogical = 0;
			kbdMsSender.modifiersLRLogicalNonIgnored = 0;
			kbdMsSender.modifiersLRPhysical = 0;
			kbdHook = 0;
			mouseHook = 0;
			ResetIndicatorSnapshot();
		}

		private static bool ShouldDisableHook()
		{
			var env = Environment.GetEnvironmentVariable("KEYSHARP_DISABLE_HOOK");
			return !string.IsNullOrEmpty(env) &&
				   (env.Equals("1") || env.Equals("true", StringComparison.OrdinalIgnoreCase) || env.Equals("yes", StringComparison.OrdinalIgnoreCase));
		}

		// -------------------- event handlers --------------------

		private void OnKeyPressed(object sender, KeyboardHookEventArgs e)
		{
			if (!keyboardEnabled) return;
			UpdateIndicatorSnapshotFromMask(e.RawEvent.Mask);

			var keyCode = e.Data.KeyCode;
			var vk = KeyCodeToVk(keyCode);
			if (vk == 0) return;

			lastHookEventWasKeyboard = true;
			lastKeyboardEventVk = vk;
			var sc = (uint)e.RawEvent.Keyboard.RawCode;
			var wasGrabbed = WasKeyGrabbed(e, vk, out var grabbedByHotstring);
			var isInjected = MarkSimulatedIfNeeded(e, vk, keyCode, false, out ulong extraInfo);

			// Track logical state as seen by apps.
			UpdateLogicalKeyFromHook(vk, keyUp: false, wasGrabbed);

			if (!isInjected && grabbedByHotstring)
				ForceReleaseEndKeyX11(vk);

			DebugLog($"[Hook] KeyDown vk={vk} sc={sc} grabbed={wasGrabbed} hsGrab={grabbedByHotstring} simulated={isInjected} extraInfo={extraInfo} time={DateTime.Now.ToString("hh.mm.ss.ffffff")}");

			var result = LowLevelCommon(e, vk, sc, sc, keyUp: false, extraInfo, (uint)(isInjected ? 0x10 : 0));

			if (result == 0 && !isInjected && wasGrabbed && !grabbedByHotstring)
				ReplayGrabbedKey(keyCode, vk, sc, false);

			if (result != 0)
				e.SuppressEvent = true;
		}

		private void OnKeyReleased(object sender, KeyboardHookEventArgs e)
		{
			if (!keyboardEnabled) return;
			UpdateIndicatorSnapshotFromMask(e.RawEvent.Mask);

			var keyCode = e.Data.KeyCode;
			var vk = KeyCodeToVk(keyCode);
			if (vk == 0) return;

			lastHookEventWasKeyboard = true;
			lastKeyboardEventVk = vk;
			var sc = (uint)e.RawEvent.Keyboard.RawCode;
			var wasGrabbed = WasKeyGrabbed(e, vk, out var grabbedByHotstring);
			var isInjected = MarkSimulatedIfNeeded(e, vk, keyCode, true, out ulong extraInfo);

			UpdateLogicalKeyFromHook(vk, keyUp: true, wasGrabbed);

			DebugLog($"[Hook] KeyUp vk={vk} sc={sc} grabbed={wasGrabbed} hsGrab={grabbedByHotstring} simulated={isInjected}");

			var result = LowLevelCommon(e, vk, sc, sc, keyUp: true, extraInfo, (uint)(isInjected ? 0x10 : 0));

			if (result == 0 && !isInjected && wasGrabbed && !grabbedByHotstring)
				ReplayGrabbedKey(keyCode, vk, sc, true);

			if (result != 0)
				e.SuppressEvent = true;
		}

		// On Linux we derive typed characters during EarlyCollectInput; no KeyTyped handler needed.

		private static readonly uint[] LMods = { VK_LSHIFT, VK_LCONTROL, VK_LMENU, VK_LWIN };
		private static readonly uint[] RMods = { VK_RSHIFT, VK_RCONTROL, VK_RMENU, VK_RWIN };
		internal uint CurrentModifiersLR()
		{
			// Prefer logical (non-ignored) state from the sender when the hook is active
			// and no send is in progress. During Send, the sender may temporarily drop
			// modifiers to emit shifted characters; we should report what is physically
			// held instead so that hotkey suffix checks (e.g. +h) keep seeing Shift.
			if (HasKbdHook())
			{
				if (!sendInProgress)
					return kbdMsSender.modifiersLRLogicalNonIgnored;
				// send in progress: fall through to physical snapshot
			}

			// With no hook, fall back to logical query via X11.
			if (TryQueryModifierLRState(out var logicalMods))
				return logicalMods;

			// Last resort: use physical snapshot.
			uint mods = 0;
			if (VK_LSHIFT < physicalKeyState.Length && (physicalKeyState[VK_LSHIFT] & StateDown) != 0) mods |= MOD_LSHIFT;
			if (VK_RSHIFT < physicalKeyState.Length && (physicalKeyState[VK_RSHIFT] & StateDown) != 0) mods |= MOD_RSHIFT;
			if (VK_LCONTROL < physicalKeyState.Length && (physicalKeyState[VK_LCONTROL] & StateDown) != 0) mods |= MOD_LCONTROL;
			if (VK_RCONTROL < physicalKeyState.Length && (physicalKeyState[VK_RCONTROL] & StateDown) != 0) mods |= MOD_RCONTROL;
			if (VK_LMENU < physicalKeyState.Length && (physicalKeyState[VK_LMENU] & StateDown) != 0) mods |= MOD_LALT;
			if (VK_RMENU < physicalKeyState.Length && (physicalKeyState[VK_RMENU] & StateDown) != 0) mods |= MOD_RALT;
			if (VK_LWIN < physicalKeyState.Length && (physicalKeyState[VK_LWIN] & StateDown) != 0) mods |= MOD_LWIN;
			if (VK_RWIN < physicalKeyState.Length && (physicalKeyState[VK_RWIN] & StateDown) != 0) mods |= MOD_RWIN;
			return mods;
		}

		internal bool TryQueryModifierLRState(out uint mods, byte[] keymapBuffer = null)
		{
			return TryQueryModifierLRStatePlatform(out mods, keymapBuffer);
		}

		protected virtual bool TryQueryModifierLRStatePlatform(out uint mods, byte[] keymapBuffer = null)
		{
			mods = 0u;
			return false;
		}

		private bool TryQueryKeyState(uint vk, out bool isDown)
		{
			return TryQueryKeyStatePlatform(vk, out isDown);
		}

		protected virtual bool TryQueryKeyStatePlatform(uint vk, out bool isDown)
		{
			isDown = false;
			return false;
		}

		private static uint VkToModMask(uint vk) => vk switch
		{
			VK_LSHIFT => MOD_LSHIFT,
			VK_RSHIFT => MOD_RSHIFT,
			VK_LCONTROL => MOD_LCONTROL,
			VK_RCONTROL => MOD_RCONTROL,
			VK_LMENU => MOD_LALT,
			VK_RMENU => MOD_RALT,
			VK_LWIN => MOD_LWIN,
			VK_RWIN => MOD_RWIN,
			_ => 0u
		};

		// Gets the logical state of all keys as a byte array (like GetKeyboardState on Windows).
		internal bool TryGetKeyboardState(out byte[] state)
		{
			state = new byte[VK_ARRAY_COUNT];
			var success = false;

			if (HasKbdHook())
			{
				System.Buffer.BlockCopy(logicalKeyState, 0, state, 0, Math.Min(logicalKeyState.Length, state.Length));
				success = true;
			}
			else if (TryQueryModifierLRState(out var mods))
			{
				if ((mods & MOD_LSHIFT) != 0 && VK_LSHIFT < state.Length) state[VK_LSHIFT] = StateDown;
				if ((mods & MOD_RSHIFT) != 0 && VK_RSHIFT < state.Length) state[VK_RSHIFT] = StateDown;
				if ((mods & MOD_LCONTROL) != 0 && VK_LCONTROL < state.Length) state[VK_LCONTROL] = StateDown;
				if ((mods & MOD_RCONTROL) != 0 && VK_RCONTROL < state.Length) state[VK_RCONTROL] = StateDown;
				if ((mods & MOD_LALT) != 0 && VK_LMENU < state.Length) state[VK_LMENU] = StateDown;
				if ((mods & MOD_RALT) != 0 && VK_RMENU < state.Length) state[VK_RMENU] = StateDown;
				if ((mods & MOD_LWIN) != 0 && VK_LWIN < state.Length) state[VK_LWIN] = StateDown;
				if ((mods & MOD_RWIN) != 0 && VK_RWIN < state.Length) state[VK_RWIN] = StateDown;
				success = true;
			}

			return success;
		}

		private void OnMouseWheel(object sender, MouseWheelHookEventArgs e)
		{
			if (!mouseEnabled) return;
			UpdateIndicatorSnapshotFromMask(e.RawEvent.Mask);

			lastHookEventWasKeyboard = false;
			var vk = MapWheelVk(e);
			var sc = (uint)e.Data.Rotation;
			var isInjected = MarkSimulatedIfNeeded(e, vk, KeyCode.VcUndefined, false, out ulong extraInfo);

			var result = LowLevelCommon(e, vk, sc, sc, keyUp: false, extraInfo, 0);
			if (result != 0)
				e.SuppressEvent = true;
		}

		private void OnMouseMoved(object sender, MouseHookEventArgs e)
		{
			if (!mouseEnabled) return;
			UpdateIndicatorSnapshotFromMask(e.RawEvent.Mask);

			lastHookEventWasKeyboard = false;
			if (!e.IsEventSimulated)
			{
				var script = Script.TheScript;
				script.timeLastInputPhysical = script.timeLastInputMouse = DateTime.UtcNow;

				if (script.KeyboardData.blockMouseMove)
					e.SuppressEvent = true;
			}
		}

		private void OnMousePressed(object sender, MouseHookEventArgs e)
		{
			if (!mouseEnabled) return;
			UpdateIndicatorSnapshotFromMask(e.RawEvent.Mask);

			lastHookEventWasKeyboard = false;
			var vk = MapMouseVk(e.Data.Button);
			var sc = 0u;
			var isInjected = MarkSimulatedIfNeeded(e, vk, KeyCode.VcUndefined, false, out ulong extraInfo);

			var result = LowLevelCommon(e, vk, sc, sc, keyUp: false, extraInfo, 0);
			if (result != 0)
				e.SuppressEvent = true;
		}

		private void OnMouseReleased(object sender, MouseHookEventArgs e)
		{
			if (!mouseEnabled) return;
			UpdateIndicatorSnapshotFromMask(e.RawEvent.Mask);

			lastHookEventWasKeyboard = false;
			var vk = MapMouseVk(e.Data.Button);
			var sc = 0u;
			var isInjected = MarkSimulatedIfNeeded(e, vk, KeyCode.VcUndefined, true, out ulong extraInfo);

			var result = LowLevelCommon(e, vk, sc, sc, keyUp: true, extraInfo, 0);
			if (result != 0)
				e.SuppressEvent = true;
		}

		private static uint MapMouseVk(MouseButton button) => button switch
		{
			MouseButton.Button1 => VK_LBUTTON,
			MouseButton.Button2 => VK_RBUTTON,
			MouseButton.Button3 => VK_MBUTTON,
			MouseButton.Button4 => VK_XBUTTON1,
			MouseButton.Button5 => VK_XBUTTON2,
			_ => 0u
		};

		private static uint MapWheelVk(MouseWheelHookEventArgs e)
		{
			if (e.Data.Direction == MouseWheelScrollDirection.Vertical)
				return e.Data.Rotation < 0 ? VK_WHEEL_UP : VK_WHEEL_DOWN;
			return e.Data.Rotation < 0 ? VK_WHEEL_LEFT : VK_WHEEL_RIGHT;
		}

		private static readonly FieldInfo rawEventField = typeof(HookEventArgs).GetField("<RawEvent>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);

		protected static void SetRawEvent(HookEventArgs e, UioHookEvent raw)
		{
			rawEventField?.SetValue(e, raw);
		}

		internal void RegisterSyntheticEvent(KeyCode keyCode, bool keyUp, DateTime ms, long extraInfo)
		{
			if (keyCode == KeyCode.VcUndefined)
				return;

			lock (pendingSynthetic)
			{
				pendingSynthetic.Add(new SyntheticToken(keyCode, keyUp, ms, extraInfo));
				DebugLog($"[Hook] Registered synthetic [{keyCode}] {(keyUp ? "up" : "down")} at {ms.Ticks} with extraInfo={extraInfo}");
			}
		}

		private bool PeekSyntheticEvent(KeyCode keyCode, bool keyUp, DateTimeOffset ms)
		{
			if (keyCode == KeyCode.VcUndefined)
				return false;

			lock (pendingSynthetic)
			{
				int count = pendingSynthetic.Count;
				for (int i = 0; i < count; i++)
				{
					var s = pendingSynthetic[i];
					var diffMs = (ms - s.EnqueueTime).TotalMilliseconds;
					if (diffMs > SyntheticEventTimeoutMs) // Enqueued >200ms in past, remove stale event
					{
						pendingSynthetic.RemoveAt(i);
						i--; count--;
					}
					else if (diffMs < 0) // Enqueued in future compared to hook event
					{
						break;
					}
					else
					{
						if (s.KeyCode == keyCode && s.KeyUp == keyUp)
							return true;
					}
				}
			}

			return false;
		}

		private bool ConsumeSyntheticEvent(KeyCode keyCode, bool keyUp, DateTimeOffset ms, out ulong extraInfo)
		{
			extraInfo = 0;
			if (keyCode == KeyCode.VcUndefined)
				return false;

			lock (pendingSynthetic)
			{
				int count = pendingSynthetic.Count;
				for (int i = 0; i < count; i++)
				{
					var s = pendingSynthetic[i];
					var diffMs = (ms - s.EnqueueTime).TotalMilliseconds;
					if (diffMs > SyntheticEventTimeoutMs) // Enqueued >200ms in past, remove stale event
					{
						pendingSynthetic.RemoveAt(i);
						i--; count--;
					}
					else if (diffMs < 0) // Enqueued in future compared to hook event
					{
						break;
					}
					else
					{
						if (s.KeyCode == keyCode && s.KeyUp == keyUp) 
						{
							extraInfo = (ulong)s.ExtraInfo;
							pendingSynthetic.RemoveAt(i);
							DebugLog($"[Hook] Removed synthetic [{keyCode}] {(keyUp ? "up" : "down")} at {ms.Ticks} with extraInfo={s.ExtraInfo}");
							return true;
						}
					}
				}
			}

			return false;
		}

		private void SuppressHotkeyRelease(uint vk)
		{
			if (vk == 0) return;
			lock (suppressedHotkeyReleases)
			{
				suppressedHotkeyReleases.TryGetValue(vk, out var curr);
				var next = curr + 1;
				suppressedHotkeyReleases[vk] = next;
				DebugLog($"[Hook] Suppress suffix release vk={vk} count={next}");
			}
		}

		private bool ShouldSuppressSuffixRelease(HotkeyVariant variant, uint vk, uint hotkeyIdWithFlags)
		{
			if (vk == 0) return false;

			if (variant != null && (variant.noSuppress & HotkeyDefinition.AT_LEAST_ONE_VARIANT_HAS_TILDE) != 0)
				return false;

			if (HasKeyUpHotkey(vk))
				return false;

			return activeHotkeyDown && activeHotkeyVk == vk;
		}

		private bool MarkSimulatedIfNeeded(HookEventArgs e, uint vk, KeyCode keyCode, bool keyUp, out ulong extraInfo)
		{
			var mask = e.RawEvent.Mask;
			var simulated = (mask & EventMask.SimulatedEvent) != 0;
			extraInfo = 0;

			if (!simulated && (ConsumeSyntheticEvent(keyCode, keyUp, e.EventTime, out extraInfo)
				 //|| IsLogicallyDownPhysicallyUp(vk)
				 ))
			{
				var raw = e.RawEvent;
				raw.Mask |= EventMask.SimulatedEvent;
				SetRawEvent(e, raw);
				simulated = true;
			}

			if (simulated && extraInfo == 0)
        		extraInfo = (ulong)KeyboardMouseSender.KeyIgnoreAllExceptModifier;

			return simulated;
		}

		private ulong ComputeExtraInfo(bool isSimulated)
		{
			if (sendInProgress || isSimulated)
				return (ulong)KeyboardMouseSender.KeyIgnoreAllExceptModifier;

			return 0;
		}

		private bool WasKeyGrabbed(HookEventArgs e, uint vk, out bool grabbedByHotstring)
		{
			return WasKeyGrabbedPlatform(e, vk, out grabbedByHotstring);
		}

		protected virtual bool WasKeyGrabbedPlatform(HookEventArgs e, uint vk, out bool grabbedByHotstring)
		{
			grabbedByHotstring = false;
			return false;
		}

		internal virtual bool HasButtonGrab(uint button) => false;

		internal virtual uint MouseButtonToXButton(MouseButton btn) => 0u;

		protected virtual void PlatformUngrabAll()
		{
		}

		private void ReplayGrabbedKey(KeyCode kc, uint vk, uint sc, bool keyUp)
		{
			if (vk == 0) return;
			kbdMsSender.SendKeyEvent(keyUp ? KeyEventTypes.KeyUp : KeyEventTypes.KeyDown, vk, sc, default, false, KeyboardMouseSender.KeyIgnore);
			if (vk < logicalKeyState.Length)
				logicalKeyState[vk] = (byte)(keyUp ? 0 : StateDown);
		}

		// Temporarily release all grabs (keyboard + passive keys) during a send, then re-apply them.
		internal virtual GrabSnapshot BeginSendUngrab(HashSet<uint> keycodes = null, HashSet<uint> buttons = null)
			=> default;

		internal virtual void EndSendUngrab(GrabSnapshot snap)
		{
		}

		internal virtual uint TemporarilyUngrabKey(uint vk) => 0;

		internal virtual void RegrabKey(uint targetXcode)
		{
		}

		/*
		internal void WaitForPendingSyntheticDrain(int timeoutMs)
		{
			var sw = Stopwatch.StartNew();
			while (sw.ElapsedMilliseconds < timeoutMs)
			{
				lock (pendingSynthetic)
				{
					bool any = false;
					foreach (var kv in pendingSynthetic)
					{
						if (kv.Value.Down != 0 || kv.Value.Up != 0)
						{
							any = true;
							break;
						}
					}
					if (!any)
						return;
				}
				Thread.Sleep(1);
			}
		}
		*/

		internal void DisarmHotstring()
		{
			if (!hsArmed)
				return;

			lock (hkLock)
			{
				DebugLog("Disarming hotstring");
				DisarmHotstringPlatform();

				hsActiveGrabVariants.Clear();
				activeHotstringGrabs.Clear();
				hsArmedEnds.Clear();
				hsArmed = false;
			}
		}

		protected virtual void DisarmHotstringPlatform()
		{
		}

		// Arm a set of end characters (all that would complete a match NOW)
		private void ArmHotstringForEnds(HashSet<char> endsToArm)
		{
			DisarmHotstring();
			if (endsToArm.Count == 0)
				return;

			lock (hkLock)
			{
				hsArmed = ArmHotstringForEndsPlatform(endsToArm);
			}
		}

		protected virtual bool ArmHotstringForEndsPlatform(HashSet<char> endsToArm) => false;

		// After we mutate the hs buffer, call this to (re)compute arming
		private void RecomputeHotstringArming()
		{
			var script = Script.TheScript;
			var hm = script.HotstringManager;

			// 1) If a full match exists already (e.g., non-terminating HS), trigger immediately.
			var ready = hm.MatchHotstring();
			if (ready != null)
			{
				char? evtChar = ' ';
				if (!KeyboardMouseSender.HotInputLevelAllowsFiring(ready.inputLevel, lastTypedExtraInfo, ref evtChar))
				{
					LogHotstringBuffer($"match gated by inputlevel={ready.inputLevel} lastExtra={lastTypedExtraInfo}");
					DisarmHotstring();
					return;
				}

				// Determine case + end char like Windows (same code you already use below)
				var caseMode = ComputeCaseMode(ready, hm.hsBuf);

				char endChar = '\0';
				var sspan = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(hm.hsBuf);
				if (ready.endCharRequired)
				{
					if (sspan.Length > 0)
						endChar = sspan[^1];
					else
						endChar = lastTypedChar;
				}

				_ = channel.Writer.TryWrite(new KeysharpMsg
				{
					message = (uint)UserMessages.AHK_HOTSTRING,
					obj = new HotstringMsg { hs = ready, caseMode = caseMode, endChar = endChar }
				});
				LogHotstringBuffer($"hotstring fired '{ready.Name}' end='{(endChar == '\0' ? "\\0" : endChar.ToString())}'");
				ClearHotstringBuffer("fired");

				DisarmHotstring();
				return;
			}

			// 2) Predict: append each possible end-char; if MatchHotstring() would succeed, arm that end-char.
			var ends = new HashSet<char>();

			// Prefer manager’s configured defaults; fall back to a sensible superset if needed.
			var def = hm.defEndChars ?? string.Empty;
			foreach (var c in def)
			{
				hm.hsBuf.Add(c);
				var m = hm.MatchHotstring();
				hm.hsBuf.RemoveAt(hm.hsBuf.Count - 1);
				if (m != null)
					ends.Add(c);
			}

			// If manager had no defaults or nothing matched, you can optionally include extra common terminators:
			// foreach (var c in " \t\r\n.,;:!?-") { ... }  // (uncomment if you want a bit more aggressive prediction)

			// Arm (or disarm) accordingly
			if (ends.Count > 0) ArmHotstringForEnds(ends);
			else DisarmHotstring();
		}

		internal virtual void ForceReleaseEndKeyX11(uint vk)
		{
		}

		internal readonly struct ReleasedKey
		{
			public readonly uint Vk;
			public ReleasedKey(uint vk) { Vk = vk; }
		}

		internal virtual List<ReleasedKey> ForceReleaseKeysForSend(HashSet<uint> vks)
			=> [];

		/// <summary>
		/// Re-press keys that we previously force-released, but only if they are STILL physically held.
		/// This keeps our logical snapshot aligned with the user still holding the key.
		/// </summary>
		internal virtual void RestoreNonModifierKeysAfterSend(List<ReleasedKey> released)
		{
		}

		private void UpdateLogicalKeyFromHook(uint vk, bool keyUp, bool wasGrabbed)
		{
			if (vk == 0 || vk >= logicalKeyState.Length)
				return;

			if (!wasGrabbed)
				logicalKeyState[vk] = (byte)(keyUp ? 0 : StateDown);
		}

		// Map a candidate end character to VK + required modifiers (layout-aware)
		protected bool MapEndCharToVkAndNeeds(char endChar, out uint vk, out bool needShift, out bool needAltGr)
		{
			vk = 0; needShift = needAltGr = false;

			switch (endChar)
			{
				case ' ': vk = VK_SPACE; return true;
				case '\t': vk = VK_TAB; return true;
				case '\r':
				case '\n': vk = VK_RETURN; return true;
			}

			// Use the same mapper you use for Send (handles punctuation layout properly)
			if (System.Text.Rune.TryGetRuneAt(endChar.ToString(), 0, out var rune)
				&& UnixKeyboardMouseSender.UnixCharMapper.TryMapRuneToKeystroke(rune, out var mappedVk, out var s, out var g))
			{
				vk = mappedVk; needShift = s; needAltGr = g;
				return vk != 0;
			}
			return false;
		}


		private IndicatorSnapshot RefreshIndicatorSnapshot()
		{
			if (TryGetIndicatorStates(out var capsOn, out var numOn, out var scrollOn))
			{
				indicatorSnapshot = new IndicatorSnapshot(capsOn, numOn, scrollOn);
				return indicatorSnapshot;
			}

			return indicatorSnapshot;
		}

		// -------------------- basic matcher → AHK_HOOK_HOTKEY --------------------
		private static short PhysicalInputLevel => (short)(Keysharp.Core.Common.Keyboard.KeyboardMouseSender.SendLevelMax + 1);

		internal bool HasKeyUpHotkey(uint vk)
		{
			lock (hkLock)
			{
				foreach (var hk in linuxHotkeys)
				{
					if (hk.Vk == vk && hk.KeyUp)
						return true;
				}
			}
			return false;
		}

		internal void ResetActiveHotkeyState()
		{
			activeHotkeyDown = false;
			activeHotkeyVk = 0;
			activeHotkeyKc = KeyCode.VcUndefined;
			lock (suppressedHotkeyReleases)
				suppressedHotkeyReleases.Clear();
		}

		private bool IsLogicallyDownPhysicallyUp(uint vk)
		{
			if (vk == 0) return false;

			if (vk >= physicalKeyState.Length || (physicalKeyState[vk] & StateDown) != 0)
				return false;

			if (vk < logicalKeyState.Length && (logicalKeyState[vk] & StateDown) != 0)
				return true;

			return false;
		}

		private void PostHotkey(uint hotkeyId, uint sc /* typically 0 on Linux */, long eventLevel)
		{
			// Mirror Windows: pack SC (low word) + event input level (high word), variant omitted.
			_ = channel.Writer.TryWrite(new KeysharpMsg
			{
				message = (uint)UserMessages.AHK_HOOK_HOTKEY,
				wParam = new nint(hotkeyId),
				lParam = new nint(KeyboardUtils.MakeLong((short)sc, (short)eventLevel)),
				obj = null // <— Do NOT pass variant; UI thread recomputes it.
			});
		}

		private uint GetCurrentModifiersLRExcludingSuffix(uint suffixVk)
		{
			uint mods = 0;

			// Consider *physical* state at the time of event.
			for (uint vk = 0; vk < VK_ARRAY_COUNT; vk++)
			{
				if (vk == suffixVk) continue; // a suffix doesn't modify itself (Windows does similar) :contentReference[oaicite:17]{index=17}
				if (vk >= physicalKeyState.Length) break;
				if ((physicalKeyState[vk] & StateDown) == 0) continue;

				switch (vk)
				{
					case VK_LSHIFT: mods |= MOD_LSHIFT; break;
					case VK_RSHIFT: mods |= MOD_RSHIFT; break;
					case VK_LCONTROL: mods |= MOD_LCONTROL; break;
					case VK_RCONTROL: mods |= MOD_RCONTROL; break;
					case VK_LMENU: mods |= MOD_LALT; break;
					case VK_RMENU: mods |= MOD_RALT; break;
					case VK_LWIN: mods |= MOD_LWIN; break;
					case VK_RWIN: mods |= MOD_RWIN; break;
				}
			}
			return mods;
		}

		// -------------------- utilities & abstract impls --------------------

		/// <summary>
		/// Determine whether the given virtual key is currently logically down.
		/// </summary>
		/// <param name="vk"></param>
		internal override bool IsKeyDown(uint vk)
		{
			if (HasKbdHook())
			{
				if (vk < logicalKeyState.Length)
					return (logicalKeyState[vk] & StateDown) != 0;
			}

			// Fallback: query X11 logical state for any key.
			if (TryQueryKeyState(vk, out var isDown))
				return isDown;

			return false;
		}
		internal override bool IsKeyDownAsync(uint vk) => IsKeyDown(vk);
		internal override bool IsKeyToggledOn(uint vk)
		{
			if (TryGetIndicatorStates(out var capsOn, out var numOn, out var scrollOn))
			{
				return vk switch
				{
					VK_CAPITAL => capsOn,
					VK_NUMLOCK => numOn,
					VK_SCROLL => scrollOn,
					_ => false
				};
			}

			return false;
		}

		// Route hotstring collection through the Linux arming logic so end-keys can be grabbed/ungrabbed.
		internal override bool CollectHotstring(ulong extraInfo, char[] ch, int charCount, nint activeWindow,
												KeyHistoryItem keyHistoryCurr, ref HotstringDefinition hsOut, ref CaseConformModes caseConformMode, ref char endChar)
		{
			if (charCount <= 0 || ch.Length == 0)
				return true;

			if (KeyboardMouseSender.IsIgnored(extraInfo))
				return true; // Ignore simulated/send input.

			var hm = Script.TheScript.HotstringManager;
			lastTypedExtraInfo = extraInfo;

			var result = base.CollectHotstring(extraInfo, ch, charCount, activeWindow, keyHistoryCurr, ref hsOut, ref caseConformMode, ref endChar);

			// Keep Linux-side logging and arming in sync with the buffer maintained by the base collector.
			if (charCount > 0)
			{
				var c = ch[0];
				lastTypedChar = c;
				var chDesc = c switch { '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", _ when char.IsControl(c) => "\\u" + ((int)c).ToString("X4"), _ => c.ToString() };

				var hmBuf = hm.hsBuf;
				var sb = new StringBuilder(hmBuf.Count * 2);
				static string Escape(char cc) => cc switch
				{
					'\n' => "\\n",
					'\r' => "\\r",
					'\t' => "\\t",
					_ when char.IsControl(cc) => "\\u" + ((int)cc).ToString("X4"),
					_ => cc.ToString()
				};
				foreach (var cc in hmBuf) sb.Append(Escape(cc));
				DebugLog($"[HS] typed '{chDesc}': len={hmBuf.Count} buf=\"{sb}\" armed={hsArmed}");

				RecomputeHotstringArming();
			}

			return result;
		}

		// Hotstrings on Linux rely on XGrabKey to swallow end characters. As soon as a hotstring
		// fires, release those grabs before the replacement is sent so the ending character can
		// be replayed by the sender.
		internal override void SendHotkeyMessages(bool keyUp, ulong extraInfo, KeyHistoryItem keyHistoryCurr, uint hotkeyIDToPost, HotkeyVariant variant, HotstringDefinition hs, CaseConformModes caseConformMode, char endChar)
		{
			if (hs != null)
				DisarmHotstring();

			if (hotkeyIDToPost != HotkeyDefinition.HOTKEY_ID_INVALID)
			{
				var vk = keyHistoryCurr.vk;
				if (vk == 0 && lastHookEventWasKeyboard)
					vk = lastKeyboardEventVk;
				if (vk != 0)
				{
					if (!keyUp && ShouldSuppressSuffixRelease(variant, vk, hotkeyIDToPost))
						SuppressHotkeyRelease(vk);
					activeHotkeyVk = vk;
					activeHotkeyDown = !keyUp;
				}
			}

			base.SendHotkeyMessages(keyUp, extraInfo, keyHistoryCurr, hotkeyIDToPost, variant, hs, caseConformMode, endChar);
		}

		internal override void PrepareToSendHotstringReplacement(char endChar)
		{
			DisarmHotstring();
		}

		internal override uint KeyToModifiersLR(uint vk, uint sc, ref bool? isNeutral)
		{
			if (vk == 0 && sc == 0)
				return 0;

			if (vk != 0)
			{
				switch (vk)
				{
					case VK_SHIFT:
					{
						if (sc != 0)
						{
							var mapped = MapScToVk(sc);
							if (mapped == VK_RSHIFT) return MOD_RSHIFT;
							if (mapped == VK_LSHIFT)
							{
								if (isNeutral != null) isNeutral = true;
								return MOD_LSHIFT;
							}
						}

						if (isNeutral != null) isNeutral = true;
						return MOD_LSHIFT;
					}
					case VK_LSHIFT: return MOD_LSHIFT;
					case VK_RSHIFT: return MOD_RSHIFT;

					case VK_CONTROL:
					{
						if (sc != 0)
						{
							var mapped = MapScToVk(sc);
							if (mapped == VK_RCONTROL) return MOD_RCONTROL;
							if (mapped == VK_LCONTROL)
							{
								if (isNeutral != null) isNeutral = true;
								return MOD_LCONTROL;
							}
						}

						if (isNeutral != null) isNeutral = true;
						return MOD_LCONTROL;
					}
					case VK_LCONTROL: return MOD_LCONTROL;
					case VK_RCONTROL: return MOD_RCONTROL;

					case VK_MENU:
					{
						if (sc != 0)
						{
							var mapped = MapScToVk(sc);
							if (mapped == VK_RMENU) return MOD_RALT;
							if (mapped == VK_LMENU)
							{
								if (isNeutral != null) isNeutral = true;
								return MOD_LALT;
							}
						}

						if (isNeutral != null) isNeutral = true;
						return MOD_LALT;
					}
					case VK_LMENU: return MOD_LALT;
					case VK_RMENU: return MOD_RALT;

					case VK_LWIN: return MOD_LWIN;
					case VK_RWIN: return MOD_RWIN;
					default:
						return 0;
				}
			}

			var mappedFromSc = MapScToVk(sc);
			return mappedFromSc switch
			{
				VK_LSHIFT => MOD_LSHIFT,
				VK_RSHIFT => MOD_RSHIFT,
				VK_LCONTROL => MOD_LCONTROL,
				VK_RCONTROL => MOD_RCONTROL,
				VK_LMENU => MOD_LALT,
				VK_RMENU => MOD_RALT,
				VK_LWIN => MOD_LWIN,
				VK_RWIN => MOD_RWIN,
				_ => 0
			};
		}

		internal override uint MapScToVk(uint sc)
		{
			if (sc == 0)
				return 0;

			return MapScToVkPlatform(sc);
		}

		internal override uint MapVkToSc(uint vk, bool returnSecondary = false)
		{
			if (vk == 0)
				return 0;

			return MapVkToScPlatform(vk, returnSecondary);
		}

		protected virtual uint MapScToVkPlatform(uint sc)
		{
			var kc = (KeyCode)sc;
			return SharpHookKeyMapper.KeyCodeToVk(kc);
		}

		protected virtual uint MapVkToScPlatform(uint vk, bool returnSecondary = false)
		{
			var kc = SharpHookKeyMapper.VkToKeyCode(vk);
			return kc == KeyCode.VcUndefined ? 0u : (uint)kc;
		}

		internal override bool EarlyCollectInput(ulong extraInfo, uint rawSC, uint vk, uint sc, bool keyUp, bool isIgnored
										, CollectInputState state, KeyHistoryItem keyHistoryCurr)
		// Returns true if the caller should treat the key as visible (non-suppressed).
		// Always use the parameter aVK rather than event.vkCode because the caller or caller's caller
		// might have adjusted aVK, such as to make it a left/right specific modifier key rather than a
		// neutral one. On the other hand, event.scanCode is the one we need for ToUnicodeEx() calls.
		{
			var script = Script.TheScript;
			state.earlyCollected = true;
			state.used_dead_key_non_destructively = false;
			state.charCount = 0;

			if (keyUp && !CollectKeyUp(extraInfo, vk, sc, true))
				return false;

			// The checks above suppress key-up if key-down was suppressed and the Input is still active.
			// Otherwise, avoid suppressing key-up since it may result in the key getting stuck down.
			// At the very least, this is needed for cases where a user presses a #z hotkey, for example,
			// to initiate an Input.  When the user releases the LWIN/RWIN key during the input, that
			// up-event should not be suppressed otherwise the modifier key would get "stuck down".
			if (keyUp)
				return true;

			var transcribeKey = true;

			// Don't unconditionally transcribe modified keys such as Ctrl-C because calling ToAsciiEx() on
			// some such keys (e.g. Ctrl-LeftArrow or RightArrow if I recall correctly), disrupts the native
			// function of those keys.  That is the reason for the existence of the
			// g_input.TranscribeModifiedKeys option.
			// Fix for v1.0.38: Below now uses kbdMsSender.modifiersLR_logical vs. g_modifiersLR_physical because
			// it's the logical state that determines what will actually be produced on the screen and
			// by ToAsciiEx() below.  This fixes the Input command to properly capture simulated
			// keystrokes even when they were sent via hotkey such #c or a hotstring for which the user
			// might still be holding down a modifier, such as :*:<t>::Test (if '>' requires shift key).
			// It might also fix other issues.
			if ((kbdMsSender.modifiersLRLogical & ~(MOD_LSHIFT | MOD_RSHIFT)) != 0 // At least one non-Shift modifier is down (Shift may also be down).
					&& !((kbdMsSender.modifiersLRLogical & (MOD_LALT | MOD_RALT)) != 0 && (kbdMsSender.modifiersLRLogical & (MOD_LCONTROL | MOD_RCONTROL)) != 0))
			{
				// Since in some keybd layouts, AltGr (Ctrl+Alt) will produce valid characters (such as the @ symbol,
				// which is Ctrl+Alt+Q in the German/IBM layout and Ctrl+Alt+2 in the Spanish layout), an attempt
				// will now be made to transcribe all of the following modifier combinations:
				// - Anything with no modifiers at all.
				// - Anything that uses ONLY the shift key.
				// - Anything with Ctrl+Alt together in it, including Ctrl+Alt+Shift, etc. -- but don't do
				//   "anything containing the Alt key" because that causes weird side-effects with
				//   Alt+LeftArrow/RightArrow and maybe other keys too).
				// Older comment: If any modifiers except SHIFT are physically down, don't transcribe the key since
				// most users wouldn't want that.  An additional benefit of this policy is that registered hotkeys will
				// normally be excluded from the input (except those rare ones that have only SHIFT as a modifier).
				// Note that ToAsciiEx() will translate ^i to a tab character, !i to plain i, and many other modified
				// letters as just the plain letter key, which we don't want.
				for (var input = script.input; ; input = input.prev)
				{
					if (input == null) // No inputs left, and none were found that meet the conditions below.
					{
						transcribeKey = false;
						break;
					}

					// Transcription is done only once for all layers, so do this if any layer requests it:
					if (input.transcribeModifiedKeys && input.InProgress() && input.IsInteresting(extraInfo))
						break;
				}
			}

			// v1.1.28.00: active_window is set to the focused control, if any, so that the hotstring buffer is reset
			// when the focus changes between controls, not just between windows.
			// v1.1.28.01: active_window is left as the active window; the above is not done because it disrupts
			// hotstrings when the first keypress causes a change in focus, such as to enter editing mode in Excel.
			// See Get_active_window_keybd_layout macro definition for related comments.
			var activeWindow = WindowManager.GetForegroundWindowHandle(); // Set default in case there's no focused control.
			var activeWindowKeybdLayout = GetKeyboardLayout(0);
			state.activeWindow = activeWindow;
			state.keyboardLayout = activeWindowKeybdLayout;

			// SUMMARY OF DEAD KEY ISSUE:
			// Calling ToUnicodeEx() with conventional parameters disrupts the entry of dead keys in two different ways:
			//  1) Passing a dead key buffers it within the keyboard layout's internal state.
			//  2) Passing a live key removes any pending dead key from the keyboard layout's internal state.
			// In either case, the state is then incorrect for the active window's own call to ToUnicodeEx(), so it ends
			// up with something like "e" or "''e" instead of "é".  Originally this was solved by reinserting the pending
			// dead key (first by re-sending the dead key, then later by calling ToUnicodeEx()), but now we use a special
			// combination of parameters to avoid changing the state where possible.

			int charCount;
			var ch = new char[8];

			if (vk == VK_PACKET)
			{
				// VK_PACKET corresponds to a SendInput event with the KEYEVENTF_UNICODE flag.
				charCount = 1;// SendInput only supports a single 16-bit character code.
				ch[0] = (char)rawSC; // No translation needed.
			}
			else if (transcribeKey && vk != VK_MENU)
			{
				var keyState = new byte[physicalKeyState.Length];
				bool interfere = pendingDeadKeys.Count > 0 && pendingDeadKeyInvisible;

				if (interfere)
				{
					// Either an invisible InputHook is in progress or there is a UWP app focused.  In either case, the dead key
					// was only recorded by us and wasn't retained by the keyboard layout's internal state, so we need to "replay"
					// the sequence to set things up for conversion of the new key.
					for (int i = 0; i < pendingDeadKeys.Count; ++i)
					{
						var dead_key = pendingDeadKeys[i];
						AdjustKeyState(keyState, dead_key.modLR);
						keyState[VK_CAPITAL] = (byte)dead_key.caps;
						System.Array.Clear(ch, 0, ch.Length);
						_ = ToUnicode(dead_key.vk, dead_key.sc, keyState, ch, 0, activeWindowKeybdLayout);
					}
				}

				// The documentation for ToAsciiEx is incomplete, but recent documentation for ToUnicodeEx shows the meaning of
				// the flags: 0x1 = Alt+Numpad key combinations are not handled (but the flag doesn't prevent Alt-up itself from
				// being processed), 0x2 = handle key break events (key-up).  We fake key-up to avoid changing the dead key state
				// (it stands to reason that key-down normally causes a change in state, so the corresponding key-up wouldn't).
				// We must avoid passing VK_MENU with KBDBREAK (0x8000) because that disrupts any ongoing Alt+Numpad entry.
				// Note that Windows 10 v1607 supports flag 0x4 to avoid changing the keyboard state, but there seems to be no
				// benefit; in particular, the Alt+Numpad state is still affected.
				// Credit to Ilya Zakharevich for pointing out this method @ https://stackoverflow.com/a/78173420/894589
				var flags = interfere ? 1u : 3u;
				var scanCode = rawSC | (interfere ? 0u : 0x8000u);
				// Provide the correct logical modifier and CapsLock state for any translation below.
				AdjustKeyState(keyState, kbdMsSender.modifiersLRLogical);
				keyState[VK_CAPITAL] = (byte)(IsKeyToggledOn(VK_CAPITAL) ? 1 : 0);
				System.Array.Clear(ch, 0, ch.Length);
				charCount = ToUnicode(vk, scanCode, keyState, ch, flags, activeWindowKeybdLayout);

				if (charCount == 0 && (kbdMsSender.modifiersLRLogical & (MOD_LALT | MOD_RALT)) != 0 && (kbdMsSender.modifiersLRLogical & (MOD_LCONTROL | MOD_RCONTROL)) == 0u && !interfere)
				{
					// Apparently, ToUnicodeEx ignores the Alt in Alt and Alt+Shift combinations only if the key-up bit is not set.
					// For consistency with prior versions (and Win, but not Ctrl/Shift), let the Alt state be ignored under these
					// conditions.  transcribe_key and modifier state checked above imply that the M option was used.
					keyState[VK_MENU] = 0;
					System.Array.Clear(ch, 0, ch.Length);
					charCount = ToUnicode(vk, scanCode, keyState, ch, flags, activeWindowKeybdLayout);
				}

				if (charCount <= 0 && interfere) // A key with no text translation, or possibly a chained dead key (if < 0).
				{
					// Flush the dead key which was buffered either by the ToUnicodeEx call above or the dead key loop further up.
					var ignored = new char[8];

					// Michael S. Kaplan blogged that he would explain in a later post why he used VK_SPACE to clear the buffer,
					// but then changed to using VK_DECIMAL and apparently never explained either choice.  Still, VK_DECIMAL
					// seems like a safe choice for clearing the state; probably any key which produces text will work, but
					// the loop is needed in case of an unconventional layout which makes VK_DECIMAL itself a dead key.
					while (ToUnicode(VK_DECIMAL, 0, keyState, ignored, flags, activeWindowKeybdLayout) == -1) ;
				}

				if (charCount > 0)
				{
					state.used_dead_key_non_destructively = pendingDeadKeys.Count > 0 && !interfere;
					pendingDeadKeys.Clear();
					pendingDeadKeyInvisible = false;
				}
				else if (charCount < 0 && pendingDeadKeys.Count < 3)
				{
					// Record this dead key so that we can reproduce the sequence when needed.
					var deadKey = new DeadKeyRecord()
					{
						vk = vk,
						sc = rawSC,
						modLR = kbdMsSender.modifiersLRLogical,
						caps = keyState[VK_CAPITAL]
					};
					pendingDeadKeys.Add(deadKey);
				}

				if ((kbdMsSender.modifiersLRLogical & (MOD_LCONTROL | MOD_RCONTROL)) == 0) // i.e. must not replace '\r' with '\n' if it is the result of Ctrl+M.
				{
					if (ch.Length > 0)
						if (ch[0] == '\r')  // Translate \r to \n since \n is more typical and useful in Windows.
							ch[0] = '\n';

					if (ch.Length > 1)
						if (ch[1] == '\r')  // But it's never referred to if byte_count < 2
							ch[1] = '\n';
				}
			}
			else
			{
				charCount = 0;
			}

			// Simulated Enter may not always produce text via ToUnicode on Linux, but hotstring
			// matching expects an end-character for Enter.
			if (charCount == 0 && vk == VK_RETURN)
			{
				charCount = 1;
				ch[0] = '\n';
			}

			// If Backspace is pressed after a dead key, ch[0] is the "dead" char and ch[1] is '\b'.
			if (vk == VK_BACK && charCount > 0)
				charCount--;// Remove '\b' to simplify the backspacing and collection stages.

			state.ch = ch;
			state.charCount = charCount;

			if (!CollectInputHook(extraInfo, vk, sc, ch, charCount, true))
				return false; // Suppress.

			return true;//Visible.
		}
										
		/*
		{
			state.earlyCollected = true;
			state.used_dead_key_non_destructively = false;
			state.charCount = 0;
			state.ch = System.Array.Empty<char>();
			state.activeWindow = IntPtr.Zero;
			state.keyboardLayout = IntPtr.Zero;

			if (keyUp)
				return CollectKeyUp(extraInfo, vk, sc, true);

			// Mirror Windows logic: only transcribe when no modifiers, shift-only, or AltGr (Ctrl+Alt).
			var mods = kbdMsSender.modifiersLRLogical;
			var nonShiftMods = mods & ~(MOD_LSHIFT | MOD_RSHIFT);
			var hasAltGr = (mods & (MOD_LCONTROL | MOD_RCONTROL)) != 0 && (mods & (MOD_LALT | MOD_RALT)) != 0;
			var transcribeKey = nonShiftMods == 0 || hasAltGr;

			if (!transcribeKey)
				return true;

			if (!IsX11Available || xDisplay == IntPtr.Zero)
				return true;

			// Translate keycode + modifiers to a character using the current layout.
			int keycode = (int)rawSC;
			bool shift = (mods & (MOD_LSHIFT | MOD_RSHIFT)) != 0;
			bool altgr = hasAltGr || (mods & MOD_RALT) != 0;
			int idx = (altgr ? 2 : 0) + (shift ? 1 : 0);

			uint ks = (uint)XKeycodeToKeysym(xDisplay, keycode, idx);
			if (ks == 0 && idx != 0)
				ks = (uint)XKeycodeToKeysym(xDisplay, keycode, 0); // fallback to base

			if (ks != 0 && KeysymToChar(ks, out var ch))
			{
				state.charCount = 1;
				state.ch = new[] { ch };
			}

			return true;
		}
		*/

		internal override uint CharToVKAndModifiers(char ch, ref uint? modifiersLr, nint keybdLayout, bool enableAZFallback = false)
		{
			// Delegate to the Linux char mapper used by the sender; add Shift/AltGr if needed.
			if (Rune.TryGetRuneAt(ch.ToString(), 0, out var rune)
				&& UnixKeyboardMouseSender.UnixCharMapper.TryMapRuneToKeystroke(rune, out var vk, out var needShift, out var needAltGr))
			{
				uint mods = modifiersLr ?? 0;
				if (needShift) mods |= MOD_LSHIFT;
				if (needAltGr) mods |= MOD_RALT;
				modifiersLr = mods;
				return vk;
			}
			return 0;
		}

		internal override bool SystemHasAnotherKeybdHook() => false;
		internal override bool SystemHasAnotherMouseHook() => false;

		internal virtual uint VkToXKeycode(uint vk) => 0;

		private void ClearHotstringBuffer(string reason)
		{
			var hm = Script.TheScript.HotstringManager;
			if (hm.hsBuf.Count == 0)
			{
				LogHotstringBuffer(reason);
				return;
			}

			hm.hsBuf.Clear();
			LogHotstringBuffer(reason);
		}

		private void LogHotstringBuffer(string reason)
		{
			var hm = Script.TheScript.HotstringManager;
			var sb = new StringBuilder(hm.hsBuf.Count * 2);

			static string EscapeChar(char c) => c switch
			{
				'\n' => "\\n",
				'\r' => "\\r",
				'\t' => "\\t",
				_ when char.IsControl(c) => "\\u" + ((int)c).ToString("X4"),
				_ => c.ToString()
			};

			foreach (var ch in hm.hsBuf)
				sb.Append(EscapeChar(ch));

			DebugLog($"[HS] {reason}: len={hm.hsBuf.Count} buf=\"{sb}\" armed={hsArmed}");
		}

		internal virtual bool TryGetIndicatorStates(out bool capsOn, out bool numOn, out bool scrollOn)
		{
			if (indicatorSnapshotValid)
			{
				capsOn = indicatorSnapshot.Caps;
				numOn = indicatorSnapshot.Num;
				scrollOn = indicatorSnapshot.Scroll;
				return true;
			}

			capsOn = false;
			numOn = false;
			scrollOn = false;
			return false;
		}

	}
}
#endif


