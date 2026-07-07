#if OSX
using System.Runtime.InteropServices;
using System.Text;
using Keysharp.Builtins;
using Keysharp.Internals.Input.Keyboard;
using Keysharp.Internals.Input.Hooks;
using Keysharp.Internals.Input.Hooks.MacOS;
using static Keysharp.Internals.Input.Keyboard.KeyboardMouseSender;
using static Keysharp.Internals.Input.Keyboard.KeyboardUtils;
using static Keysharp.Internals.Input.Keyboard.VirtualKeys;

namespace Keysharp.Internals.Input.MacOS
{
	internal static partial class MacNativeInput
	{
		private const string ApplicationServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
		private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

		internal const uint kCGEventSourceStateCombinedSessionState = 0;
		internal const uint kCGEventSourceStateHIDSystemState = 1;
		internal const uint kCGHIDEventTap = 0;
		internal const uint kCGSessionEventTap = 1;
		internal const uint kCGHeadInsertEventTap = 0;
		internal const uint kCGEventTapOptionDefault = 0;
		internal const uint kCGEventSourceUserData = 42;
		internal const uint kCGKeyboardEventKeycode = 9;

		internal const uint kCGEventKeyDown = 10;
		internal const uint kCGEventKeyUp = 11;
		internal const uint kCGEventFlagsChanged = 12;
		internal const uint kCGEventTapDisabledByTimeout = 0xFFFFFFFE;
		internal const uint kCGEventTapDisabledByUserInput = 0xFFFFFFFF;

		private const ulong kCGEventFlagMaskAlphaShift = 1UL << 16;
		private const ulong kCGEventFlagMaskShift = 1UL << 17;
		private const ulong kCGEventFlagMaskControl = 1UL << 18;
		private const ulong kCGEventFlagMaskAlternate = 1UL << 19;
		private const ulong kCGEventFlagMaskCommand = 1UL << 20;

		private static readonly nint runLoopDefaultMode = CreateRunLoopMode("kCFRunLoopDefaultMode");

		internal delegate nint CGEventTapCallBack(nint proxy, uint type, nint cgEvent, nint userInfo);

		[LibraryImport(ApplicationServices)]
		internal static partial nint CGEventCreateKeyboardEvent(nint source, ushort virtualKey, [MarshalAs(UnmanagedType.I1)] bool keyDown);

		[LibraryImport(ApplicationServices)]
		internal static partial nint CGEventSourceCreate(uint stateId);

		[LibraryImport(ApplicationServices)]
		internal static partial void CGEventPost(uint tap, nint cgEvent);

		[LibraryImport(ApplicationServices)]
		internal static partial void CGEventSetIntegerValueField(nint cgEvent, uint field, long value);

		[LibraryImport(ApplicationServices)]
		internal static partial long CGEventGetIntegerValueField(nint cgEvent, uint field);

		[LibraryImport(ApplicationServices)]
		internal static partial ulong CGEventGetFlags(nint cgEvent);

		[LibraryImport(ApplicationServices)]
		internal static partial void CGEventSetFlags(nint cgEvent, ulong flags);

		[LibraryImport(ApplicationServices)]
		internal static partial ulong CGEventSourceFlagsState(uint sourceState);

		[LibraryImport(ApplicationServices)]
		[return: MarshalAs(UnmanagedType.I1)]
		internal static partial bool CGEventSourceKeyState(uint sourceState, ushort virtualKey);

		[LibraryImport(ApplicationServices, StringMarshalling = StringMarshalling.Utf16)]
		internal static partial void CGEventKeyboardSetUnicodeString(nint cgEvent, long stringLength, string unicodeString);

		[DllImport(ApplicationServices)]
		internal static extern nint CGEventTapCreate(uint tap, uint place, uint options, ulong eventsOfInterest, CGEventTapCallBack callback, nint userInfo);

		[LibraryImport(ApplicationServices)]
		internal static partial void CGEventTapEnable(nint tap, [MarshalAs(UnmanagedType.I1)] bool enable);

		[LibraryImport(CoreFoundation)]
		internal static partial nint CFMachPortCreateRunLoopSource(nint allocator, nint port, nint order);

		[LibraryImport(CoreFoundation)]
		internal static partial nint CFRunLoopGetCurrent();

		[LibraryImport(CoreFoundation)]
		internal static partial void CFRunLoopAddSource(nint rl, nint source, nint mode);

		[LibraryImport(CoreFoundation)]
		internal static partial void CFRunLoopRemoveSource(nint rl, nint source, nint mode);

		[LibraryImport(CoreFoundation)]
		internal static partial void CFRunLoopRun();

		[LibraryImport(CoreFoundation)]
		internal static partial void CFRunLoopStop(nint rl);

		[LibraryImport(CoreFoundation)]
		internal static partial void CFRelease(nint cf);

		[LibraryImport(CoreFoundation, StringMarshalling = StringMarshalling.Utf8)]
		private static partial nint CFStringCreateWithCString(nint allocator, string cStr, uint encoding);

		internal static nint RunLoopDefaultMode => runLoopDefaultMode;

		internal static ulong KeyboardEventMask =>
			(1UL << (int)kCGEventKeyDown) |
			(1UL << (int)kCGEventKeyUp) |
			(1UL << (int)kCGEventFlagsChanged);

		private static nint CreateRunLoopMode(string value)
			=> CFStringCreateWithCString(nint.Zero, value, 0x08000100); // kCFStringEncodingUTF8

		internal static bool IsModifierVk(uint vk) =>
			vk is VK_LSHIFT or VK_RSHIFT or VK_LCONTROL or VK_RCONTROL or VK_LMENU or VK_RMENU or VK_LWIN or VK_RWIN or VK_CAPITAL;

		internal static bool IsKeyUpFromFlagsChanged(uint vk, ulong flags) => vk switch
		{
			VK_LSHIFT or VK_RSHIFT => (flags & kCGEventFlagMaskShift) == 0,
			VK_LCONTROL or VK_RCONTROL => (flags & kCGEventFlagMaskControl) == 0,
			VK_LMENU or VK_RMENU => (flags & kCGEventFlagMaskAlternate) == 0,
			VK_LWIN or VK_RWIN => (flags & kCGEventFlagMaskCommand) == 0,
			VK_CAPITAL => (flags & kCGEventFlagMaskAlphaShift) == 0,
			_ => false
		};

		internal static EventMask ToEventMask(ulong flags)
		{
			var mask = EventMask.None;

			if ((flags & kCGEventFlagMaskShift) != 0)
				mask |= EventMask.LeftShift | EventMask.RightShift;
			if ((flags & kCGEventFlagMaskControl) != 0)
				mask |= EventMask.LeftCtrl | EventMask.RightCtrl;
			if ((flags & kCGEventFlagMaskAlternate) != 0)
				mask |= EventMask.LeftAlt | EventMask.RightAlt;
			if ((flags & kCGEventFlagMaskCommand) != 0)
				mask |= EventMask.LeftMeta | EventMask.RightMeta;
			if ((flags & kCGEventFlagMaskAlphaShift) != 0)
				mask |= EventMask.CapsLock;

			return mask;
		}

		internal static void PostKeyboard(uint vk, bool keyDown, long extraInfo)
		{
			if (!KeyCodes.TryMapVkToMacCode(vk, out var keyCode))
				return;

			var source = CGEventSourceCreate(kCGEventSourceStateHIDSystemState);
			var ev = CGEventCreateKeyboardEvent(source, (ushort)keyCode, keyDown);

			if (ev != nint.Zero)
			{
				CGEventSetIntegerValueField(ev, kCGEventSourceUserData, extraInfo);
				CGEventPost(kCGHIDEventTap, ev);
				CFRelease(ev);
			}

			if (source != nint.Zero)
				CFRelease(source);
		}

		internal static void PostUnicodeText(string text, long extraInfo)
		{
			if (string.IsNullOrEmpty(text))
				return;

			foreach (var rune in text.EnumerateRunes())
				PostUnicodeScalar(rune.ToString(), extraInfo);
		}

		private static void PostUnicodeScalar(string scalar, long extraInfo)
		{
			var source = CGEventSourceCreate(kCGEventSourceStateHIDSystemState);

			void Post(bool down)
			{
				var ev = CGEventCreateKeyboardEvent(source, 0, down);
				if (ev == nint.Zero)
					return;

				CGEventKeyboardSetUnicodeString(ev, scalar.Length, scalar);
				CGEventSetIntegerValueField(ev, kCGEventSourceUserData, extraInfo);
				CGEventPost(kCGHIDEventTap, ev);
				CFRelease(ev);
			}

			Post(true);
			Post(false);

			if (source != nint.Zero)
				CFRelease(source);
		}
	}

	internal sealed class MacKeyboardMouseSender : Keysharp.Internals.Input.Unix.UnixKeyboardMouseSender
	{
		protected override void DispatchKeybdEvent(Keysharp.Internals.Input.Hooks.Unix.UnixHookThread lht, KeyEventTypes eventType, uint vk, long extraInfo)
		{
			WithSendScope(lht, () =>
			{
				EnsureInputSendPermission("send keyboard input");

				if (vk == VK_CAPITAL && eventType != KeyEventTypes.KeyUp && MacCapsLockState.TryToggle())
					return;

				switch (eventType)
				{
					case KeyEventTypes.KeyDown:
						MacNativeInput.PostKeyboard(vk, true, extraInfo);
						break;
					case KeyEventTypes.KeyUp:
						if (vk != VK_CAPITAL || !MacCapsLockState.IsAvailable)
							MacNativeInput.PostKeyboard(vk, false, extraInfo);
						break;
					case KeyEventTypes.KeyDownAndUp:
						MacNativeInput.PostKeyboard(vk, true, extraInfo);
						MacNativeInput.PostKeyboard(vk, false, extraInfo);
						break;
				}
			});
		}

		protected override bool TrySendPlatformUnicodeText(Keysharp.Internals.Input.Hooks.Unix.UnixHookThread lht, string text, long extraInfo)
		{
			WithSendScope(lht, () =>
			{
				EnsureInputSendPermission("send text input");
				MacNativeInput.PostUnicodeText(text, extraInfo);
			});

			return true;
		}
	}

	internal sealed class MacNativeEventTap : IDisposable
	{
		private readonly MacHookThread owner;
		private readonly MacNativeInput.CGEventTapCallBack callback;
		private readonly ManualResetEventSlim ready = new(false);
		private Thread thread;
		private nint tap;
		private nint source;
		private nint runLoop;
		private volatile bool stopping;

		internal MacNativeEventTap(MacHookThread owner)
		{
			this.owner = owner;
			callback = OnEvent;
		}

		internal bool Start(int timeoutMs)
		{
			if (thread != null)
				return true;

			stopping = false;
			ready.Reset();
			thread = new Thread(Run) { IsBackground = true, Name = "Keysharp macOS CGEvent tap" };
			thread.Start();
			return ready.Wait(timeoutMs) && tap != nint.Zero;
		}

		internal void Stop()
		{
			stopping = true;

			if (runLoop != nint.Zero)
				MacNativeInput.CFRunLoopStop(runLoop);

			if (thread != null && thread.IsAlive)
				_ = thread.Join(250);

			thread = null;
		}

		private void Run()
		{
			runLoop = MacNativeInput.CFRunLoopGetCurrent();
			tap = MacNativeInput.CGEventTapCreate(
				MacNativeInput.kCGSessionEventTap,
				MacNativeInput.kCGHeadInsertEventTap,
				MacNativeInput.kCGEventTapOptionDefault,
				MacNativeInput.KeyboardEventMask,
				callback,
				nint.Zero);

			if (tap == nint.Zero)
			{
				ready.Set();
				return;
			}

			source = MacNativeInput.CFMachPortCreateRunLoopSource(nint.Zero, tap, nint.Zero);
			if (source == nint.Zero)
			{
				ready.Set();
				return;
			}

			MacNativeInput.CFRunLoopAddSource(runLoop, source, MacNativeInput.RunLoopDefaultMode);
			ready.Set();
			MacNativeInput.CFRunLoopRun();

			if (source != nint.Zero)
			{
				MacNativeInput.CFRunLoopRemoveSource(runLoop, source, MacNativeInput.RunLoopDefaultMode);
				MacNativeInput.CFRelease(source);
				source = nint.Zero;
			}

			if (tap != nint.Zero)
			{
				MacNativeInput.CFRelease(tap);
				tap = nint.Zero;
			}

			runLoop = nint.Zero;
		}

		private nint OnEvent(nint proxy, uint type, nint cgEvent, nint userInfo)
		{
			if (type == MacNativeInput.kCGEventTapDisabledByTimeout || type == MacNativeInput.kCGEventTapDisabledByUserInput)
			{
				if (tap != nint.Zero)
					MacNativeInput.CGEventTapEnable(tap, true);
				return cgEvent;
			}

			if (stopping || cgEvent == nint.Zero)
				return cgEvent;

			var suppress = owner.ProcessNativeKeyboardEvent(type, cgEvent);
			return suppress ? nint.Zero : cgEvent;
		}

		public void Dispose()
		{
			Stop();
			ready.Dispose();
		}
	}
}
#endif
