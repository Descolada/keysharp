using Keysharp.Builtins;
#if WINDOWS
using Keysharp.Internals.Input.Hooks.Windows;
using Keysharp.Internals.Input.Keyboard;
using Keysharp.Internals.Input.Mouse;
using Keysharp.Internals.Input.Windows.Interception;
using static Keysharp.Internals.Os.Windows.WindowsAPI;

namespace Keysharp.Internals.Input.Windows
{
	/// <summary>
	/// Synthesis backend for <c>#UseHook Interception</c>. Subclasses <see cref="WindowsKeyboardMouseSender"/>
	/// rather than reimplementing the abstract KeyboardMouseSender contract from scratch: the array-building
	/// side (InitEventArray/PutKeybdEventIntoArray/PutMouseEventIntoArray/CleanupEventArray/ToggleKeyState/
	/// MouseCoordToAbs/dead-key-adjacent logic) is identical regardless of which primitive ultimately ships
	/// the built <c>INPUT</c> array -- only the actual OS "commit" call needs to change, which is entirely
	/// localized to <see cref="SendEventArray"/>. This mirrors what actually differs between the Win32
	/// hook chain and Interception at the hook side (see WindowsInterceptionHookThread): everything except
	/// the raw capture/injection primitive is shared.
	///
	/// Two things SendInput can do that Interception structurally cannot, both scoped out here rather than
	/// guessed at without a way to test them:
	///  - KEYEVENTF_UNICODE (arbitrary-Unicode-character injection, used by SendText/Unicode SendKeys) has no
	///    Interception equivalent (it operates at the raw scan-code/HID level). Falls back to a real
	///    SendInput call for just that one event; SendInput injects above the device stack Interception taps,
	///    so it will not loop back through this backend's own hook.
	///  - SendPlay (WH_JOURNALPLAYBACK) has no Interception equivalent either; falls back to the inherited
	///    base implementation unchanged.
	/// </summary>
	internal sealed class InterceptionKeyboardMouseSender : WindowsKeyboardMouseSender
	{
		private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
		private const uint KEYEVENTF_KEYUP = 0x0002;
		private const uint KEYEVENTF_UNICODE = 0x0004;

		private readonly WindowsInterceptionHookThread owner;

		internal InterceptionKeyboardMouseSender(WindowsInterceptionHookThread owner) => this.owner = owner;

		internal override void SendEventArray(ref long finalKeyDelay, uint modsDuringSend)
		{
			if (sendMode != SendModes.Input)
			{
				// SendPlay: no Interception equivalent (see class remarks) -- unmodified native behavior.
				base.SendEventArray(ref finalKeyDelay, modsDuringSend);
				return;
			}

			var context = owner.Context;

			if (context == 0)
			{
				// Hook/context not currently live (e.g. no hotkey/#InstallKeybdHook has requested a hook yet).
				// SendInput still works on its own regardless -- it injects above the device stack Interception
				// taps -- so fall back rather than silently dropping the send.
				base.SendEventArray(ref finalKeyDelay, modsDuringSend);
				return;
			}

			foreach (var input in eventSi)
			{
				if (input.type == INPUT_KEYBOARD && (input.i.k.dwFlags & KEYEVENTF_UNICODE) != 0)
				{
					var single = input;
					_ = SendInput(1, [single], Marshal.SizeOf(typeof(INPUT)));
					continue;
				}

				SendOneStroke(context, input);
			}
		}

		private void SendOneStroke(nint context, INPUT input)
		{
			var stroke = new InterceptionStroke();

			if (input.type == INPUT_KEYBOARD)
			{
				var k = input.i.k;
				// wScan carries the real scan code whenever Keysharp filled it in (KEYEVENTF_SCANCODE path);
				// otherwise derive it from the VK the same way the array was built for SendInput's benefit.
				var sc = k.wScan != 0 ? k.wScan : (ushort)KeyCodes.MapVkToSc(k.wVk);
				ushort state = 0;

				if ((k.dwFlags & KEYEVENTF_KEYUP) != 0)
					state |= (ushort)InterceptionKeyState.Up;

				if ((k.dwFlags & KEYEVENTF_EXTENDEDKEY) != 0 || sc > 0xFF)
					state |= (ushort)InterceptionKeyState.E0;

				stroke.key = new InterceptionKeyStroke { code = (ushort)(sc & 0xFF), state = state, information = 0 };
				_ = InterceptionApi.interception_send(context, owner.LastKeyboardDevice, ref stroke, 1);
			}
			else if (input.type == INPUT_MOUSE)
			{
				var m = input.i.m;
				var flags = (MOUSEEVENTF)m.dwFlags;
				ushort state = 0;
				short rolling = 0;

				if ((flags & MOUSEEVENTF.LEFTDOWN) != 0) state |= (ushort)InterceptionMouseState.LeftButtonDown;
				if ((flags & MOUSEEVENTF.LEFTUP) != 0) state |= (ushort)InterceptionMouseState.LeftButtonUp;
				if ((flags & MOUSEEVENTF.RIGHTDOWN) != 0) state |= (ushort)InterceptionMouseState.RightButtonDown;
				if ((flags & MOUSEEVENTF.RIGHTUP) != 0) state |= (ushort)InterceptionMouseState.RightButtonUp;
				if ((flags & MOUSEEVENTF.MIDDLEDOWN) != 0) state |= (ushort)InterceptionMouseState.MiddleButtonDown;
				if ((flags & MOUSEEVENTF.MIDDLEUP) != 0) state |= (ushort)InterceptionMouseState.MiddleButtonUp;

				if ((flags & MOUSEEVENTF.XDOWN) != 0)
					state |= (ushort)(Conversions.HighWord(unchecked((int)m.mouseData)) == MouseUtils.XBUTTON1
						? InterceptionMouseState.Button4Down : InterceptionMouseState.Button5Down);

				if ((flags & MOUSEEVENTF.XUP) != 0)
					state |= (ushort)(Conversions.HighWord(unchecked((int)m.mouseData)) == MouseUtils.XBUTTON1
						? InterceptionMouseState.Button4Up : InterceptionMouseState.Button5Up);

				if ((flags & MOUSEEVENTF.WHEEL) != 0) { state |= (ushort)InterceptionMouseState.Wheel; rolling = unchecked((short)m.mouseData); }
				if ((flags & MOUSEEVENTF.HWHEEL) != 0) { state |= (ushort)InterceptionMouseState.HWheel; rolling = unchecked((short)m.mouseData); }

				var moveFlags = (flags & MOUSEEVENTF.ABSOLUTE) != 0 ? InterceptionMouseFlag.MoveAbsolute : InterceptionMouseFlag.MoveRelative;

				stroke.mouse = new InterceptionMouseStroke
				{
					state = state,
					flags = (ushort)moveFlags,
					rolling = rolling,
					x = m.dx,
					y = m.dy,
					information = 0
				};
				_ = InterceptionApi.interception_send(context, owner.LastMouseDevice, ref stroke, 1);
			}
		}
	}
}
#endif
