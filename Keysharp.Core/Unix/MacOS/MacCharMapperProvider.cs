#if OSX
using System.Runtime.InteropServices;
using static Keysharp.Core.Common.Keyboard.VirtualKeys;

namespace Keysharp.Core.Unix
{
	partial class UnixKeyboardMouseSender
	{
		private sealed partial class MacCharMapperProvider : IUnixCharMapperProvider
		{
			private readonly Lock mapperLock = new();
			private readonly Dictionary<int, (uint vk, bool needShift, bool needAltGr)> cache = new();
			private nint lastLayoutPtr;
			private nint retainedLayoutDataRef;
			private nint retainedLayoutPtr;

			// Carbon HIToolbox constants used by UCKeyTranslate.
			private const ushort kUCKeyActionDown = 0;
			private const uint kUCKeyTranslateNoDeadKeysBit = 1u;
			private const uint shiftKeyState = 0x02u; // shiftKey >> 8
			private const uint optionKeyState = 0x08u; // optionKey >> 8

			private static readonly ushort[] candidateKeyCodes =
			{
				0x00,0x01,0x02,0x03,0x04,0x05,0x06,0x07,0x08,0x09,0x0B,0x0C,0x0D,0x0E,0x0F,
				0x10,0x11,0x12,0x13,0x14,0x15,0x16,0x17,0x18,0x19,0x1A,0x1B,0x1C,0x1D,0x1E,0x1F,
				0x20,0x21,0x22,0x23,0x24,0x25,0x26,0x27,0x28,0x29,0x2A,0x2B,0x2C,0x2D,0x2E,0x2F,
				0x30,0x31,0x32
			};

			private static readonly nint tisUnicodeLayoutKey = CreateCfString("TISPropertyUnicodeKeyLayoutData");

			public bool TryMapRuneToKeystroke(Rune rune, out uint vk, out bool needShift, out bool needAltGr)
			{
				lock (mapperLock)
				{
					var key = rune.Value;

					var layoutPtr = GetCurrentKeyboardLayoutPtr();
					if (layoutPtr == nint.Zero)
					{
						needAltGr = false;
						return TextFastMapUS.TryMapCharToVkShift_US(rune, out vk, out needShift);
					}

					if (layoutPtr != lastLayoutPtr)
					{
						cache.Clear();
						lastLayoutPtr = layoutPtr;
					}

					if (cache.TryGetValue(key, out var hit))
					{
						vk = hit.vk;
						needShift = hit.needShift;
						needAltGr = hit.needAltGr;
						return vk != 0;
					}

					// Prefer plain keys first (e.g. spacebar), then shifted, then AltGr combos.
					static bool TryFindForModifiers(
						nint ptr,
						Rune targetRune,
						uint modifiers,
						out uint foundVk)
					{
						foundVk = 0;

						foreach (var keyCode in candidateKeyCodes)
						{
							if (!TryTranslate(ptr, keyCode, modifiers, out var translatedRune))
								continue;

							if (translatedRune.Value != targetRune.Value)
								continue;

							if (!TryMapMacKeyCodeToVk(keyCode, out foundVk))
								continue;

							return true;
						}

						return false;
					}

					if (TryFindForModifiers(layoutPtr, rune, 0, out vk))
					{
						needShift = false;
						needAltGr = false;
						cache[key] = (vk, false, false);
						return true;
					}

					if (TryFindForModifiers(layoutPtr, rune, shiftKeyState, out vk))
					{
						needShift = true;
						needAltGr = false;
						cache[key] = (vk, true, false);
						return true;
					}

					if (TryFindForModifiers(layoutPtr, rune, optionKeyState, out vk))
					{
						needShift = false;
						needAltGr = true;
						cache[key] = (vk, false, true);
						return true;
					}

					if (TryFindForModifiers(layoutPtr, rune, shiftKeyState | optionKeyState, out vk))
					{
						needShift = true;
						needAltGr = true;
						cache[key] = (vk, true, true);
						return true;
					}

				}

				needAltGr = false;
				return TextFastMapUS.TryMapCharToVkShift_US(rune, out vk, out needShift);
			}

			public void ConfigureLayout(string rules, string model, string layout, string variant, string options)
			{
				lock (mapperLock)
				{
					cache.Clear();
					lastLayoutPtr = nint.Zero;
					ReleaseRetainedLayoutData();
				}
			}

			public void Dispose()
			{
				lock (mapperLock)
				{
					cache.Clear();
					lastLayoutPtr = nint.Zero;
					ReleaseRetainedLayoutData();
				}
			}

			private static bool TryMapMacKeyCodeToVk(ushort keyCode, out uint vk)
			{
				vk = keyCode switch
				{
					0x00 => (uint)'A',
					0x01 => (uint)'S',
					0x02 => (uint)'D',
					0x03 => (uint)'F',
					0x04 => (uint)'H',
					0x05 => (uint)'G',
					0x06 => (uint)'Z',
					0x07 => (uint)'X',
					0x08 => (uint)'C',
					0x09 => (uint)'V',
					0x0B => (uint)'B',
					0x0C => (uint)'Q',
					0x0D => (uint)'W',
					0x0E => (uint)'E',
					0x0F => (uint)'R',
					0x10 => (uint)'Y',
					0x11 => (uint)'T',
					0x12 => (uint)'1',
					0x13 => (uint)'2',
					0x14 => (uint)'3',
					0x15 => (uint)'4',
					0x16 => (uint)'6',
					0x17 => (uint)'5',
					0x18 => VK_OEM_PLUS,
					0x19 => (uint)'9',
					0x1A => (uint)'7',
					0x1B => VK_OEM_MINUS,
					0x1C => (uint)'8',
					0x1D => (uint)'0',
					0x1E => VK_OEM_6,      // ]
					0x1F => (uint)'O',
					0x20 => (uint)'U',
					0x21 => VK_OEM_4,      // [
					0x22 => (uint)'I',
					0x23 => (uint)'P',
					0x24 => VK_RETURN,
					0x25 => (uint)'L',
					0x26 => (uint)'J',
					0x27 => VK_OEM_7,      // '
					0x28 => (uint)'K',
					0x29 => VK_OEM_1,      // ;
					0x2A => VK_OEM_5,      // \
					0x2B => VK_OEM_COMMA,
					0x2C => VK_OEM_2,      // /
					0x2D => (uint)'N',
					0x2E => (uint)'M',
					0x2F => VK_OEM_PERIOD,
					0x30 => VK_TAB,
					0x31 => VK_SPACE,
					0x32 => VK_OEM_3,      // `
					0x33 => VK_BACK,
					_ => 0u
				};

				return vk != 0;
			}

			private static bool TryTranslate(nint layoutPtr, ushort keyCode, uint modifierState, out Rune rune)
			{
				rune = default;

				uint deadKeyState = 0;
				char[] chars = new char[8];
				var result = UCKeyTranslate(
					layoutPtr,
					keyCode,
					kUCKeyActionDown,
					modifierState,
					LMGetKbdType(),
					kUCKeyTranslateNoDeadKeysBit,
					ref deadKeyState,
					chars.Length,
					out var actualLength,
					chars);

				if (result != 0 || actualLength <= 0)
					return false;

				if (actualLength > chars.Length)
					actualLength = chars.Length;

				var translated = new string(chars, 0, actualLength);
				return Rune.TryGetRuneAt(translated, 0, out rune);
			}

			private nint GetCurrentKeyboardLayoutPtr()
			{
				if (retainedLayoutPtr != nint.Zero)
					return retainedLayoutPtr;

				// TIS/Carbon access is most reliable on the UI thread. Load once and cache.
				Script.InvokeOnUIThread(LoadLayoutDataCore);
				return retainedLayoutPtr;
			}

			private void LoadLayoutDataCore()
			{
				if (retainedLayoutPtr != nint.Zero)
					return;

				var source = TISCopyCurrentASCIICapableKeyboardLayoutInputSource();

				if (source == nint.Zero)
					source = TISCopyCurrentKeyboardLayoutInputSource();

				if (source == nint.Zero)
					return;

				try
				{
					var keyRef = GetTisUnicodeLayoutDataPropertyKey();
					if (keyRef == nint.Zero)
						return;

					var dataRef = TISGetInputSourceProperty(source, keyRef);
					if (dataRef == nint.Zero)
						return;

					// Guard against invalid native values before dereferencing CFData.
					if (CFGetTypeID(dataRef) != CFDataGetTypeID())
						return;

					var ptr = CFDataGetBytePtr(dataRef);
					if (ptr == nint.Zero)
						return;

					CFRetain(dataRef);
					retainedLayoutDataRef = dataRef;
					retainedLayoutPtr = ptr;
				}
				finally
				{
					CFRelease(source);
				}
			}

			private void ReleaseRetainedLayoutData()
			{
				if (retainedLayoutDataRef != nint.Zero)
				{
					CFRelease(retainedLayoutDataRef);
					retainedLayoutDataRef = nint.Zero;
				}

				retainedLayoutPtr = nint.Zero;
			}

			private static nint GetTisUnicodeLayoutDataPropertyKey()
			{
				return tisUnicodeLayoutKey;
			}

			private static nint CreateCfString(string value)
			{
				try
				{
					return CFStringCreateWithCString(nint.Zero, value, kCFStringEncodingUTF8);
				}
				finally
				{
				}
			}

			private const uint kCFStringEncodingUTF8 = 0x08000100;

			[LibraryImport("/System/Library/Frameworks/Carbon.framework/Carbon")]
			private static partial nint TISCopyCurrentKeyboardLayoutInputSource();

			[LibraryImport("/System/Library/Frameworks/Carbon.framework/Carbon")]
			private static partial nint TISCopyCurrentASCIICapableKeyboardLayoutInputSource();

			[LibraryImport("/System/Library/Frameworks/Carbon.framework/Carbon")]
			private static partial nint TISGetInputSourceProperty(nint inputSource, nint propertyKey);

			[LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
			private static partial void CFRelease(nint cfTypeRef);

			[LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
			private static partial nint CFRetain(nint cfTypeRef);

			[LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
			private static partial nint CFGetTypeID(nint cf);

			[LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
			private static partial nint CFDataGetTypeID();

			[LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", StringMarshalling = StringMarshalling.Utf8)]
			private static partial nint CFStringCreateWithCString(nint alloc, string cStr, uint encoding);

			[LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
			private static partial nint CFDataGetBytePtr(nint theData);

			[LibraryImport("/System/Library/Frameworks/Carbon.framework/Carbon")]
			private static partial uint LMGetKbdType();

			[DllImport("/System/Library/Frameworks/Carbon.framework/Carbon")]
			private static extern int UCKeyTranslate(
				nint keyLayoutPtr,
				ushort virtualKeyCode,
				ushort keyAction,
				uint modifierKeyState,
				uint keyboardType,
				uint keyTranslateOptions,
				ref uint deadKeyState,
				int maxStringLength,
				out int actualStringLength,
				[Out] char[] unicodeString);
		}
	}
}
#endif
