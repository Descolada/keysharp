using Keysharp.Builtins;
#if WINDOWS
using Keysharp.Internals.Input.Keyboard;
using Keysharp.Internals.Input.Windows;
using Keysharp.Internals.Input.Windows.Interception;
using static Keysharp.Internals.Input.Keyboard.VirtualKeys;
using static Keysharp.Internals.Input.Keyboard.ScanCodes;
using static Keysharp.Internals.Os.Windows.WindowsAPI;

namespace Keysharp.Internals.Input.Hooks.Windows
{
	/// <summary>
	/// Opt-in hook/synthesis backend selected via <c>#UseHook Interception</c>. Captures keyboard/mouse
	/// strokes through the third-party Interception driver (https://github.com/oblitum/Interception) instead
	/// of WH_KEYBOARD_LL/WH_MOUSE_LL, giving two things the Win32 hook chain structurally can't: a stable
	/// per-physical-device id on every event (surfaced as A_EventInfo.DeviceId, which WH_KEYBOARD_LL never
	/// populates), and synthesized output other user-mode hooks see as real hardware rather than SendInput's
	/// always-injected events. Script.CreateHookThread only constructs this class after confirming the
	/// driver is actually installed, falling back to plain WindowsHookThread otherwise, so every method here
	/// assumes the driver is present.
	///
	/// Reuses WindowsHookThread's non-hook-installation logic unchanged (dead-key/ToUnicode composition,
	/// hotstring word-char rules, alt-tab menu handling, foreground-window queries, key-toggle state) by
	/// overriding only the four hook-installation primitives WindowsHookThread.ChangeHookState was factored
	/// to call (InstallKeyboardHook/UninstallKeyboardHook/InstallMouseHook/UninstallMouseHook), plus
	/// CreateKbdMsSender and CallNextHook.
	///
	/// Unlike SetWindowsHookEx's synchronous callback model, Interception is a blocking read loop
	/// (interception_wait/interception_receive/interception_send) -- the same shape LinuxHookThread already
	/// uses for keysharp-inputd, just via a direct DLL call instead of a socket. ReadLoop below is modeled
	/// closely on LinuxHookThread.InputdHookLoop/ProcessAndDecideHookEvent.
	/// </summary>
	internal sealed class WindowsInterceptionHookThread : WindowsHookThread
	{
		private readonly Lock lifecycleLock = new();
		private nint context;
		private CancellationTokenSource loopCancel;
		private Task loopTask;
		private bool keyboardWanted;
		private bool mouseWanted;

		protected override KeyboardMouseSender CreateKbdMsSender() => new InterceptionKeyboardMouseSender(this);

		// WindowsHookThread's override calls the real CallNextHookEx using e.Hook/e.Code/e.WParam/e.StructPtr,
		// which are meaningless here -- there is no live SetWindowsHookEx chain to continue. LowLevelCommon
		// (HookThread.cs) returns CallNextHook(e) verbatim as its own result; both Unix backends rely on the
		// same base-class no-op (nint.Zero) for the identical reason. "Pass" is signaled here by actually
		// re-sending the stroke via interception_send in ReadLoop below, not by this return value -- so the
		// convention becomes "LowLevelCommon returned non-zero" == suppressed, exactly matching how
		// LinuxHookThread.ProcessInputdKeyboardHook/ProcessInputdMouseHook already interpret the same call.
		protected internal override nint CallNextHook(HookEventArgs e) => nint.Zero;

		/// <summary>Live Interception context handle, or 0 when no hook is currently installed. Used by
		/// InterceptionKeyboardMouseSender to route synthesized strokes through the same driver session.</summary>
		internal nint Context { get { lock (lifecycleLock) return context; } }

		// The device index a synthesized stroke should target. Interception addresses a specific device
		// [1..20], unlike SendInput which is device-agnostic, so synthesis reuses whichever physical
		// keyboard/mouse last produced a real event -- making synthesized input appear to come from the same
		// device the user is actually using (the same convention tools like AutoHotInterception use), rather
		// than an arbitrary fixed index. Defaults to the first keyboard/mouse slot before any real event has
		// been observed.
		internal int LastKeyboardDevice { get; private set; } = 1;
		internal int LastMouseDevice { get; private set; } = InterceptionApi.MaxKeyboard + 1;

		protected override bool InstallKeyboardHook()
		{
			lock (lifecycleLock)
			{
				if (!EnsureContextAndLoopLocked())
					return false;

				keyboardWanted = true;
				ApplyFiltersLocked();
				kbdHook = 1; // Sentinel only (matches HasKbdHook()); Interception has no Win32 hook handle.
				return true;
			}
		}

		protected override bool UninstallKeyboardHook()
		{
			lock (lifecycleLock)
			{
				keyboardWanted = false;
				ApplyFiltersLocked();
				MaybeTearDownLocked();
				return true;
			}
		}

		protected override bool InstallMouseHook()
		{
			lock (lifecycleLock)
			{
				if (!EnsureContextAndLoopLocked())
					return false;

				mouseWanted = true;
				ApplyFiltersLocked();
				mouseHook = 1;
				return true;
			}
		}

		protected override bool UninstallMouseHook()
		{
			lock (lifecycleLock)
			{
				mouseWanted = false;
				ApplyFiltersLocked();
				MaybeTearDownLocked();
				return true;
			}
		}

		private bool EnsureContextAndLoopLocked()
		{
			if (context != 0 && loopTask != null && !loopTask.IsCompleted)
				return true;

			context = InterceptionApi.interception_create_context();

			if (context == 0)
				return false;

			loopCancel = new CancellationTokenSource();
			var token = loopCancel.Token;
			var ctx = context;
			loopTask = Task.Run(() => ReadLoop(ctx, token));
			return true;
		}

		private void ApplyFiltersLocked()
		{
			if (context == 0)
				return;

			InterceptionApi.interception_set_filter(context, InterceptionApi.interception_is_keyboard,
				(ushort)(keyboardWanted ? InterceptionFilterKeyState.All : InterceptionFilterKeyState.None));
			InterceptionApi.interception_set_filter(context, InterceptionApi.interception_is_mouse,
				(ushort)(mouseWanted
					? InterceptionFilterMouseState.All | InterceptionFilterMouseState.Move
					: InterceptionFilterMouseState.None));
		}

		private void MaybeTearDownLocked()
		{
			if (keyboardWanted || mouseWanted || context == 0)
				return;

			try { loopCancel?.Cancel(); } catch { }
			try { InterceptionApi.interception_destroy_context(context); } catch { }
			context = 0;
			loopCancel = null;
			loopTask = null;
		}

		private void ReadLoop(nint ctx, CancellationToken token)
		{
			var stroke = new InterceptionStroke();

			while (!token.IsCancellationRequested)
			{
				int device;

				try
				{
					device = InterceptionApi.interception_wait_with_timeout(ctx, 250);
				}
				catch (Exception ex)
				{
					if (!token.IsCancellationRequested)
						Ks.OutputDebugLine($"Interception hook reader stopped: {ex.Message}");

					return;
				}

				if (token.IsCancellationRequested)
					return;

				if (InterceptionApi.interception_is_invalid(device) != 0) // Timeout: nothing waiting.
					continue;

				if (InterceptionApi.interception_receive(ctx, device, ref stroke, 1) != 1)
					continue;

				bool pass;

				try
				{
					pass = InterceptionApi.interception_is_keyboard(device) != 0
						? ProcessKeyboardStroke(device, ref stroke)
						: InterceptionApi.interception_is_mouse(device) != 0 && ProcessMouseStroke(device, ref stroke);
				}
				catch (Exception ex)
				{
					Ks.OutputDebugLine($"Interception stroke processing failed: {ex}");
					pass = true; // Fail open rather than silently eating the user's keystroke/click.
				}

				if (pass)
				{
					try { _ = InterceptionApi.interception_send(ctx, device, ref stroke, 1); }
					catch (Exception ex) { Ks.OutputDebugLine($"Interception send failed: {ex.Message}"); }
				}
			}
		}

		private bool ProcessKeyboardStroke(int device, ref InterceptionStroke stroke)
		{
			LastKeyboardDevice = device;
			var ks = stroke.key;
			var keyUp = (ks.state & (ushort)InterceptionKeyState.Up) != 0;
			var sc = (uint)(ks.code & 0xFF);

			if ((ks.state & (ushort)InterceptionKeyState.E0) != 0)
				sc |= 0x100; // Same extended-key encoding WindowsHookThread.LowLevelKeybdHandler uses.

			var vk = KeyCodes.MapScToVk(sc);

			// Same left/right disambiguation LowLevelKeybdHandler applies to the OS-supplied vk; MapScToVk
			// alone doesn't reliably distinguish LCtrl/RCtrl or LAlt/RAlt (Windows' own VSC_TO_VK_EX has the
			// same ambiguity, which is exactly why that switch exists there too).
			switch (vk)
			{
				case VK_SHIFT: vk = sc == RShift ? VK_RSHIFT : VK_LSHIFT; break;
				case VK_CONTROL: vk = sc == RControl ? VK_RCONTROL : VK_LCONTROL; break;
				case VK_MENU: vk = sc == RAlt ? VK_RMENU : VK_LMENU; break;
			}

			if (vk == 0) // Unmapped scan code (e.g. a vendor multimedia key with no VK on this layout).
				return true;

			// flags only needs to encode up/down here -- the KeyboardHookEventArgs ctor derives EventType from
			// it, but LowLevelCommon's actual dispatch branches on the explicit keyUp parameter below (see
			// LowLevelKeybdHandler, which computes both from the same wParam for the same reason). Interception
			// has no WM_SYSKEYDOWN/UP-equivalent signal -- that classification happens above the raw device
			// layer this driver taps into -- so the SYS variants are never produced here.
			var args = new KeyboardHookEventArgs(0, vk, sc, keyUp ? WM_KEYUP : WM_KEYDOWN, false, 0, 0, 0,
				(uint)Environment.TickCount);
			var result = LowLevelCommon(args, vk, sc, sc, keyUp, extraInfo: 0, eventFlags: 0, deviceId: (uint)device);
			return result == 0;
		}

		private bool ProcessMouseStroke(int device, ref InterceptionStroke stroke)
		{
			LastMouseDevice = device;
			var ms = stroke.mouse;
			var state = (InterceptionMouseState)ms.state;

			if (state == 0)
				return ProcessMouseMove(ms);

			var (vk, keyUp) = state switch
			{
				InterceptionMouseState.LeftButtonDown => (VK_LBUTTON, false),
				InterceptionMouseState.LeftButtonUp => (VK_LBUTTON, true),
				InterceptionMouseState.RightButtonDown => (VK_RBUTTON, false),
				InterceptionMouseState.RightButtonUp => (VK_RBUTTON, true),
				InterceptionMouseState.MiddleButtonDown => (VK_MBUTTON, false),
				InterceptionMouseState.MiddleButtonUp => (VK_MBUTTON, true),
				InterceptionMouseState.Button4Down => (VK_XBUTTON1, false),
				InterceptionMouseState.Button4Up => (VK_XBUTTON1, true),
				InterceptionMouseState.Button5Down => (VK_XBUTTON2, false),
				InterceptionMouseState.Button5Up => (VK_XBUTTON2, true),
				InterceptionMouseState.Wheel => (ms.rolling < 0 ? VK_WHEEL_DOWN : VK_WHEEL_UP, false),
				InterceptionMouseState.HWheel => (ms.rolling < 0 ? VK_WHEEL_LEFT : VK_WHEEL_RIGHT, false),
				_ => (0u, true)
			};

			if (vk == 0)
				return true;

			// Buttons/wheel don't move the cursor, so an in-process GetCursorPos() (unlike Linux's cross-
			// process compositor query) is cheap and accurate here -- no reason to omit position the way
			// Linux does for the same event kinds. Called via the WindowsAPI-qualified name: this file's
			// explicit `using static WindowsAPI` and the project-wide `using static Platform.Pointer` (see
			// GlobalUsings.cs) both expose a same-shaped GetCursorPos, so qualifying picks the raw Win32 one
			// deliberately rather than leaning on using-directive priority rules.
			_ = WindowsAPI.GetCursorPos(out var pt);

			if (state is InterceptionMouseState.Wheel or InterceptionMouseState.HWheel)
			{
				var wheelArgs = new MouseWheelHookEventArgs(0, ms.rolling, 0, false, pt.X, pt.Y, 0, 0, 0,
					(uint)Environment.TickCount);
				var wheelResult = LowLevelCommon(wheelArgs, vk, (uint)ms.rolling, (uint)ms.rolling, keyUp: false,
					extraInfo: 0, eventFlags: 0, deviceId: (uint)device);
				return wheelResult == 0;
			}

			var eventType = keyUp ? EventType.MouseReleased : EventType.MousePressed;
			var args = new MouseHookEventArgs(eventType, 0, 0, false, pt.X, pt.Y, 0, 0, 0, (uint)Environment.TickCount);
			var result = LowLevelCommon(args, vk, 0, 0, keyUp, extraInfo: 0, eventFlags: 0, deviceId: (uint)device);
			return result == 0;
		}

		private int absScreenLeft, absScreenTop, absScreenWidth, absScreenHeight;
		private long absScreenDimsTicks;

		/// <summary>
		/// Handles a pure-movement stroke (no button/wheel bits set). Mirrors
		/// LinuxHookThread.ProcessInputdMouseHook's WM_MOUSEMOVE case, for the same underlying reason:
		/// Interception (like evdev) reports relative deltas for a relative mouse, not the already-
		/// integrated absolute position WH_MOUSE_LL's pt field gives natively on the native backend.
		/// Per CollectMouseMove's documented coordinate contract (HookThread.cs), x/y are RELATIVE
		/// deltas here for a relative device -- exactly matching what the Linux inputd backend already
		/// does for the same class of device, not a gap relative to it.
		/// </summary>
		private bool ProcessMouseMove(InterceptionMouseStroke ms)
		{
			// Unlike SendInput (which re-enters WH_MOUSE_LL), a stroke this backend sends via
			// interception_send does not loop back into this context's own interception_wait/receive, so
			// every stroke reaching ProcessMouseStroke/ProcessMouseMove originated from real hardware --
			// there is no injected-vs-physical distinction to make here the way the native and inputd
			// backends need (isInjected is implicitly always false).
			if (CursorClipActive)
			{
				_ = WindowsAPI.GetCursorPos(out var clipPos);
				int cx = clipPos.X, cy = clipPos.Y;

				if (ClampToCursorClip(ref cx, ref cy))
				{
					_ = Platform.Mouse.TryMoveAbsolute(cx, cy);
					return false; // Suppress the original move; the cursor was already placed directly.
				}
			}

			var moveBlocked = Script.TheScript.KeyboardData.blockMouseMove;

			if (AnyInputWantsMouseMove())
			{
				int moveX = ms.x, moveY = ms.y;

				if ((ms.flags & (ushort)InterceptionMouseFlag.MoveAbsolute) != 0)
					NormalizeAbsoluteToScreen(ref moveX, ref moveY);

				if (!CollectMouseMove(0, moveX, moveY, null))
					moveBlocked = true;
			}

			return !moveBlocked;
		}

		/// <summary>
		/// Converts an INTERCEPTION_MOUSE_MOVE_ABSOLUTE stroke's [0,65535]-normalized coordinate into a
		/// screen pixel coordinate -- the same convention SendInput's MOUSEEVENTF_ABSOLUTE uses (see the
		/// inverse conversion in WindowsKeyboardMouseSender.MouseCoordToAbs). Rare in practice (VM/tablet
		/// pointing devices); virtual-desktop bounds are cached briefly since GetSystemMetrics is a cross-
		/// process query, mirroring LinuxHookThread.TryGetVirtualDesktopSize's caching for the same reason.
		/// </summary>
		private void NormalizeAbsoluteToScreen(ref int x, ref int y)
		{
			var now = Environment.TickCount64;

			if (now - absScreenDimsTicks >= 1000 || absScreenWidth <= 0 || absScreenHeight <= 0)
			{
				absScreenLeft = GetSystemMetrics(SystemMetric.SM_XVIRTUALSCREEN);
				absScreenTop = GetSystemMetrics(SystemMetric.SM_YVIRTUALSCREEN);
				absScreenWidth = GetSystemMetrics(SystemMetric.SM_CXVIRTUALSCREEN);
				absScreenHeight = GetSystemMetrics(SystemMetric.SM_CYVIRTUALSCREEN);
				absScreenDimsTicks = now;
			}

			if (absScreenWidth <= 0 || absScreenHeight <= 0)
				return; // Metrics unavailable: leave x/y as the raw [0,65535]-range value.

			x = absScreenLeft + (int)((long)x * absScreenWidth / 65536);
			y = absScreenTop + (int)((long)y * absScreenHeight / 65536);
		}
	}
}
#endif
