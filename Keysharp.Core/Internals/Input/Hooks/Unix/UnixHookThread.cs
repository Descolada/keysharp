using Keysharp.Builtins;
#if !WINDOWS
using System;
using System.Collections.Generic;
#if LINUX
using Keysharp.Internals.Input.Linux;
using EventCode = Keysharp.Internals.Input.Linux.Devices.EventCode;
#endif
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SharpHook;
using SharpHook.Data;
using static Keysharp.Internals.Input.Keyboard.KeyboardUtils;
using static Keysharp.Internals.Input.Keyboard.VirtualKeys;
using Keysharp.Internals.Input.Mouse;
using static Keysharp.Internals.Input.Keyboard.KeyboardMouseSender;
using static Keysharp.Internals.Platform.Unix.PlatformManager;

namespace Keysharp.Internals.Input.Hooks.Unix
{
	/// <summary>
	/// Shared Unix hook thread implementation.
	/// Platform-specific grabbing/query behavior is delegated to overrides.
	/// </summary>
	internal class UnixHookThread : Keysharp.Internals.Input.Hooks.HookThread
	{
		private static readonly bool HookDisabled = ShouldDisableHook();

		// Set to true for the duration of InputdHookLoop so callers can detect
		// they're on the hook reader thread and avoid lock acquisitions that
		// could deadlock against the script thread holding the same lock.
		[System.ThreadStatic]
		internal static bool IsHookReaderThread;

		// --- SharpHook ---
		private SimpleGlobalHook globalHook;
		private Task hookRunTask;
#if LINUX
		private KeysharpInputdClient inputdKeyboardHookClient;
		private KeysharpInputdClient inputdMouseHookClient;
		private CancellationTokenSource inputdHookCancel;
		private Task inputdKeyboardHookTask;
		private Task inputdMouseHookTask;
		private readonly InputdCallbackGate inputdCallbackGate = new();
		private bool usingInputdHooks;
		protected bool UsingInputdHooks => usingInputdHooks;
		private const int InputdCallbackGateWaitMs = 50;

		// On an X11/XWayland session the inputd hook path takes an exclusive evdev grab
		// whose replayed events only reach applications once the X server is up and has
		// adopted inputd's uinput replay device. Installing hooks during the volatile
		// early-login window (e.g. a desktop autostart entry) before X is reachable can
		// grab the devices while the replay path has no consumer, locking out the
		// keyboard and mouse with no automatic recovery. Wait out that window before
		// grabbing. The normal case (X already up) and pure Wayland skip the wait.
		private const int InputdGrabDisplayWaitMs = 5000;
		private const int InputdGrabDisplayWaitPollMs = 100;

		// Recovery bookkeeping for an unexpected loss of the inputd hook reader (daemon
		// crash/restart). One recovery runs at a time; repeated rapid losses pin the
		// X11 fallback so we don't thrash against a crash-looping daemon.
		private int inputdRecoveryInFlight;
		private long lastInputdRecoveryTicks;
		private int inputdRecoveryAttempts;
		private const long InputdRecoveryWindowMs = 5000;
		private const int MaxInputdRecoveryAttempts = 3;

		// ClipCursor enforcement: querying the cursor on Wayland is a D-Bus round-trip (KWin even
		// evaluates a JS snippet), so the per-move-event query is throttled to avoid flooding the
		// bus. X11's XQueryPointer is a cheap local call and is not throttled. Between throttled
		// queries the cursor may briefly drift outside the rectangle before being warped back.
		private long lastClipQueryTicks;
		private const int ClipCorrectionDelayMs = 8;
		private static readonly long ClipQueryMinIntervalTicks = System.Diagnostics.Stopwatch.Frequency * ClipCorrectionDelayMs / 1000;
		private int clipCorrectionRequest;
		private int clipCorrectionWorkerActive;
#endif

		private readonly Lock hookStateLock = new();
		private long hookStateGeneration;

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

#if LINUX
		private void UpdateIndicatorSnapshotFromInputd(uint flags)
		{
			// The indicator flags in the hook event are populated from current_caps_lock
			// etc. in the daemon, which are updated by EV_LED events.  EV_LED arrives
			// after EV_KEY, so the flags on the toggle-key event itself carry the
			// pre-toggle state.  This is acceptable: the snapshot will be stale for
			// exactly one event and corrected as soon as the next event arrives (after
			// EV_LED has updated the daemon-side ledstate).  We update unconditionally
			// so that the snapshot is always valid and IsKeyToggledOn never has to fall
			// through to a potentially slow IPC query during hook-callback processing.
			indicatorSnapshot = new IndicatorSnapshot(
				(flags & LLKHF_CAPS_LOCK_ON)   != 0,
				(flags & LLKHF_NUM_LOCK_ON)    != 0,
				(flags & LLKHF_SCROLL_LOCK_ON) != 0);
			indicatorSnapshotValid = true;
		}
#endif

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
		private readonly SyntheticEventQueue syntheticEventQueue = new();
		protected virtual long SyntheticEventTimeoutMs => 200;
		protected virtual long SyntheticKeyUpTimeoutMs => 200;
		protected virtual long SyntheticEventFutureToleranceMs => 25;
		private readonly Dictionary<uint, int> suppressedHotkeyReleases = new();
		private readonly HashSet<uint> replayedGrabbedDown = new();
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

		// Each armed end-key we grab. Modifier variants can be reconstructed from this metadata when needed.
		protected readonly HashSet<ArmedEnd> hsArmedEnds = new();

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

		[Flags]
		protected enum GrabKinds
		{
			None = 0,
			Hotkey = 1,
			Input = 2,
			Hotstring = 4,
		}

		// Tracks installed passive key grabs by physical X11 keycode/modifier pair and why they exist.
		protected readonly Dictionary<(uint xCode, uint mods), GrabKinds> activeKeyGrabs = new();
		protected readonly Dictionary<(uint button, uint mods), GrabKinds> activeButtonGrabs = new();
		protected virtual bool UseSyntheticEventQueue => false;
		protected virtual bool UsePlatformHotstringArming => false;
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
			public HashSet<(uint xcode, uint mods)> KeyGrabs;
			public HashSet<(uint button, uint mods)> ButtonGrabs;
		}

		protected static void AddGrabKind<TKey>(Dictionary<TKey, GrabKinds> target, TKey key, GrabKinds kind) where TKey : notnull
		{
			if (target.TryGetValue(key, out var existing))
				target[key] = existing | kind;
			else
				target[key] = kind;
		}

		protected static void RemoveGrabKind<TKey>(Dictionary<TKey, GrabKinds> target, TKey key, GrabKinds kind) where TKey : notnull
		{
			if (!target.TryGetValue(key, out var existing))
				return;

			existing &= ~kind;

			if (existing == GrabKinds.None)
				target.Remove(key);
			else
				target[key] = existing;
		}

		internal UnixHookThread() : base()
		{
			ConfigureScanCodeNames();
		}

		protected override KeyboardMouseSender CreateKbdMsSender()
		{
#if LINUX
			return ShouldUseInputdSender()
				? new InputdKeyboardMouseSender()
				: new LinuxKeyboardMouseSender();
#else
			return new UnixKeyboardMouseSender();
#endif
		}

#if LINUX
		private static bool ShouldUseInputdSender()
		{
			if (KeysharpInputdManager.IsLegacyX11FallbackActive)
				return false;

			if (!IsX11Available)
				return true;

			// Only probe daemon reachability here — do NOT request any capabilities,
			// which would trigger a permission prompt at every Script construction
			// (including for hosts like Keyview that don't actually use hooks/send).
			// The real capability prompt happens lazily on first hook registration
			// or Send call via EnsureInputMonitoring / EnsureInputInjection.
			if (KeysharpInputdManager.IsDaemonReachable())
				return true;

			KeysharpInputdManager.ActivateLegacyX11Fallback(
				$"keysharp-inputd not reachable at '{KeysharpInputdClient.DefaultSocketPath}'.");
			return false;
		}

		private void EnsureLegacyX11Sender()
		{
			if (kbdMsSender is LinuxKeyboardMouseSender)
				return;

			try
			{
				if (kbdMsSender is IDisposable disposable)
					disposable.Dispose();
			}
			catch
			{
			}

			kbdMsSender = new LinuxKeyboardMouseSender();
			ResetTrackedInputState(clearSyntheticQueue: true);
		}
#endif

		internal SendScope EnterSendScope() => new(this);

		private void SyncModifiersAfterSend()
		{
			// Ensure modifiers don't stay logically down after Send manipulates them.
			// On the inputd path the modifier reconciliation is handled by
			// InputdKeyboardMouseSender.ReconcileLogicalModifiersFromOs (called from
			// SendEventArray after each bypass-hook batch), and by hook echoes for
			// Event-mode sends. The GetModifierLRState(true) call below handles the
			// X11/SharpHook path via the "wrongly down" correction in that method;
			// on the inputd path it is a no-op (IsKeyDownAsync reads modifiersLRLogical
			// directly, so modifiersWronglyDown is always zero there).
			_ = kbdMsSender?.GetModifierLRState(true);
		}

		private void ResetTrackedInputState(bool clearSyntheticQueue)
		{
			System.Array.Clear(physicalKeyState, 0, physicalKeyState.Length);
			System.Array.Clear(logicalKeyState, 0, logicalKeyState.Length);

			if (clearSyntheticQueue)
				syntheticEventQueue.Clear();

			lock (replayedGrabbedDown)
				replayedGrabbedDown.Clear();

			kbdMsSender.modifiersLRLogical = 0;
			kbdMsSender.modifiersLRLogicalNonIgnored = 0;
			kbdMsSender.modifiersLRPhysical = 0;
		}

		// Works for both inputd and X11 paths: KeyCodes.MapVkToSc dispatches to the right
		// backend based on the active scan-code mode, so the names are always consistent
		// with the actual SCs that the hook will deliver.
		internal void ConfigureScanCodeNames()
		{
			AddScKeyName("NumpadEnter", KeyCodes.MapVkToSc(VK_RETURN, true));
			AddScKeyName("Delete", KeyCodes.MapVkToSc(VK_DELETE));
			AddScKeyName("Del", KeyCodes.MapVkToSc(VK_DELETE));
			AddScKeyName("Insert", KeyCodes.MapVkToSc(VK_INSERT));
			AddScKeyName("Ins", KeyCodes.MapVkToSc(VK_INSERT));
			AddScKeyName("Up", KeyCodes.MapVkToSc(VK_UP));
			AddScKeyName("Down", KeyCodes.MapVkToSc(VK_DOWN));
			AddScKeyName("Left", KeyCodes.MapVkToSc(VK_LEFT));
			AddScKeyName("Right", KeyCodes.MapVkToSc(VK_RIGHT));
			AddScKeyName("Home", KeyCodes.MapVkToSc(VK_HOME));
			AddScKeyName("End", KeyCodes.MapVkToSc(VK_END));
			AddScKeyName("PgUp", KeyCodes.MapVkToSc(VK_PRIOR));
			AddScKeyName("PageUp", KeyCodes.MapVkToSc(VK_PRIOR));
			AddScKeyName("PgDn", KeyCodes.MapVkToSc(VK_NEXT));
			AddScKeyName("PageDown", KeyCodes.MapVkToSc(VK_NEXT));
		}

		// -------------------- lifecycle --------------------
		protected internal override void DeregisterHooks()
		{
			lock (hookStateLock)
			{
				hookStateGeneration++;
				keyboardEnabled = false;
				mouseEnabled = false;
#if LINUX
				StopInputdHookCore();
#endif
				StopGlobalHookCore(dispose: true);
				kbdHook = 0;
				mouseHook = 0;
				PlatformUngrabAll();
			}

			// Release the named hook mutexes now that no hooks are held (mirrors AddRemoveHooks; this path
			// doesn't route through it). Done outside the lock since closing a Mutex never touches hookStateLock.
			SyncHookMutexes(changeIsTemporary: false);
		}

		public override void SimulateKeyPress(uint key)
		{
			// SimulateKeyPress expects a WinForms virtual-key value.
			var vk = (uint)((Keys)key & Keys.KeyCode);
			if (vk == 0)
				return;

			var kc = KeyCodes.VkToSharpHook(vk);
			if (kc == KeyCode.VcUndefined)
				return;

			var sc = KeyCodes.MapVkToSc(vk);
			if (sc == 0)
				sc = KeyCodes.MapVkToSc(vk);

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
			long requestGeneration;

			lock (hookStateLock)
				requestGeneration = ++hookStateGeneration;

			ChangeHookStateLinux(hooksToBeActive & (HookType.Keyboard | HookType.Mouse), changeIsTemporary, requestGeneration);

			// Advertise the now-current hook state to other Keysharp scripts via the shared named mutexes
			// (HasKbdHook()/HasMouseHook() read the sentinels ChangeHookStateLinux just updated). This is what
			// lets SendInput fall back correctly and A_KeybdHookInstalled/A_MouseHookInstalled report bit 2.
			SyncHookMutexes(changeIsTemporary);
		}

		private void ChangeHookStateLinux(HookType req, bool changeIsTemporary, long expectedGeneration)
		{
			if (HookDisabled)
			{
				lock (hookStateLock)
				{
					if (hookStateGeneration != expectedGeneration)
						return;

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
				if (hookStateGeneration != expectedGeneration)
					return;

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
#if LINUX
				var inputdMessage = string.Empty;

				if (!KeysharpInputdManager.IsLegacyX11FallbackActive && TryStartInputdHookCore(wantKeyboard, wantMouse, out inputdMessage))
				{
					StopGlobalHookCore(dispose: true);

					if (!changeIsTemporary)
					{
						if (wantKeyboard && !hadKeyboard)
						{
							keyboardEnabled = false; kbdHook = 0;
							ResetHook(false, HookType.Keyboard, true);
						}
						if (wantMouse && !hadMouse)
						{
							mouseEnabled = false; mouseHook = 0;
							ResetHook(false, HookType.Mouse, true);
						}
					}

					keyboardEnabled = wantKeyboard;
					mouseEnabled = wantMouse;
					kbdHook = wantKeyboard ? 1 : 0;
					mouseHook = wantMouse ? 1 : 0;
					usingInputdHooks = true;
					return;
				}

				if (!KeysharpInputdManager.IsLegacyX11FallbackActive)
					Ks.OutputDebugLine($"keysharp-inputd hook unavailable; falling back to X11/SharpHook. {inputdMessage}");

				EnsureLegacyX11Sender();
#endif

				// libuiohook is a single-process-global hook: run()/hook_stop() share one CFRunLoop reference.
				// The test host creates many Script instances (per test, plus the compiled programs the
				// RunScript-based tests execute, plus RealThread workers), several of which start a hook; a new
				// hook_run() begun before a prior one has stopped overwrites that shared state, and a later
				// hook_stop then dereferences a stale run loop -> native SIGSEGV (not catchable, and unfixable by
				// teardown since the corruption already happened). The tests inject input by invoking OnKeyPressed
				// directly, so the native tap is never needed in the headless host; skip starting it and rely on
				// the install sentinels set below (HasKbdHook only checks those).
				if (!Script.IsUiInitializationBlocked && (globalHook == null || !globalHook.IsRunning))
				{
					_ = Script.TheScript.Permissions.EnsureInputMonitoring(operation: "install keyboard/mouse hooks");
					StopGlobalHookCore(dispose: true); // clean restart if something half-exists

					globalHook = new SimpleGlobalHook(default, default, true);

					globalHook.KeyPressed += OnKeyPressed;
					globalHook.KeyReleased += OnKeyReleased;

					globalHook.MousePressed += OnMousePressed;
					globalHook.MouseReleased += OnMouseReleased;
					globalHook.MouseMoved += OnMouseMoved;
					// libuiohook reports moving with a button held as a separate "dragged" event. A drag is
					// still cursor movement, so route it through the same handler -- otherwise OnMouseMove
					// callbacks, VisibleMouseMove/BlockInput suppression and ClipCursor all miss drags on the
					// SharpHook backend (macOS, and the Linux X11/SharpHook fallback). The Linux inputd path
					// is unaffected: it reads movement at the evdev level, which has no move/drag distinction.
					globalHook.MouseDragged += OnMouseMoved;
					globalHook.MouseWheel += OnMouseWheel;

					// Start hook thread
					hookRunTask = globalHook.RunAsync();
					_ = hookRunTask.ContinueWith(t =>
					{
						var ex = t.Exception?.GetBaseException();
						var detail = ex != null ? ex.ToString() : "Unknown hook startup failure.";

#if OSX
						Ks.OutputDebugLine($"Global hook failed on macOS: {detail}");
						Ks.OutputDebugLine("Check macOS permissions: Accessibility and Input Monitoring may be required.");
#else
						Ks.OutputDebugLine($"Global hook failed: {detail}");
#endif
					}, TaskContinuationOptions.OnlyOnFaulted);
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
#if LINUX
				usingInputdHooks = false;
#endif
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
				ResetTrackedInputState(clearSyntheticQueue: true);
				ResetIndicatorSnapshot();

				// Release any active cursor freeze (macOS decouple) now that the hook is gone, so a
				// blocked cursor can't be left stuck when the hook is removed without a final move event.
				SetMoveSuppression(false);
			}
			// If dispose==false: we intentionally keep the hook alive and keep current
			// state around; the processing gate is controlled by keyboardEnabled/mouseEnabled.
		}

		internal override void ChangeHookState(HotkeyDefinition[] hk, HookType whichHook, HookType whichHookAlways)
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
			ResetTrackedInputState(clearSyntheticQueue: true);
		}

#if LINUX
		// Blocks briefly for the X server to become reachable before the inputd hook
		// path takes its exclusive evdev grab, so the grab is never established while
		// the replay path has no consumer (see InputdGrabDisplayWaitMs). Only waits on
		// an X11/XWayland session where X isn't up yet; pure Wayland (no X expected) and
		// the normal case (X already reachable) return immediately.
		private static void WaitForDisplayServerBeforeGrab()
		{
			if (IsWaylandSession || IsX11Available)
				return;

			var deadline = Environment.TickCount64 + InputdGrabDisplayWaitMs;

			while (Environment.TickCount64 < deadline && !IsX11Available)
				Thread.Sleep(InputdGrabDisplayWaitPollMs);
		}

		private bool TryStartInputdHookCore(bool wantKeyboard, bool wantMouse, out string message)
		{
			message = string.Empty;

			if (!wantKeyboard && !wantMouse)
				return false;

			var keyboardRunning = inputdKeyboardHookClient != null
				&& inputdKeyboardHookTask != null
				&& !inputdKeyboardHookTask.IsCompleted;
			var mouseRunning = inputdMouseHookClient != null
				&& inputdMouseHookTask != null
				&& !inputdMouseHookTask.IsCompleted;

			if (keyboardRunning == wantKeyboard && mouseRunning == wantMouse)
				return true;

			// Don't take the exclusive evdev grab until the display server is ready to
			// consume inputd's replayed events.
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
				message = permission.Message;
				if (IsX11Available)
					KeysharpInputdManager.ActivateLegacyX11Fallback(message);
				return false;
			}

			try
			{
				if (wantKeyboard)
				{
					inputdKeyboardHookClient = KeysharpInputdClient.Connect(permissionRequest);
					_ = inputdKeyboardHookClient.SubscribeHook(KeysharpInputdClient.HookType.KeyboardLowLevel);
				}

				if (wantMouse)
				{
					inputdMouseHookClient = KeysharpInputdClient.Connect(permissionRequest);
					_ = inputdMouseHookClient.SubscribeHook(KeysharpInputdClient.HookType.MouseLowLevel);
				}

				inputdHookCancel = new CancellationTokenSource();
				var hookToken = inputdHookCancel.Token;

				if (inputdKeyboardHookClient != null)
				{
					var keyboardClient = inputdKeyboardHookClient;
					inputdKeyboardHookTask = Task.Run(() => InputdHookLoop(keyboardClient, hookToken));
				}

				if (inputdMouseHookClient != null)
				{
					var mouseClient = inputdMouseHookClient;
					inputdMouseHookTask = Task.Run(() => InputdHookLoop(mouseClient, hookToken));
				}

				return true;
			}
			catch (Exception ex)
			{
				StopInputdHookCore();
				message = ex.Message;
				if (IsX11Available)
					KeysharpInputdManager.ActivateLegacyX11Fallback(message);
				return false;
			}
		}

		private void StopInputdHookCore()
		{
			usingInputdHooks = false;
			try { inputdHookCancel?.Cancel(); } catch { }
			try { inputdKeyboardHookClient?.Dispose(); } catch { }
			try { inputdMouseHookClient?.Dispose(); } catch { }
			try { if (inputdKeyboardHookTask != null && !inputdKeyboardHookTask.IsCompleted) inputdKeyboardHookTask.Wait(50); } catch { }
			try { if (inputdMouseHookTask != null && !inputdMouseHookTask.IsCompleted) inputdMouseHookTask.Wait(50); } catch { }
			inputdHookCancel = null;
			inputdKeyboardHookClient = null;
			inputdMouseHookClient = null;
			inputdKeyboardHookTask = null;
			inputdMouseHookTask = null;
		}

		// Tracks whether the inputd hook reader thread is making progress, so the
		// lease heartbeat only renews the daemon's grab lease while the reader is
		// genuinely alive. A reader healthily blocked waiting for the next event
		// counts as alive; one wedged mid-processing past the grace window does
		// not, letting the daemon's dead-man's-switch release the grabbed devices
		// instead of leaving input frozen.
		private sealed class HookReaderLiveness
		{
			// Must stay well under the daemon's 15s grab-lease timeout so the lease
			// can still expire after a wedge. Generous because the daemon's
			// per-event decision-timeout eviction is the primary fast recovery and
			// this lease path is only the backstop.
			private const long StallGraceMs = 10_000;

			private long lastProgressTicks = Environment.TickCount64;
			private volatile bool waitingForEvent = true;

			// About to block waiting for the next event (healthy idle): alive for
			// the whole wait, however long.
			internal void MarkWaiting()
			{
				Volatile.Write(ref lastProgressTicks, Environment.TickCount64);
				waitingForEvent = true;
			}

			// An event was received and processing begins: a wedge from here on
			// eventually trips the stall grace.
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

			// Drive the client's lease heartbeat off this reader's actual progress
			// so a wedged reader stops renewing the daemon's grab lease (see
			// HookReaderLiveness / KeysharpInputdClient.SetLeaseLivenessProbe).
			var liveness = new HookReaderLiveness();
			client.SetLeaseLivenessProbe(liveness.IsAlive);

			try
			{
				InputdHookLoopCore(client, token, liveness);
			}
			finally
			{
				// Once the reader has exited, never renew the lease again — the
				// connection is about to be disposed; let the lease lapse if that
				// is somehow delayed.
				client.SetLeaseLivenessProbe(static () => false);
				IsHookReaderThread = false;
			}
		}

		private void InputdHookLoopCore(KeysharpInputdClient client, CancellationToken token, HookReaderLiveness liveness)
		{
			while (!token.IsCancellationRequested)
			{
				KeysharpInputdClient.HookEvent hookEvent;

				// Healthy idle: about to block waiting for the next event.
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

				// An event arrived; processing begins. A wedge from here on trips
				// the stall grace and stops lease renewal.
				liveness.MarkProgress();

				// Emergency panic-ungrab is checked here, before the shared callback gate, so it still
				// fires when the main script thread is hung or has BlockInput active (a runaway script
				// can otherwise lock the user out since inputd grabs the devices).
				CheckInputdPanicCombo(hookEvent);

				var block = false;
				var callbackStateLost = false;

				try
				{
					// LowLevelCommon intentionally shares combined keyboard/mouse state.
					// Never let one stuck callback consume the other lane's native
					// one-second decision deadline: fail open if the shared state owner
					// is not available promptly.
					if (inputdCallbackGate.TryEnter(InputdCallbackGateWaitMs, out var callbackLease))
					{
						using (callbackLease)
						{
							block = hookEvent.HookType switch
							{
								KeysharpInputdClient.HookType.KeyboardLowLevel => ProcessInputdKeyboardHook(hookEvent.Keyboard),
								KeysharpInputdClient.HookType.MouseLowLevel => ProcessInputdMouseHook(hookEvent.Mouse),
								_ => false
							};
						}
					}
					else
					{
						// Transient contention: the OTHER lane is holding the shared callback
						// state (e.g. one lane processing a burst of events), not a lost
						// connection. The event is failed open below (block stays false -> Pass),
						// which is the documented "fail open" intent above.
						NoteInputdCallbackGateContention();
					}
				}
				catch (Exception ex)
				{
					Ks.OutputDebugLine($"keysharp-inputd hook event processing failed: {ex}");
					callbackStateLost = true;
				}

				try
				{
					client.SendHookDecision(
						hookEvent.EventId,
						block ? KeysharpInputdClient.HookDecision.Block : KeysharpInputdClient.HookDecision.Pass);

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
					// The daemon already passed this event after its one-second decision
					// deadline. This commonly happens while stopped in a debugger and does
					// not mean the hook connection or subscription was lost.
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

		// A single inputd reader lane couldn't take the shared callback gate within the
		// per-event deadline (the other lane is busy). We fail that event open and keep
		// the hooks up; surface it at most once every couple of seconds so a burst of
		// events on one lane can't spam the debug console.
		private long lastGateContentionLogTicks;

		private void NoteInputdCallbackGateContention()
		{
			var now = Environment.TickCount64;

			if (now - Volatile.Read(ref lastGateContentionLogTicks) < 2000)
				return;

			Volatile.Write(ref lastGateContentionLogTicks, now);
			Ks.OutputDebugLine("keysharp-inputd: shared callback state busy; passed an event without processing (hooks remain active).");
		}

		// Called from the inputd hook reader thread as it exits due to a daemon
		// disconnect/error (never on an intentional stop — the caller checks the
		// cancellation token first). Recovers on a separate thread so we don't rebuild
		// hooks from the dying reader thread.
		// Emergency panic-ungrab combo state (Ctrl+Alt+Pause). Updated and read only on the keyboard
		// hook reader thread, so no synchronization is needed.
		private bool inputdPanicCtrlDown;
		private bool inputdPanicAltDown;

		// Detects Ctrl+Alt+Pause from the raw keyboard hook stream and asks the daemon to release all
		// grabs/block-input. Runs before the callback gate so it works even when the main thread is
		// hung. Never alters the event's pass/block decision -- the keystroke still flows normally.
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
				case VK_CANCEL: // Ctrl+Pause reports as Break/Cancel on some layouts.
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

				// Force the re-arm below to treat these hooks as newly enabled so it
				// resyncs key/modifier snapshots (avoids stuck keys after the switch).
				keyboardEnabled = false;
				mouseEnabled = false;

				// Keep teardown in the same state transition. Otherwise a newer
				// AddRemoveHooks call can install a connection which stale recovery
				// work then tears down.
				StopInputdHookCore();
			}

			if (want == HookType.None)
				return;

			// Repeated rapid losses mean the daemon is gone or crash-looping; pin the
			// X11/SharpHook fallback (when available) instead of reconnecting forever.
			var now = Environment.TickCount64;

			if (now - lastInputdRecoveryTicks < InputdRecoveryWindowMs)
			{
				if (++inputdRecoveryAttempts >= MaxInputdRecoveryAttempts && IsX11Available)
					KeysharpInputdManager.ActivateLegacyX11Fallback(reason);
			}
			else
			{
				inputdRecoveryAttempts = 1;
			}

			lastInputdRecoveryTicks = now;

			if (!IsX11Available && KeysharpInputdManager.IsLegacyX11FallbackActive)
			{
				// No daemon and no X11 fallback — nothing left to route input through.
				if (CursorClipActive)
					ClearCursorClip();

				Ks.OutputDebugLine($"keysharp-inputd hooks lost and no fallback is available: {reason}");
				return;
			}

			Ks.OutputDebugLine($"keysharp-inputd hook reader lost ({reason}); re-establishing hooks.");

			// Re-arm. If the fallback is now active this routes through X11/SharpHook;
			// otherwise it reconnects to a freshly socket-activated daemon, and if that
			// reconnect fails TryStartInputdHookCore activates the X11 fallback itself.
			ChangeHookStateLinux(want, changeIsTemporary: false, expectedGeneration: recoveryGeneration);

			if (CursorClipActive && !UsingInputdHooks)
			{
				ClearCursorClip();
				Ks.OutputDebugLine("ClipCursor released because the keysharp-inputd mouse hook was lost.");
			}
		}

		// KSI_LLKHF_* indicator bits set by the daemon on every keyboard hook event.
		private const uint LLKHF_CAPS_LOCK_ON   = 0x04u;
		private const uint LLKHF_NUM_LOCK_ON    = 0x08u;
		private const uint LLKHF_SCROLL_LOCK_ON = 0x40u;

		// Maps a numpad key's evdev keycode (delivered by the daemon as the scan code) to the
		// navigation VK it produces when NumLock is off; returns 0 for any non-numpad key.
		private static uint NumpadNavigationVk(uint sc) => sc switch
		{
			(uint)EventCode.Kp7 => VK_HOME,   // KEY_KP7   → NumpadHome
			(uint)EventCode.Kp8 => VK_UP,     // KEY_KP8   → NumpadUp
			(uint)EventCode.Kp9 => VK_PRIOR,  // KEY_KP9   → NumpadPgUp
			(uint)EventCode.Kp4 => VK_LEFT,   // KEY_KP4   → NumpadLeft
			(uint)EventCode.Kp5 => VK_CLEAR,  // KEY_KP5   → NumpadClear
			(uint)EventCode.Kp6 => VK_RIGHT,  // KEY_KP6   → NumpadRight
			(uint)EventCode.Kp1 => VK_END,    // KEY_KP1   → NumpadEnd
			(uint)EventCode.Kp2 => VK_DOWN,   // KEY_KP2   → NumpadDown
			(uint)EventCode.Kp3 => VK_NEXT,   // KEY_KP3   → NumpadPgDn
			(uint)EventCode.Kp0 => VK_INSERT, // KEY_KP0   → NumpadIns
			(uint)EventCode.KpDot => VK_DELETE, // KEY_KPDOT → NumpadDel
			_ => 0u
		};

		private bool ProcessInputdKeyboardHook(KeysharpInputdClient.KeyboardHookEvent ev)
		{
			if (!keyboardEnabled)
				return false;

			// Update the indicator snapshot from the flags the daemon embeds in every
			// keyboard event — no separate IPC query needed, no reentrancy risk.
			// VK is passed so we can skip the update for toggle keys (EV_LED arrives
			// after EV_KEY, so the embedded flags would be stale for those keys).
			UpdateIndicatorSnapshotFromInputd(ev.Flags);

			var vk = ev.VkCode;
			var sc = ev.ScanCode <= SC_MAX ? ev.ScanCode : 0u;
			var keyUp = (ev.Flags & 0x80u) != 0 || ev.Message == 0x0101u || ev.Message == 0x0105u;
			var isInjected = (ev.Flags & 0x10u) != 0;

			// Mirror WindowsHookThread.LowLevelKeybdHandler: an event tagged KeyPhysIgnore must be
			// treated as PHYSICAL for key/modifier state tracking even though it was injected. AHK uses
			// this to mark a prefix key's synthetic release as physical (see HookThread.cs where
			// KeyPhysIgnore is sent) so IsKeyDown/KeyWait and the physical modifier mask stay correct.
			// It still won't fire hotkeys because IsIgnored(KeyPhysIgnore) is true in LowLevelCommon.
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

			// evdev keycodes are NumLock-agnostic, so the daemon always reports a numpad key's
			// NumLock-on VK (VK_NUMPAD0..9 / VK_DECIMAL). Windows' low-level hook instead reports the
			// NumLock-off navigation VK (VK_UP, VK_HOME, ...) depending on both NumLock and Shift:
			// holding Shift inverts the NumLock interpretation (so Shift+Numpad8 with NumLock on yields
			// VK_UP, and Shift+Numpad8 with NumLock off yields VK_NUMPAD8). The key is in navigation
			// mode exactly when NumLock and Shift agree. Mirror that here so "NumpadUp::" (bound by
			// VK_UP) and "Numpad8::" (bound by VK_NUMPAD8) fire under the same conditions as on Windows.
			// The scan code (the evdev keycode) is left untouched, so dedicated "Up::" (a distinct
			// keycode resolved by scTakesPrecedence) is unaffected.
			var numLockOn = (ev.Flags & LLKHF_NUM_LOCK_ON) != 0;
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

			var keyCode = KeyCodes.VkToSharpHook(vk);
			var raw = new UioHookEvent
			{
				Type = keyUp ? EventType.KeyReleased : EventType.KeyPressed,
				Time = ev.TimeMs,
				Mask = isInjected ? EventMask.SimulatedEvent : EventMask.None,
				Keyboard = new KeyboardEventData
				{
					KeyCode = keyCode,
					RawCode = (ushort)sc,
					RawKeyChar = KeyboardEventData.RawUndefinedChar
				}
			};

			var args = new KeyboardHookEventArgs(raw);
			var extraInfo = ev.ExtraInfo;

			if (extraInfo == (ulong)KeyboardMouseSender.KeyBlockThis)
				return true;

			// AHK's hook.cpp updates modifier state via UpdateKeybdState from
			// inside SuppressThisKeyFunc / AllowIt at the end of LowLevelCommon —
			// nothing pre-call is needed. The Linux-specific complication is
			// that synth modifier events re-enter the hook asynchronously, so
			// any sender that needs state to be current within the same hotkey
			// dispatch must update it synchronously at the send site (see
			// InputdKeyboardMouseSender.SendKeybdEvent).
			var result = LowLevelCommon(args, vk, sc, ev.ScanCode, keyUp, extraInfo, isInjected ? HOOK_EVENT_INJECTED : 0, ev.DeviceId);
			ApplyKeyStateAfterKeyboardDecision(vk, keyUp, isInjected, result, replayed: false, wasGrabbed: false);
			return result != 0;
		}

		// --- ClipCursor (Linux) ---
		// Clipping requires inputd to receive and suppress physical move events. Wayland also
		// requires a compositor backend capable of both querying and moving the cursor; X11 uses
		// XQueryPointer/XWarpPointer for those operations.

		protected override bool CanClipCursor(out string reason)
		{
			if (!UsingInputdHooks)
			{
				reason = "the keysharp-inputd mouse hook is not active";
				return false;
			}

			if (!IsWaylandSession)
			{
				if (GetCursorPos(out _))
				{
					reason = "";
					return true;
				}

				reason = "no X11 cursor-position query is available";
				return false;
			}

			var backend = Keysharp.Internals.Window.Linux.Wayland.WaylandBackend.Current;

			if (backend?.SupportsMouse != true || !backend.TryGetCursorPos(out _, out _))
			{
				reason = "the Wayland compositor backend cannot both query and move the cursor";
				return false;
			}

			reason = "";
			return true;
		}

		protected override void WarpCursor(int x, int y)
		{
			if (IsWaylandSession)
			{
				_ = Keysharp.Internals.Window.Linux.Wayland.WaylandBackend.Current?.TrySendMouseMoveAbsolute(x, y);
				return;
			}

			// X11: XWarpPointer is pixel-accurate, unlike inputd's normalised uinput abs path.
			try
			{
				var display = Keysharp.Internals.Window.Linux.Proxies.XDisplay.Default;

				if (display != null && display.Handle != 0)
				{
					var root = Keysharp.Internals.Window.Linux.X11.Xlib.XDefaultRootWindow(display.Handle);
					_ = Keysharp.Internals.Window.Linux.X11.Xlib.XWarpPointer(display.Handle, 0, root, 0, 0, 0, 0, x, y);
					_ = Keysharp.Internals.Window.Linux.X11.Xlib.XFlush(display.Handle);
				}
			}
			catch
			{
			}
		}

		// Hot-path query used during clip enforcement. X11 is queried every event (cheap);
		// Wayland queries are rate-limited to avoid flooding D-Bus. A throttled-out event returns
		// false so the caller leaves the move alone until the next allowed query.
		private bool TryGetCursorPosThrottled(out POINT p)
		{
			if (IsWaylandSession)
			{
				var now = System.Diagnostics.Stopwatch.GetTimestamp();

				if (now - lastClipQueryTicks < ClipQueryMinIntervalTicks)
				{
					p = default;
					return false;
				}

				lastClipQueryTicks = now;
			}

			return GetCursorPos(out p);
		}

		// inputd delivers movement before replaying it through uinput, so the position observed by
		// ProcessInputdMouseHook belongs to the previous event. Coalesce a correction after replay
		// so the final event which crosses the boundary cannot leave the cursor outside indefinitely.
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
							WarpCursor(x, y);
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

			// Do not lose a request which raced with the worker shutting down.
			if (CursorClipActive
				&& handledRequest != Volatile.Read(ref clipCorrectionRequest)
				&& Interlocked.CompareExchange(ref clipCorrectionWorkerActive, 1, 0) == 0)
				_ = Task.Run(RunCursorClipCorrectionAsync);
		}

		// Cached virtual-desktop (X11 root) dimensions used to convert absolute-pointer coordinates
		// ([0,65535]) into screen pixels. XGetGeometry is a server round-trip, so the result is cached
		// and refreshed at most once per second (resolution changes are rare), keeping the
		// high-frequency mouse-move path cheap. Touched only from the mouse hook reader thread.
		private int absNormWidth;
		private int absNormHeight;
		private long absNormDimsTicks;

		// Converts a daemon absolute-pointer coordinate (each axis normalised to [0,65535] across the
		// whole virtual desktop, flagged KSI_MOUSEEVENTF_ABSOLUTE) into an X11 root pixel coordinate --
		// the same space XQueryPointer reports for clicks, so moves and clicks agree. Leaves the value
		// untouched if the desktop size can't be determined (e.g. no X11), so the raw normalised value
		// is still forwarded rather than zeroed.
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

				// The default-screen root window spans the entire virtual desktop with origin (0,0),
				// which is exactly the coordinate space XQueryPointer returns for the click path.
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

			var isInjected = (ev.Flags & 0x01u) != 0;
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
					// Confine the cursor to the active ClipCursor rectangle. ev.X/ev.Y are raw evdev
					// values (deltas or device-range abs), not screen pixels, so query the real
					// screen-space position to decide whether to clamp. The mouse device is grabbed,
					// so this fires before the move reaches the compositor; the position read here is
					// from the previous (already-clamped) event, which makes this a per-event
					// warp-back that pins the cursor to the boundary while preserving acceleration.
					if (!isInjected && CursorClipActive && TryGetCursorPosThrottled(out var clipPos))
					{
						int cx = clipPos.X, cy = clipPos.Y;

						if (ClampToCursorClip(ref cx, ref cy))
						{
							WarpCursor(cx, cy);
							return true;
						}
					}

					var moveBlocked = !isInjected && Script.TheScript.KeyboardData.blockMouseMove;

					// Forward movement to any active InputHook(s) and honor VisibleMouseMove. The SharpHook
					// fallback does this in OnMouseMoved; the inputd path is the live backend on this host,
					// so without this call OnMouseMove never fires and VisibleMouseMove can't suppress. The
					// coordinate meaning depends on the device (see linux_devices.c): an absolute pointer
					// (VMware's virtual mouse, tablets, touchpads) sends each axis normalised to [0,65535]
					// flagged ABSOLUTE, which we convert to real screen pixels so OnMouseMove matches the
					// Windows hook; a relative mouse sends motion deltas, forwarded verbatim per the
					// CollectMouseMove coordinate contract.
					//
					// Gate the whole thing on an actual move listener: this runs under the shared callback
					// gate on every move, so under a high-frequency move stream we must not do the
					// coordinate normalization or CollectMouseMove work when no InputHook cares (the
					// common case).
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

			// The daemon hardcodes ev.X/ev.Y to 0 for button events (dispatch_mouse_button_event in
			// linux_devices.c -- evdev button events carry no position), so query the real screen cursor
			// position to give clicks an absolute coordinate matching the Windows hook (lParam.pt). The
			// query is reused below for clip enforcement. On Wayland without a compositor backend the
			// query fails and X/Y fall back to ev.X/ev.Y (0).
			//
			// Only query when something will use the result: the clip rectangle is active, or an
			// InputHook exists that may report the click position (OnMouseDown/OnMouseUp). On Wayland
			// GetCursorPos is a compositor round-trip running on this gate-held hook path, so skip it for
			// the common no-clip/no-hook case (and for injected events, which never warp or report here).
			POINT clickPos = default;
			var haveClickPos = !isInjected
				&& (CursorClipActive || Script.TheScript.input != null)
				&& GetCursorPos(out clickPos);

			// Confine clicks too. Because the per-event move warp-back lags by one event, the cursor
			// can sit just outside the rectangle when a button event arrives. inputd re-injects the
			// button at the current cursor position, so pull the cursor back inside first; the click
			// then lands within the clip region.
			if (!isInjected && CursorClipActive && haveClickPos)
			{
				int bx = clickPos.X, by = clickPos.Y;

				if (ClampToCursorClip(ref bx, ref by))
				{
					WarpCursor(bx, by);
					clickPos = new POINT(bx, by); // Report the clamped position the click actually lands at.
				}
			}

			var keyUp = true;
			var vk = ev.Message switch
			{
				0x0201u => VK_LBUTTON,
				0x0202u => VK_LBUTTON,
				0x0204u => VK_RBUTTON,
				0x0205u => VK_RBUTTON,
				0x0207u => VK_MBUTTON,
				0x0208u => VK_MBUTTON,
				0x020Bu => (ev.MouseData >> 16) == MouseUtils.XBUTTON1 ? VK_XBUTTON1 : VK_XBUTTON2,
				0x020Cu => (ev.MouseData >> 16) == MouseUtils.XBUTTON1 ? VK_XBUTTON1 : VK_XBUTTON2,
				_ => 0u
			};

			if (ev.Message == 0x0201u || ev.Message == 0x0204u || ev.Message == 0x0207u || ev.Message == 0x020Bu)
				keyUp = false;

			if (vk == 0)
				return false;

			var raw = new UioHookEvent
			{
				Type = keyUp ? EventType.MouseReleased : EventType.MousePressed,
				Time = ev.TimeMs,
				Mask = isInjected ? EventMask.SimulatedEvent : EventMask.None,
				Mouse = new MouseEventData
				{
					Button = KeyCodes.VkToMouseButton(vk),
					Clicks = 1,
					X = haveClickPos ? (short)clickPos.X : (short)ev.X,
					Y = haveClickPos ? (short)clickPos.Y : (short)ev.Y
				}
			};

			var args = new MouseHookEventArgs(raw);
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
			var raw = new UioHookEvent
			{
				Type = EventType.MouseWheel,
				Time = ev.TimeMs,
				Mask = isInjected ? EventMask.SimulatedEvent : EventMask.None,
				Wheel = new MouseWheelEventData
				{
					Type = MouseWheelScrollType.UnitScroll,
					Rotation = delta,
					Delta = 120,
					Direction = vertical ? MouseWheelScrollDirection.Vertical : MouseWheelScrollDirection.Horizontal,
					X = (short)ev.X,
					Y = (short)ev.Y
				}
			};

			var args = new MouseWheelHookEventArgs(raw);
			var result = LowLevelCommon(args, vk, sc, sc, keyUp: false, ev.ExtraInfo, isInjected ? HOOK_EVENT_INJECTED : 0, deviceId: ev.DeviceId);
			return result != 0;
		}
#endif

		private static bool ShouldDisableHook()
		{
			var env = Environment.GetEnvironmentVariable("KEYSHARP_DISABLE_HOOK");
			return !string.IsNullOrEmpty(env) &&
				   (env.Equals("1") || env.Equals("true", StringComparison.OrdinalIgnoreCase) || env.Equals("yes", StringComparison.OrdinalIgnoreCase));
		}

		// -------------------- event handlers --------------------

		// On platforms without an OS-level "block all input" API (e.g. macOS, and the X11/SharpHook
		// fallback path), BlockInput is implemented by suppressing physical events at the hook level.
		// Injected (self-generated) events must still pass through so that Send/Click keep working
		// while BlockInput is active.
		private static bool ShouldSuppressForBlockInput(bool isInjected) =>
			!isInjected && Script.TheScript.KeyboardData.blockInput;

		private void OnKeyPressed(object sender, KeyboardHookEventArgs e)
		{
			if (!keyboardEnabled) return;
			UpdateIndicatorSnapshotFromMask(e.RawEvent.Mask);

			var keyCode = e.Data.KeyCode;
			var vk = KeyCodes.SharpHookToVk(keyCode);
			if (vk == 0) return;

			lastHookEventWasKeyboard = true;
			lastKeyboardEventVk = vk;
			var sc = (uint)e.RawEvent.Keyboard.RawCode;
			var wasGrabbed = WasKeyGrabbed(e, vk, keyUp: false, out var grabbedByHotstring);
			var isInjected = MarkSimulatedIfNeeded(e, vk, keyCode, false, out ulong extraInfo);
			extraInfo = ComputeExtraInfo(extraInfo, isInjected || e.IsEventSimulated);

			if (ShouldSuppressForBlockInput(isInjected))
			{
				UpdateObservedPhysicalKeyState(vk, keyUp: false, isInjected);
				if (!isInjected)
					OnPhysicalKeyDownSuppressed(vk);
				e.SuppressEvent = true;
				return;
			}

			if (ShouldConsumePlatformHotstringKeyDown(vk))
			{
				UpdateObservedPhysicalKeyState(vk, keyUp: false, isInjected);
				if (!isInjected)
					OnPhysicalKeyDownSuppressed(vk);
				e.SuppressEvent = true;
				return;
			}

			if (extraInfo == (ulong)KeyboardMouseSender.KeyBlockThis)
			{
				e.SuppressEvent = true;
				return;
			}

			if (!isInjected && grabbedByHotstring)
				ForceReleaseEndKeyX11(vk);

			var result = LowLevelCommon(e, vk, sc, sc, keyUp: false, extraInfo, isInjected ? HOOK_EVENT_INJECTED : 0);
			NormalizeUnsuppressableModifierState(vk, sc, keyUp: false, isInjected, wasGrabbed, result, extraInfo);
			var shouldReplayDown = result == 0 && wasGrabbed && !grabbedByHotstring && !isInjected;

			if (shouldReplayDown)
				ReplayGrabbedKey(keyCode, vk, sc, false);

			ApplyKeyStateAfterKeyboardDecision(vk, keyUp: false, isInjected, result, shouldReplayDown, wasGrabbed);

			if (result != 0)
			{
				if (!isInjected)
					OnPhysicalKeyDownSuppressed(vk);
				e.SuppressEvent = true;
			}
		}

		private void OnKeyReleased(object sender, KeyboardHookEventArgs e)
		{
			if (!keyboardEnabled) return;
			UpdateIndicatorSnapshotFromMask(e.RawEvent.Mask);

			var keyCode = e.Data.KeyCode;
			var vk = KeyCodes.SharpHookToVk(keyCode);
			if (vk == 0) return;

			lastHookEventWasKeyboard = true;
			lastKeyboardEventVk = vk;
			var sc = (uint)e.RawEvent.Keyboard.RawCode;
			var wasGrabbed = WasKeyGrabbed(e, vk, keyUp: true, out var grabbedByHotstring);
			var isInjected = MarkSimulatedIfNeeded(e, vk, keyCode, true, out ulong extraInfo);
			extraInfo = ComputeExtraInfo(extraInfo, isInjected || e.IsEventSimulated);

			if (ShouldSuppressForBlockInput(isInjected))
			{
				UpdateObservedPhysicalKeyState(vk, keyUp: true, isInjected);
				ClearLogicalKeyIfNeeded(vk, isInjected);
				e.SuppressEvent = true;
				return;
			}

			if (ShouldConsumePlatformHotstringKeyUp(vk, isInjected))
			{
				UpdateObservedPhysicalKeyState(vk, keyUp: true, isInjected);
				ClearLogicalKeyIfNeeded(vk, isInjected);
				e.SuppressEvent = true;
				return;
			}

			if (extraInfo == (ulong)KeyboardMouseSender.KeyBlockThis)
			{
				e.SuppressEvent = true;
				return;
			}

			var result = LowLevelCommon(e, vk, sc, sc, keyUp: true, extraInfo, isInjected ? HOOK_EVENT_INJECTED : 0);
			NormalizeUnsuppressableModifierState(vk, sc, keyUp: true, isInjected, wasGrabbed, result, extraInfo);
			var shouldReplayUp = result == 0
				&& !grabbedByHotstring
				&& !isInjected
				&& (wasGrabbed || kbdMsSender.IsKeyGrabSuspendedForReplay(vk));

			if (shouldReplayUp)
				ReplayGrabbedKey(keyCode, vk, sc, true);

			ApplyKeyStateAfterKeyboardDecision(vk, keyUp: true, isInjected, result, shouldReplayUp, wasGrabbed);

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

		protected virtual bool ShouldConsumePlatformHotstringKeyDown(uint vk) => false;

		protected virtual bool ShouldConsumePlatformHotstringKeyUp(uint vk, bool isInjected) => false;

		protected virtual void TrackPlatformHotstringTrigger(uint triggerVk)
		{
		}

		protected virtual void AddPlatformHotstringArmEnds(HotstringManager hm, ReadOnlySpan<char> hsBuf, HashSet<char> ends)
		{
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
			if (!e.IsEventSimulated)
			{
				var script = Script.TheScript;
				script.timeLastInputPhysical = script.timeLastInputMouse = DateTime.UtcNow;
			}

			lastHookEventWasKeyboard = false;
			var vk = MapWheelVk(e);
			var sc = (uint)e.Data.Rotation;
			var isInjected = MarkSimulatedIfNeeded(e, vk, KeyCode.VcUndefined, false, out ulong extraInfo);
			extraInfo = ComputeExtraInfo(extraInfo, isInjected || e.IsEventSimulated);

			if (ShouldSuppressForBlockInput(isInjected))
			{
				e.SuppressEvent = true;
				return;
			}

			var result = LowLevelCommon(e, vk, sc, sc, keyUp: false, extraInfo, isInjected ? HOOK_EVENT_INJECTED : 0);
			if (result != 0)
				e.SuppressEvent = true;
		}

		// Whether physical mouse movement is currently being suppressed (BlockInput mouse-move / blockInput,
		// or an InputHook with VisibleMouseMove := false). Toggling it lets a platform actively stop the
		// cursor when suppressing the move event alone doesn't (macOS -- see OnMoveSuppressionChanged).
		// Touched only on the single SharpHook hook thread, so no synchronization is needed.
		private bool moveSuppressionActive;

		// Called when physical move suppression turns on/off. macOS decouples the cursor from the mouse so
		// it actually stops moving (its session-level event tap can't stop the OS-driven cursor); the base
		// is a no-op (Windows uses its own hook thread, the X11 hook can't suppress moves, and the Linux
		// inputd path blocks at the device level before reaching here).
		protected virtual void OnMoveSuppressionChanged(bool active) { }

		private void SetMoveSuppression(bool active)
		{
			if (active == moveSuppressionActive)
				return;

			moveSuppressionActive = active;
			OnMoveSuppressionChanged(active);
		}

		private void OnMouseMoved(object sender, MouseHookEventArgs e)
		{
			if (!mouseEnabled) return;
			UpdateIndicatorSnapshotFromMask(e.RawEvent.Mask);

			lastHookEventWasKeyboard = false;
			var suppress = false;

			if (!e.IsEventSimulated)
			{
				var script = Script.TheScript;
				script.timeLastInputPhysical = script.timeLastInputMouse = DateTime.UtcNow;

				// Confine the cursor to the active ClipCursor rectangle. Suppressing the move only hides it
				// from applications -- on macOS libuiohook's session-level tap can't stop the OS-driven
				// cursor (same limitation as VisibleMouseMove), so warp it back to the clamped edge. Unlike
				// the whole-screen freeze, clipping must keep the cursor tracking inside the rectangle, so
				// WarpCursor (which re-associates) is correct here rather than decoupling. This block is
				// reachable only on macOS in practice: on Linux the inputd path enforces the clip in
				// ProcessInputdMouseHook, and the X11/SharpHook fallback can't clip at all (CanClipCursor).
				if (CursorClipActive)
				{
					int cx = e.Data.X, cy = e.Data.Y;

					if (ClampToCursorClip(ref cx, ref cy))
					{
						e.SuppressEvent = true;
						WarpCursor(cx, cy);
						return;
					}
				}

				if (script.KeyboardData.blockMouseMove || script.KeyboardData.blockInput)
				{
					e.SuppressEvent = true;
					suppress = true;
				}
			}

			// Notify any active InputHook(s) of movement; CollectMouseMove returns false to suppress when
			// VisibleMouseMove is off. This is the SharpHook/libuiohook fallback path (used when the
			// inputd daemon is unavailable), where e.Data.X/Y are absolute screen coordinates -- unlike
			// the inputd path (ProcessInputdMouseHook), which forwards relative deltas for a relative
			// mouse and screen-normalised coordinates for an absolute pointer.
			if (Script.TheScript.input != null
					&& !CollectMouseMove(ComputeExtraInfo(0, e.IsEventSimulated), e.Data.X, e.Data.Y, null))
			{
				e.SuppressEvent = true;
				suppress = true;
			}

			// Track the suppression state off physical events only (a simulated move shouldn't flip it),
			// so the platform can freeze/release the cursor on the on/off transition.
			if (!e.IsEventSimulated)
				SetMoveSuppression(suppress);
		}

		private void OnMousePressed(object sender, MouseHookEventArgs e)
		{
			if (!mouseEnabled) return;
			UpdateIndicatorSnapshotFromMask(e.RawEvent.Mask);
			if (!e.IsEventSimulated)
			{
				var script = Script.TheScript;
				script.timeLastInputPhysical = script.timeLastInputMouse = DateTime.UtcNow;
			}

			lastHookEventWasKeyboard = false;
			var vk = MapMouseVk(e.Data.Button);
			var sc = 0u;
			var isInjected = MarkSimulatedIfNeeded(e, vk, KeyCode.VcUndefined, false, out ulong extraInfo);
			extraInfo = ComputeExtraInfo(extraInfo, isInjected || e.IsEventSimulated);

			if (ShouldSuppressForBlockInput(isInjected))
			{
				e.SuppressEvent = true;
				return;
			}

			var result = LowLevelCommon(e, vk, sc, sc, keyUp: false, extraInfo, isInjected ? HOOK_EVENT_INJECTED : 0);
			if (result != 0)
				e.SuppressEvent = true;
		}

		private void OnMouseReleased(object sender, MouseHookEventArgs e)
		{
			if (!mouseEnabled) return;
			UpdateIndicatorSnapshotFromMask(e.RawEvent.Mask);
			if (!e.IsEventSimulated)
			{
				var script = Script.TheScript;
				script.timeLastInputPhysical = script.timeLastInputMouse = DateTime.UtcNow;
			}

			lastHookEventWasKeyboard = false;
			var vk = MapMouseVk(e.Data.Button);
			var sc = 0u;
			var isInjected = MarkSimulatedIfNeeded(e, vk, KeyCode.VcUndefined, true, out ulong extraInfo);
			extraInfo = ComputeExtraInfo(extraInfo, isInjected || e.IsEventSimulated);

			if (ShouldSuppressForBlockInput(isInjected))
			{
				e.SuppressEvent = true;
				return;
			}

			var result = LowLevelCommon(e, vk, sc, sc, keyUp: true, extraInfo, isInjected ? HOOK_EVENT_INJECTED : 0);
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
				return e.Data.Rotation > 0 ? VK_WHEEL_UP : VK_WHEEL_DOWN;
			return e.Data.Rotation > 0 ? VK_WHEEL_LEFT : VK_WHEEL_RIGHT;
		}

		private static readonly FieldInfo rawEventField = typeof(HookEventArgs).GetField("<RawEvent>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);

		protected static void SetRawEvent(HookEventArgs e, UioHookEvent raw)
		{
			rawEventField?.SetValue(e, raw);
		}

		internal void RegisterSyntheticEvent(KeyCode keyCode, bool keyUp, DateTime ms, long extraInfo, uint vk = 0)
		{
			if (!UseSyntheticEventQueue)
				return;

			if (keyCode == KeyCode.VcUndefined)
				return;
			syntheticEventQueue.Register(keyCode, vk, keyUp, ms, extraInfo, KeepSyntheticDownTokensForRepeats);
		}

		protected bool ConsumeSyntheticEvent(KeyCode keyCode, uint vk, bool keyUp, DateTimeOffset ms, out ulong extraInfo)
		{
			var consumed = syntheticEventQueue.TryConsume(
				keyCode,
				vk,
				keyUp,
				ms,
				SyntheticEventTimeoutMs,
				SyntheticKeyUpTimeoutMs,
				SyntheticEventFutureToleranceMs,
				KeepSyntheticDownTokensForRepeats,
				out extraInfo);

			return consumed;
		}

		protected bool ConsumeSyntheticEventByKey(KeyCode keyCode, uint vk, bool keyUp, DateTimeOffset ms, out ulong extraInfo)
		{
			var consumed = syntheticEventQueue.TryConsumeByKey(
				keyCode,
				vk,
				keyUp,
				KeepSyntheticDownTokensForRepeats,
				out extraInfo);

			return consumed;
		}

		protected bool TryGetHeldSyntheticDownOwnerExtra(KeyCode keyCode, uint vk, out ulong extraInfo)
			=> syntheticEventQueue.TryGetHeldSyntheticDownOwnerExtra(keyCode, vk, out extraInfo);

		protected virtual bool KeepSyntheticDownTokensForRepeats => false;

		private void SuppressHotkeyRelease(uint vk)
		{
			if (vk == 0) return;
			lock (suppressedHotkeyReleases)
			{
				suppressedHotkeyReleases.TryGetValue(vk, out var curr);
				var next = curr + 1;
				suppressedHotkeyReleases[vk] = next;
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

		protected virtual bool MarkSimulatedIfNeeded(HookEventArgs e, uint vk, KeyCode keyCode, bool keyUp, out ulong extraInfo)
		{
			var mask = e.RawEvent.Mask;
			var simulated = (mask & EventMask.SimulatedEvent) != 0;
			extraInfo = 0;
			var skipQueueMatch = ShouldSkipSyntheticQueueMatch(vk, keyUp);

			if (UseSyntheticEventQueue
				&& !skipQueueMatch
				&& ConsumeSyntheticEvent(keyCode, vk, keyUp, e.EventTime, out extraInfo))
			{
				var raw = e.RawEvent;
				raw.Mask |= EventMask.SimulatedEvent;
				SetRawEvent(e, raw);
				simulated = true;
			}
			else if (UseSyntheticEventQueue
				&& simulated
				&& !skipQueueMatch
				&& ConsumeSyntheticEventByKey(keyCode, vk, keyUp, e.EventTime, out extraInfo))
			{
			}

			if (simulated && extraInfo == 0)
        		extraInfo = (ulong)KeyboardMouseSender.KeyIgnoreAllExceptModifier;

			return simulated;
		}

		protected virtual bool ShouldSkipSyntheticQueueMatch(uint vk, bool keyUp) => false;

		private ulong ComputeExtraInfo(ulong extraInfo, bool isSimulated)
		{
			if (extraInfo != 0)
				return extraInfo;

			// On Linux we rely on synthetic-token matching for injected classification.
			// Tagging all hook events during sendInProgress as ignored can mask real physical input
			// that overlaps an outgoing send (e.g. rapid remap loops like ^a::b).
			if (isSimulated || (sendInProgress && !UseSyntheticEventQueue))
				return (ulong)KeyboardMouseSender.KeyIgnoreAllExceptModifier;

			return 0;
		}

		private bool WasKeyGrabbed(HookEventArgs e, uint vk, bool keyUp, out bool grabbedByHotstring)
		{
			return WasKeyGrabbedPlatform(e, vk, keyUp, out grabbedByHotstring);
		}

		protected virtual bool WasKeyGrabbedPlatform(HookEventArgs e, uint vk, bool keyUp, out bool grabbedByHotstring)
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

				if (keyUp)
				{
					kbdMsSender.SendKeyEventForHookReplay(KeyEventTypes.KeyUp, vk, sc, KeyboardMouseSender.KeyIgnore);

					lock (replayedGrabbedDown)
						replayedGrabbedDown.Remove(vk);

					if (vk < logicalKeyState.Length)
						logicalKeyState[vk] = 0;

					return;
				}

				bool repeatedDown;

				lock (replayedGrabbedDown)
					repeatedDown = !replayedGrabbedDown.Add(vk);

				if (repeatedDown)
				{
					// Grabbed repeat comes in as consecutive key-down notifications.
					// Emit an up->down transition so the target app receives each repeat.
					kbdMsSender.SendKeyEventForHookReplay(KeyEventTypes.KeyUp, vk, sc, KeyboardMouseSender.KeyIgnore);
					kbdMsSender.SendKeyEventForHookReplay(KeyEventTypes.KeyDown, vk, sc, KeyboardMouseSender.KeyIgnore);
				}
				else if (IsX11Available)
				{
					// On X11 the physical key may already be logically down even when grabbed,
					// so replay the initial press as up->down to force a visible keypress.
					kbdMsSender.SendKeyEventForHookReplay(KeyEventTypes.KeyUp, vk, sc, KeyboardMouseSender.KeyIgnore);
					kbdMsSender.SendKeyEventForHookReplay(KeyEventTypes.KeyDown, vk, sc, KeyboardMouseSender.KeyIgnore);
				}
				else
				{
					kbdMsSender.SendKeyEventForHookReplay(KeyEventTypes.KeyDown, vk, sc, KeyboardMouseSender.KeyIgnore);
				}

				if (vk < logicalKeyState.Length)
					logicalKeyState[vk] = StateDown;
			}

		private static bool IsScriptIgnoredExtraInfo(ulong extraInfo)
		{
			var info = unchecked((long)extraInfo);
			return info >= KeyIgnoreMin() && info <= KeyIgnoreMax;
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

		internal void DisarmHotstring()
		{
			if (!hsArmed)
				return;

			lock (hkLock)
			{
				DisarmHotstringPlatform();

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

				var triggerVk = lastHookEventWasKeyboard ? lastKeyboardEventVk : 0;
				var finalCharSuppressed = ready.doBackspace || ready.omitEndChar && ready.endCharRequired;
				var skipChars = ready.ComputeReplacementSkipChars(sspan, finalCharSuppressed, ref caseMode);

				if (triggerVk != 0)
					TrackPlatformHotstringTrigger(triggerVk);

				_ = PostMessage(new KeysharpMsg
				{
					message = (uint)UserMessages.AHK_HOTSTRING,
					obj = new HotstringMsg
					{
						hs = ready,
						caseMode = caseMode,
						endChar = endChar,
						skipChars = skipChars,
						triggerVk = triggerVk,
						recheckCriterionOnReceipt = HotkeyDefinition.HotCriterionRequiresReceiptReevaluation(ready.hotCriterion)
					}
				});
				ClearHotstringBuffer();

				DisarmHotstring();
				return;
			}

			// 2) Predict: append each possible completion char.
			// Shared Unix logic arms end-chars; platforms can add extra completion keys if needed.
			var ends = new HashSet<char>();
			var hsBufSpan = (ReadOnlySpan<char>)System.Runtime.InteropServices.CollectionsMarshal.AsSpan(hm.hsBuf);

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

			AddPlatformHotstringArmEnds(hm, hsBufSpan, ends);

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

		private static bool IsModifierKey(uint vk) => vk is
			VK_SHIFT or VK_LSHIFT or VK_RSHIFT or
			VK_CONTROL or VK_LCONTROL or VK_RCONTROL or
			VK_MENU or VK_LMENU or VK_RMENU or
			VK_LWIN or VK_RWIN;

		private void NormalizeUnsuppressableModifierState(uint vk, uint sc, bool keyUp, bool isInjected, bool wasGrabbed, nint result, ulong extraInfo)
		{
			// On X11, non-grabbed events can't actually be blocked. If the core logic decides to
			// suppress such a modifier event, ensure modifier logical tracking still follows reality.
			if (!IsX11Available || result == 0 || wasGrabbed || isInjected || !IsModifierKey(vk))
				return;

			UpdateKeybdState(sc, extraInfo, isArtificial: false, vk, sc, keyUp, isSuppressed: false);
		}

		private void UpdateObservedPhysicalKeyState(uint vk, bool keyUp, bool isInjected)
		{
			if (isInjected || IsModifierKey(vk) || vk == 0 || vk >= physicalKeyState.Length)
				return;

			physicalKeyState[vk] = (byte)(keyUp ? 0 : StateDown);
		}

		private void ClearLogicalKeyIfNeeded(uint vk, bool isInjected)
		{
			if (isInjected || IsModifierKey(vk) || vk == 0 || vk >= logicalKeyState.Length)
				return;

			logicalKeyState[vk] = 0;
		}

		internal void SetLogicalStateFromSyntheticSend(uint vk, bool isDown)
		{
			if (vk == 0 || vk >= logicalKeyState.Length)
				return;

			logicalKeyState[vk] = (byte)(isDown ? StateDown : 0);
		}

		private void ApplyKeyStateAfterKeyboardDecision(uint vk, bool keyUp, bool isInjected, nint result, bool replayed, bool wasGrabbed)
		{
			if (vk == 0 || IsModifierKey(vk))
				return;

			if (keyUp)
				OnPlatformKeyUpObserved(vk, isInjected);

			if (keyUp && !isInjected)
				OnPlatformPhysicalKeyUpObserved(vk);

			UpdateObservedPhysicalKeyState(vk, keyUp, isInjected);

			// Grabs only reach applications via explicit replay.
			var deliveredToApp = replayed || (result == 0 && !wasGrabbed);

			if (!isInjected)
			{
				// Physical (user-pressed) events: always update logicalKeyState to reflect
				// the real key state. This makes KeyWait and IsKeyDown work correctly even
				// for suppressed prefix keys (e.g. CapsLock & a:: suppresses CapsLock-down
				// but the script still needs KeyWait("CapsLock") to see it as held).
				if (vk < logicalKeyState.Length)
					logicalKeyState[vk] = (byte)(keyUp ? 0 : StateDown);
				return;
			}

			// Injected events: track only when delivered to the app so that a suppressed
			// synthetic key-down doesn't falsely report the key as logically held.
			if (deliveredToApp)
			{
				if (vk < logicalKeyState.Length)
					logicalKeyState[vk] = (byte)(keyUp ? 0 : StateDown);
				return;
			}

			// Force logical up on any consumed injected key-up to avoid stale down-state.
			if (keyUp && vk < logicalKeyState.Length)
				logicalKeyState[vk] = 0;
		}

		protected virtual void OnPlatformPhysicalKeyUpObserved(uint vk)
		{
		}

		// Called when a physical (non-injected) key-down is suppressed by the hook.
		// Lets platforms undo side effects the OS applies below the event tap
		// (e.g. the macOS HID driver toggles CapsLock before suppression takes effect).
		protected virtual void OnPhysicalKeyDownSuppressed(uint vk)
		{
		}

		protected virtual void OnPlatformKeyUpObserved(uint vk, bool isInjected)
		{
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
				&& KeyCodes.TryMapRuneToKeystroke(rune, out var mappedVk, out var s, out var g))
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
		private static short PhysicalInputLevel => (short)(Keysharp.Internals.Input.Keyboard.KeyboardMouseSender.SendLevelMax + 1);

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
			var extraInfo = (ulong)KeyboardMouseSender.KeyIgnoreLevel(eventLevel);
			var lParam = new nint(KeyboardUtils.MakeLong((short)sc, (short)eventLevel));

			if (!TryBuildHookHotkeyMessage(hotkeyId, extraInfo, null, null, out var hotkeyMsg))
				return;

			_ = PostMessage(new KeysharpMsg
			{
				message = (uint)UserMessages.AHK_HOOK_HOTKEY,
				wParam = new nint(hotkeyId),
				lParam = lParam,
				obj = hotkeyMsg
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

				if (vk < logicalKeyState.Length)
					return (logicalKeyState[vk] & StateDown) != 0;
			}

			// Fallback: query X11 logical state for any key.
			if (TryQueryKeyState(vk, out var isDown))
				return isDown;

			return false;
		}
		internal override bool IsKeyDownAsync(uint vk)
		{
			// Mirror Windows' GetAsyncKeyState: report OS-level key state.
			//
			// On the inputd path every key event — physical (evdev) and synthetic
			// (uinput, re-injected through the hook) — flows through UpdateKeybdState,
			// so modifiersLRLogical is always the complete, authoritative modifier state.
			// Querying X11/XWayland is wrong here: the evdev grab makes X11 blind to
			// physical key events, so it returns false for held modifiers and causes
			// GetModifierLRState's "wrongly down" correction to wipe valid state.
			// This manifests as ^p::, !a::, <#LAlt::, etc. never firing.
			//
			// Note: a future improvement could query inputd for a live snapshot so that
			// modifiers stuck across a temporary Send-ungrab window are also corrected.
			// For now the tiny race window is an accepted trade-off.
#if LINUX
			if (UsingInputdHooks)
			{
				var modMask = vk switch
				{
					VK_LCONTROL => MOD_LCONTROL,
					VK_RCONTROL => MOD_RCONTROL,
					VK_CONTROL  => MOD_LCONTROL | MOD_RCONTROL,
					VK_LSHIFT   => MOD_LSHIFT,
					VK_RSHIFT   => MOD_RSHIFT,
					VK_SHIFT    => MOD_LSHIFT | MOD_RSHIFT,
					VK_LMENU    => MOD_LALT,
					VK_RMENU    => MOD_RALT,
					VK_MENU     => MOD_LALT | MOD_RALT,
					VK_LWIN     => MOD_LWIN,
					VK_RWIN     => MOD_RWIN,
					_           => 0u
				};

				if (modMask != 0)
					return (kbdMsSender.modifiersLRLogical & modMask) != 0;

				// Non-modifier: physical snapshot (injected keys are not tracked here,
				// which is correct — callers like KeyWait want the physical state).
				if (vk < physicalKeyState.Length)
					return (physicalKeyState[vk] & StateDown) != 0;

				return false;
			}
#endif

			// Original path: X11 / SharpHook, then Wayland no-X11 fallback.
			// The hook's modifiersLRPhysical only tracks user-pressed keys, so using
			// it alone would cause GetModifierLRState's "wrongly down" correction
			// to wipe modifiersLRLogical bits set by a synth modifier press —
			// for example, the Shift put down by ShiftAltTab would be cleared
			// the next time any GetKeyState(..., "P") call runs.
			if (TryQueryKeyState(vk, out var isDown))
				return isDown;

			// Wayland / no-X11 fallback: trust the hook's tracking.
			if (HasKbdHook())
			{
				var modMask = vk switch
				{
					VK_LCONTROL => MOD_LCONTROL,
					VK_RCONTROL => MOD_RCONTROL,
					VK_CONTROL  => MOD_LCONTROL | MOD_RCONTROL,
					VK_LSHIFT   => MOD_LSHIFT,
					VK_RSHIFT   => MOD_RSHIFT,
					VK_SHIFT    => MOD_LSHIFT | MOD_RSHIFT,
					VK_LMENU    => MOD_LALT,
					VK_RMENU    => MOD_RALT,
					VK_MENU     => MOD_LALT | MOD_RALT,
					VK_LWIN     => MOD_LWIN,
					VK_RWIN     => MOD_RWIN,
					_           => 0u
				};

				if (modMask != 0)
					return (kbdMsSender.modifiersLRPhysical & modMask) != 0;

				if (vk < physicalKeyState.Length)
					return (physicalKeyState[vk] & StateDown) != 0;
			}

			return IsKeyDown(vk);
		}
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

		// Route hotstring collection through platform arming logic when available (Linux/X11).
		internal override bool CollectHotstring(ulong extraInfo, char[] ch, int charCount, nint activeWindow,
												KeyHistoryItem keyHistoryCurr, ref HotstringDefinition hsOut, ref CaseConformModes caseConformMode, ref char endChar, ref int skipChars)
		{
			if (charCount <= 0 || ch.Length == 0)
				return true;

			if (KeyboardMouseSender.IsIgnored(extraInfo))
				return true; // Ignore simulated/send input.

			var hm = Script.TheScript.HotstringManager;
			lastTypedExtraInfo = extraInfo;

			var result = base.CollectHotstring(extraInfo, ch, charCount, activeWindow, keyHistoryCurr, ref hsOut, ref caseConformMode, ref endChar, ref skipChars);

			// Keep Linux-side logging and arming in sync with the buffer maintained by the base collector.
			if (charCount > 0)
			{
				var c = ch[0];
				lastTypedChar = c;

				if (UsePlatformHotstringArming)
				{
					if (hsOut != null)
						DisarmHotstring();
					else
						RecomputeHotstringArming();
				}
			}

			return result;
		}

		internal override void SendHotkeyMessages(bool keyUp, ulong extraInfo, KeyHistoryItem keyHistoryCurr, uint hotkeyIDToPost, HotkeyVariant variant, HotstringDefinition hs, CaseConformModes caseConformMode, char endChar, int skipChars = 0, object eventInfo = null)
		{
			if (UsePlatformHotstringArming && hs != null)
				DisarmHotstring();

			var vk = keyHistoryCurr.vk;
			if (vk == 0 && lastHookEventWasKeyboard)
				vk = lastKeyboardEventVk;

			if (!keyUp && hs != null && vk != 0)
				TrackPlatformHotstringTrigger(vk);

			if (hotkeyIDToPost != HotkeyDefinition.HOTKEY_ID_INVALID)
			{
				if (vk != 0)
				{
					if (!keyUp && ShouldSuppressSuffixRelease(variant, vk, hotkeyIDToPost))
						SuppressHotkeyRelease(vk);
					activeHotkeyVk = vk;
					activeHotkeyDown = !keyUp;
				}
			}

			base.SendHotkeyMessages(keyUp, extraInfo, keyHistoryCurr, hotkeyIDToPost, variant, hs, caseConformMode, endChar, skipChars, eventInfo);
		}

		internal override void PrepareToSendHotstringReplacement(char endChar, uint triggerVk)
		{
			if (UsePlatformHotstringArming)
				DisarmHotstring();
		}

		internal override uint SC_LCONTROL => KeyCodes.MapVkToSc(VK_LCONTROL);
		internal override uint SC_RCONTROL => KeyCodes.MapVkToSc(VK_RCONTROL);
		internal override uint SC_LALT => KeyCodes.MapVkToSc(VK_LMENU);
		internal override uint SC_RALT => KeyCodes.MapVkToSc(VK_RMENU);
		internal override uint SC_LSHIFT => KeyCodes.MapVkToSc(VK_LSHIFT);
		internal override uint SC_RSHIFT => KeyCodes.MapVkToSc(VK_RSHIFT);
		internal override uint SC_LWIN => KeyCodes.MapVkToSc(VK_LWIN);
		internal override uint SC_RWIN => KeyCodes.MapVkToSc(VK_RWIN);

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
							var mapped = KeyCodes.MapScToVk(sc);
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
							var mapped = KeyCodes.MapScToVk(sc);
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
							var mapped = KeyCodes.MapScToVk(sc);
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

			return KeyCodes.MapScToVk(sc) switch
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

		internal override bool EarlyCollectInput(ulong extraInfo, uint rawSC, uint vk, uint sc, bool keyUp, bool isIgnored
										, CollectInputState state, KeyHistoryItem keyHistoryCurr, object eventInfo)
		// Returns true if the caller should treat the key as visible (non-suppressed).
		// Always use the parameter aVK rather than event.vkCode because the caller or caller's caller
		// might have adjusted aVK, such as to make it a left/right specific modifier key rather than a
		// neutral one. On the other hand, event.scanCode is the one we need for ToUnicodeEx() calls.
		{
			var script = Script.TheScript;
			state.earlyCollected = true;
			state.used_dead_key_non_destructively = false;
			state.charCount = 0;

			if (keyUp && !CollectKeyUp(extraInfo, vk, sc, true, eventInfo))
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
#if OSX
			// On macOS, resolving the focused window per keystroke is expensive (Accessibility
			// round-trips, sometimes a full CGWindowList snapshot) and would touch AppKit from this
			// background hook thread. For hotstring buffer reset we only need an identity that changes
			// when the typing context changes, so use the frontmost application instead — its PID is
			// tracked event-driven and cached, making this read effectively free. Tradeoff: switching
			// between two windows of the same app won't reset the buffer.
			var activeWindow = WindowManager.GetForegroundAppHandle();
#else
			var activeWindow = WindowManager.GetForegroundWindowHandle(); // Set default in case there's no focused control.
#endif
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
				// Unlike Windows, the Unix/macOS hooks read raw key events and translate them with our
				// own keyboard-layout mapping, so there is no shared OS keyboard buffer to avoid disturbing.
				// Dead-key composition state is therefore owned entirely by the layout provider (see
				// ToUnicodeWithDeadKeys), making the Windows replay/flush/no-modify dance unnecessary: the
				// provider buffers a dead key and composes it with the next key regardless of whether the
				// dead key was suppressed from the active window.
				var keyState = new byte[physicalKeyState.Length];
				// Provide the correct logical modifier and CapsLock state for the translation below.
				AdjustKeyState(keyState, kbdMsSender.modifiersLRLogical);
				keyState[VK_CAPITAL] = (byte)(IsKeyToggledOn(VK_CAPITAL) ? 1 : 0);
				System.Array.Clear(ch, 0, ch.Length);
				charCount = ToUnicodeWithDeadKeys(vk, rawSC, keyState, ch, 0, activeWindowKeybdLayout);

				if (charCount == 0 && (kbdMsSender.modifiersLRLogical & (MOD_LALT | MOD_RALT)) != 0 && (kbdMsSender.modifiersLRLogical & (MOD_LCONTROL | MOD_RCONTROL)) == 0u)
				{
					// Let the Alt state be ignored for Alt / Alt+Shift combinations (matches the M option
					// behavior: e.g. Win+E or Alt+letter still transcribes to the base letter).
					keyState[VK_MENU] = 0;
					keyState[VK_LMENU] = 0;
					keyState[VK_RMENU] = 0;
					System.Array.Clear(ch, 0, ch.Length);
					charCount = ToUnicodeWithDeadKeys(vk, rawSC, keyState, ch, 0, activeWindowKeybdLayout);
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

			if (!CollectInputHook(extraInfo, vk, sc, ch, charCount, true, eventInfo))
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
				return CollectKeyUp(extraInfo, vk, sc, true, eventInfo);

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
				&& KeyCodes.TryMapRuneToKeystroke(rune, out var vk, out var needShift, out var needAltGr))
			{
				uint mods = modifiersLr ?? 0;
				if (needShift) mods |= MOD_LSHIFT;
				if (needAltGr) mods |= MOD_RALT;
				modifiersLr = mods;
				return vk;
			}
			return 0;
		}

		private void ClearHotstringBuffer()
		{
			var hm = Script.TheScript.HotstringManager;
			hm.hsBuf.Clear();
		}

		internal void SetIndicatorSnapshot(bool capsOn, bool numOn, bool scrollOn)
		{
			indicatorSnapshot = new IndicatorSnapshot(capsOn, numOn, scrollOn);
			indicatorSnapshotValid = true;
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
