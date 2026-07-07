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
using static Keysharp.Internals.Input.Keyboard.KeyboardUtils;
using static Keysharp.Internals.Input.Keyboard.VirtualKeys;
using Keysharp.Internals.Input.Mouse;
using static Keysharp.Internals.Input.Keyboard.KeyboardMouseSender;
using Keysharp.Internals.Input.Hooks;

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

		private string lastHookActivationFailure;

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

		protected readonly Lock hookStateLock = new();
		protected long hookStateGeneration;

		// Cached on/off
		protected volatile bool keyboardEnabled;
		protected volatile bool mouseEnabled;

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
		private uint activeHotkeyKc;
		private bool activeHotkeyDown;
		internal uint ActiveHotkeyVk => activeHotkeyVk;
		internal uint ActiveHotkeyKc => activeHotkeyKc;
		internal bool SendInProgress => sendInProgress;
		internal bool IsHotkeySuffixDown(uint vk) => activeHotkeyDown && activeHotkeyVk == vk;
		private readonly Dictionary<uint, int> suppressedHotkeyReleases = new();
		private uint lastKeyboardEventVk;
		private bool lastHookEventWasKeyboard;

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
		internal UnixHookThread() : base()
		{
			ConfigureScanCodeNames();
		}

		protected override KeyboardMouseSender CreateKbdMsSender()
		{
#if LINUX
			return new InputdKeyboardMouseSender();
#else
			return new UnixKeyboardMouseSender();
#endif
		}

#if LINUX
		private void EnsureInputdSender()
		{
			if (_kbdMsSender is null or InputdKeyboardMouseSender)
				return;

			InvalidateKbdMsSender();
			ResetTrackedInputState(clearSyntheticQueue: false);
		}
#endif

		internal SendScope EnterSendScope() => new(this);

		private void SyncModifiersAfterSend()
		{
			// Ensure modifiers don't stay logically down after Send manipulates them.
			// On the inputd path the modifier reconciliation is handled by
			// InputdKeyboardMouseSender.ReconcileLogicalModifiersFromOs (called from
			// SendEventArray after each bypass-hook batch), and by hook echoes for
			// Event-mode sends.
			_ = kbdMsSender?.GetModifierLRState(true);
		}

		protected void ResetTrackedInputState(bool clearSyntheticQueue)
		{
			System.Array.Clear(physicalKeyState, 0, physicalKeyState.Length);

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
				StopPlatformHookCore(dispose: true);
				kbdHook = 0;
				mouseHook = 0;
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

			var sc = KeyCodes.MapVkToSc(vk);
			if (sc == 0)
				sc = KeyCodes.MapVkToSc(vk);

			var e = new KeyboardHookEventArgs(EventType.KeyPressed, vk, sc);
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

#if LINUX
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
#endif

		protected virtual void ChangeHookStateLinux(HookType req, bool changeIsTemporary, long expectedGeneration)
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
					StopPlatformHookCore(dispose: true);
				}

				lastHookActivationFailure = "Linux hook disabled via KEYSHARP_DISABLE_HOOK=1.";
				Ks.OutputDebugLine(lastHookActivationFailure);
				return;
			}

			var wantKeyboard = (req & HookType.Keyboard) != 0;
			var wantMouse = (req & HookType.Mouse) != 0;

			lock (hookStateLock)
			{
				if (hookStateGeneration != expectedGeneration)
					return;

				var hadKeyboard = keyboardEnabled;
				var hadMouse = mouseEnabled;

				if (!wantKeyboard && !wantMouse)
				{
					keyboardEnabled = false;
					mouseEnabled = false;
					kbdHook = 0;
					mouseHook = 0;
					lastHookActivationFailure = null;

					if (!changeIsTemporary)
						StopPlatformHookCore(dispose: true);

					return;
				}

#if LINUX
				var inputdMessage = string.Empty;

				if (TryStartInputdHookCore(wantKeyboard, wantMouse, out inputdMessage))
				{
					if (!changeIsTemporary)
					{
						if (wantKeyboard && !hadKeyboard)
						{
							keyboardEnabled = false;
							kbdHook = 0;
							ResetHook(false, HookType.Keyboard, true);
						}

						if (wantMouse && !hadMouse)
						{
							mouseEnabled = false;
							mouseHook = 0;
							ResetHook(false, HookType.Mouse, true);
						}
					}

					keyboardEnabled = wantKeyboard;
					mouseEnabled = wantMouse;
					kbdHook = wantKeyboard ? 1 : 0;
					mouseHook = wantMouse ? 1 : 0;
					usingInputdHooks = true;
					lastHookActivationFailure = null;
					EnsureInputdSender();
					return;
				}

				lastHookActivationFailure = $"keysharp-inputd hook unavailable; global hooks disabled. {inputdMessage}";
				Ks.OutputDebugLine(lastHookActivationFailure);
				WarnIfWaylandHookUnavailable();
				usingInputdHooks = false;
#endif
				keyboardEnabled = false;
				mouseEnabled = false;
				kbdHook = 0;
				mouseHook = 0;
			}
		}

#if LINUX
		internal override string GetHookActivationFailureReason() => lastHookActivationFailure ?? "";
#endif

		protected void StopPlatformHookCore(bool dispose)
		{
			if (!dispose)
				return;

			ResetTrackedInputState(clearSyntheticQueue: false);
			ResetIndicatorSnapshot();
			SetMoveSuppression(false);
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

			}
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
			ResetTrackedInputState(clearSyntheticQueue: false);
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

			var now = Environment.TickCount64;

			if (now - lastInputdRecoveryTicks < InputdRecoveryWindowMs)
			{
				if (++inputdRecoveryAttempts >= MaxInputdRecoveryAttempts)
				{
					if (CursorClipActive)
						ClearCursorClip();

					Ks.OutputDebugLine($"keysharp-inputd hooks lost repeatedly; global hooks disabled: {reason}");
					return;
				}
			}
			else
			{
				inputdRecoveryAttempts = 1;
			}

			lastInputdRecoveryTicks = now;

			Ks.OutputDebugLine($"keysharp-inputd hook reader lost ({reason}); re-establishing hooks.");

			// Re-arm through keysharp-inputd. If reconnect fails, the hook state is left disabled.
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
			(uint)EventCode.Kp7 => VK_HOME,   // KEY_KP7   â†’ NumpadHome
			(uint)EventCode.Kp8 => VK_UP,     // KEY_KP8   â†’ NumpadUp
			(uint)EventCode.Kp9 => VK_PRIOR,  // KEY_KP9   â†’ NumpadPgUp
			(uint)EventCode.Kp4 => VK_LEFT,   // KEY_KP4   â†’ NumpadLeft
			(uint)EventCode.Kp5 => VK_CLEAR,  // KEY_KP5   â†’ NumpadClear
			(uint)EventCode.Kp6 => VK_RIGHT,  // KEY_KP6   â†’ NumpadRight
			(uint)EventCode.Kp1 => VK_END,    // KEY_KP1   â†’ NumpadEnd
			(uint)EventCode.Kp2 => VK_DOWN,   // KEY_KP2   â†’ NumpadDown
			(uint)EventCode.Kp3 => VK_NEXT,   // KEY_KP3   â†’ NumpadPgDn
			(uint)EventCode.Kp0 => VK_INSERT, // KEY_KP0   â†’ NumpadIns
			(uint)EventCode.KpDot => VK_DELETE, // KEY_KPDOT â†’ NumpadDel
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
			// KeyPhysIgnore is sent) so KeyWait/current-state queries and the physical modifier mask stay correct.
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

			var args = new KeyboardHookEventArgs(
				keyUp ? EventType.KeyReleased : EventType.KeyPressed,
				vk,
				sc,
				isInjected ? EventMask.SimulatedEvent : EventMask.None,
				ev.TimeMs);
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
			ApplyKeyStateAfterKeyboardDecision(vk, keyUp, isInjected, result);
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

			// The X11-vs-Wayland decision (and, on Wayland, the compositor-backend probe) is resolved once
			// inside Platform.Mouse — the hook thread just asks the resolved service whether it can both query
			// and move the cursor.
			if (!Platform.Mouse.SupportsCursorClip)
			{
				reason = "the cursor cannot be both queried and moved in this session";
				return false;
			}

			reason = "";
			return true;
		}

		// X11 (XWarpPointer, pixel-accurate) vs Wayland (compositor IPC) is resolved once inside Platform.Mouse.
		protected override void WarpCursor(int x, int y) => _ = Platform.Mouse.TryMoveAbsolute(x, y);

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

					// Forward movement to any active InputHook(s) and honor VisibleMouseMove. The inputd
					// path is the live backend on this host,
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
#endif

		private static bool ShouldDisableHook()
		{
			var env = Environment.GetEnvironmentVariable("KEYSHARP_DISABLE_HOOK");
			return !string.IsNullOrEmpty(env) &&
				   (env.Equals("1") || env.Equals("true", StringComparison.OrdinalIgnoreCase) || env.Equals("yes", StringComparison.OrdinalIgnoreCase));
		}

		// -------------------- event handlers --------------------

		// On platforms without an OS-level "block all input" API (e.g. macOS), BlockInput is
		// implemented by suppressing physical events at the hook level.
		// Injected (self-generated) events must still pass through so that Send/Click keep working
		// while BlockInput is active.
		private static bool ShouldSuppressForBlockInput(bool isInjected) =>
			!isInjected && Script.TheScript.KeyboardData.blockInput;

		protected void OnKeyPressed(object sender, KeyboardHookEventArgs e)
		{
			if (!keyboardEnabled) return;
			UpdateIndicatorSnapshotFromMask(e.Mask);

			var vk = e.Data.VkCode != 0 ? e.Data.VkCode : KeyCodes.MapScToVk(e.Data.RawCode);
			if (vk == 0) return;

			lastHookEventWasKeyboard = true;
			lastKeyboardEventVk = vk;
			var sc = (uint)e.Data.RawCode;
			var isInjected = MarkSimulatedIfNeeded(e, vk, false, out ulong extraInfo);
			extraInfo = ComputeExtraInfo(extraInfo, isInjected || e.IsEventSimulated);

			if (ShouldSuppressForBlockInput(isInjected))
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

			var result = LowLevelCommon(e, vk, sc, sc, keyUp: false, extraInfo, isInjected ? HOOK_EVENT_INJECTED : 0);
			ApplyKeyStateAfterKeyboardDecision(vk, keyUp: false, isInjected, result);

			if (result != 0)
			{
				if (!isInjected)
					OnPhysicalKeyDownSuppressed(vk);
				e.SuppressEvent = true;
			}
		}

		protected void OnKeyReleased(object sender, KeyboardHookEventArgs e)
		{
			if (!keyboardEnabled) return;
			UpdateIndicatorSnapshotFromMask(e.Mask);

			var vk = e.Data.VkCode != 0 ? e.Data.VkCode : KeyCodes.MapScToVk(e.Data.RawCode);
			if (vk == 0) return;

			lastHookEventWasKeyboard = true;
			lastKeyboardEventVk = vk;
			var sc = (uint)e.Data.RawCode;
			var isInjected = MarkSimulatedIfNeeded(e, vk, true, out ulong extraInfo);
			extraInfo = ComputeExtraInfo(extraInfo, isInjected || e.IsEventSimulated);

			if (ShouldSuppressForBlockInput(isInjected))
			{
				UpdateObservedPhysicalKeyState(vk, keyUp: true, isInjected);
				e.SuppressEvent = true;
				return;
			}

			if (extraInfo == (ulong)KeyboardMouseSender.KeyBlockThis)
			{
				e.SuppressEvent = true;
				return;
			}

			var result = LowLevelCommon(e, vk, sc, sc, keyUp: true, extraInfo, isInjected ? HOOK_EVENT_INJECTED : 0);
			ApplyKeyStateAfterKeyboardDecision(vk, keyUp: true, isInjected, result);

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

			// With no hook, fall back to the resolved platform keyboard state service.
			if (Platform.Keyboard.TryQueryModifierLRState(out var logicalMods))
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

		protected void OnMouseWheel(object sender, MouseWheelHookEventArgs e)
		{
			if (!mouseEnabled) return;
			UpdateIndicatorSnapshotFromMask(e.Mask);
			if (!e.IsEventSimulated)
			{
				var script = Script.TheScript;
				script.timeLastInputPhysical = script.timeLastInputMouse = DateTime.UtcNow;
			}

			lastHookEventWasKeyboard = false;
			var vk = MapWheelVk(e);
			var sc = (uint)e.Data.Rotation;
			var isInjected = MarkSimulatedIfNeeded(e, vk, false, out ulong extraInfo);
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
		// Touched only on the single native hook thread, so no synchronization is needed.
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

		protected void OnMouseMoved(object sender, MouseHookEventArgs e)
		{
			if (!mouseEnabled) return;
			UpdateIndicatorSnapshotFromMask(e.Mask);

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
				// ProcessInputdMouseHook.
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
			// VisibleMouseMove is off. On native hook paths, e.Data.X/Y are absolute screen coordinates.
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

		protected void OnMousePressed(object sender, MouseHookEventArgs e)
		{
			if (!mouseEnabled) return;
			UpdateIndicatorSnapshotFromMask(e.Mask);
			if (!e.IsEventSimulated)
			{
				var script = Script.TheScript;
				script.timeLastInputPhysical = script.timeLastInputMouse = DateTime.UtcNow;
			}

			lastHookEventWasKeyboard = false;
			var vk = MapMouseVk(e.Data.Button);
			var sc = 0u;
			var isInjected = MarkSimulatedIfNeeded(e, vk, false, out ulong extraInfo);
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

		protected void OnMouseReleased(object sender, MouseHookEventArgs e)
		{
			if (!mouseEnabled) return;
			UpdateIndicatorSnapshotFromMask(e.Mask);
			if (!e.IsEventSimulated)
			{
				var script = Script.TheScript;
				script.timeLastInputPhysical = script.timeLastInputMouse = DateTime.UtcNow;
			}

			lastHookEventWasKeyboard = false;
			var vk = MapMouseVk(e.Data.Button);
			var sc = 0u;
			var isInjected = MarkSimulatedIfNeeded(e, vk, true, out ulong extraInfo);
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

		protected virtual bool MarkSimulatedIfNeeded(HookEventArgs e, uint vk, bool keyUp, out ulong extraInfo)
		{
			extraInfo = 0;
			return e.IsEventSimulated;
		}

		private ulong ComputeExtraInfo(ulong extraInfo, bool isSimulated)
		{
			if (extraInfo != 0)
				return extraInfo;

			// On Linux we rely on synthetic-token matching for injected classification.
			// Tagging all hook events during sendInProgress as ignored can mask real physical input
			// that overlaps an outgoing send (e.g. rapid remap loops like ^a::b).
			if (isSimulated || (sendInProgress))
				return (ulong)KeyboardMouseSender.KeyIgnoreAllExceptModifier;

			return 0;
		}

		private static bool IsScriptIgnoredExtraInfo(ulong extraInfo)
		{
			var info = unchecked((long)extraInfo);
			return info >= KeyIgnoreMin() && info <= KeyIgnoreMax;
		}

		private static bool IsModifierKey(uint vk) => vk is
			VK_SHIFT or VK_LSHIFT or VK_RSHIFT or
			VK_CONTROL or VK_LCONTROL or VK_RCONTROL or
			VK_MENU or VK_LMENU or VK_RMENU or
			VK_LWIN or VK_RWIN;

		private void UpdateObservedPhysicalKeyState(uint vk, bool keyUp, bool isInjected)
		{
			if (isInjected || IsModifierKey(vk) || vk == 0 || vk >= physicalKeyState.Length)
				return;

			physicalKeyState[vk] = (byte)(keyUp ? 0 : StateDown);
		}

		private void ApplyKeyStateAfterKeyboardDecision(uint vk, bool keyUp, bool isInjected, nint result)
		{
			if (vk == 0 || IsModifierKey(vk))
				return;

			if (keyUp)
				OnPlatformKeyUpObserved(vk, isInjected);

			if (keyUp && !isInjected)
				OnPlatformPhysicalKeyUpObserved(vk);

			UpdateObservedPhysicalKeyState(vk, keyUp, isInjected);
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

		// -------------------- basic matcher â†’ AHK_HOOK_HOTKEY --------------------
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

		// -------------------- utilities & abstract impls --------------------

		internal override bool IsKeyToggledOn(uint vk)
		{
			if (TryQueryIndicatorStates(out var capsOn, out var numOn, out var scrollOn))
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

		internal override void SendHotkeyMessages(bool keyUp, ulong extraInfo, KeyHistoryItem keyHistoryCurr, uint hotkeyIDToPost, HotkeyVariant variant, HotstringDefinition hs, CaseConformModes caseConformMode, char endChar, int skipChars = 0, object eventInfo = null)
		{
			var vk = keyHistoryCurr.vk;
			if (vk == 0 && lastHookEventWasKeyboard)
				vk = lastKeyboardEventVk;

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
			var activeWindow = Keysharp.Internals.Window.MacOS.MacNativeWindows.ForegroundAppHandle;
#else
			var activeWindow = WindowQuery.GetForegroundWindowHandle(); // Set default in case there's no focused control.
#endif
			var activeWindowKeybdLayout = GetKeyboardLayout(0);
			state.activeWindow = activeWindow;
			state.keyboardLayout = activeWindowKeybdLayout;

			// SUMMARY OF DEAD KEY ISSUE:
			// Calling ToUnicodeEx() with conventional parameters disrupts the entry of dead keys in two different ways:
			//  1) Passing a dead key buffers it within the keyboard layout's internal state.
			//  2) Passing a live key removes any pending dead key from the keyboard layout's internal state.
			// In either case, the state is then incorrect for the active window's own call to ToUnicodeEx(), so it ends
			// up with something like "e" or "''e" instead of "Ã©".  Originally this was solved by reinserting the pending
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

		internal void SetIndicatorSnapshot(bool capsOn, bool numOn, bool scrollOn)
		{
			indicatorSnapshot = new IndicatorSnapshot(capsOn, numOn, scrollOn);
			indicatorSnapshotValid = true;
		}

		private bool TryGetIndicatorSnapshot(out bool capsOn, out bool numOn, out bool scrollOn)
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

		private bool TryQueryIndicatorStates(out bool capsOn, out bool numOn, out bool scrollOn)
		{
			// Avoid live platform calls from the inputd hook reader: the daemon already placed the
			// indicator state on the event, and a synchronous IPC query here can add hook latency.
			if (IsHookReaderThread && TryGetIndicatorSnapshot(out capsOn, out numOn, out scrollOn))
				return true;

			if (Platform.Keyboard.TryGetIndicatorStates(out capsOn, out numOn, out scrollOn))
			{
#if OSX
				// macOS exposes CapsLock natively, but NumLock/ScrollLock are only known from hook snapshots.
				if (TryGetIndicatorSnapshot(out _, out var snapNum, out var snapScroll))
				{
					numOn |= snapNum;
					scrollOn |= snapScroll;
				}
#endif
				return true;
			}

			return TryGetIndicatorSnapshot(out capsOn, out numOn, out scrollOn);
		}

	}
}
#endif
