using Keysharp.Builtins;
#if OSX
using System.Runtime.InteropServices;
using static Keysharp.Internals.Input.Keyboard.VirtualKeys;

namespace Keysharp.Internals.Input.Unix
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

			// Carbon HIToolbox modifier-key-state bits (used to select between candidate runes per key,
			// and passed directly as UCKeyTranslate's modifierKeyState parameter).
			private const uint shiftKeyState = 0x02u; // shiftKey >> 8
			private const uint optionKeyState = 0x08u; // optionKey >> 8

			private const ushort kUCKeyActionDown = 0;

			private static readonly ushort[] candidateKeyCodes =
			{
				// 0x0A is the extra key found on ISO keyboards (e.g. between left shift and Z, or
				// near Enter), which many European layouts (incl. Nordic/Baltic) use for an
				// additional letter.
				0x0A,
				0x00,0x01,0x02,0x03,0x04,0x05,0x06,0x07,0x08,0x09,0x0B,0x0C,0x0D,0x0E,0x0F,
				0x10,0x11,0x12,0x13,0x14,0x15,0x16,0x17,0x18,0x19,0x1A,0x1B,0x1C,0x1D,0x1E,0x1F,
				0x20,0x21,0x22,0x23,0x24,0x25,0x26,0x27,0x28,0x29,0x2A,0x2B,0x2C,0x2D,0x2E,0x2F,
				0x30,0x31,0x32
			};

			private static readonly Dictionary<uint, ushort> vkToMacKeyCode = BuildVkToMacKeyCodeMap();

			private static Dictionary<uint, ushort> BuildVkToMacKeyCodeMap()
			{
				var map = new Dictionary<uint, ushort>();

				foreach (var keyCode in candidateKeyCodes)
					if (TryMapMacKeyCodeToVk(keyCode, out var vk))
						map[vk] = keyCode;

				return map;
			}

			private static readonly nint tisUnicodeLayoutKey = CreateCfString("TISPropertyUnicodeKeyLayoutData");
			private static readonly nint kTISNotifySelectedKeyboardInputSourceChanged = CreateCfString("TISNotifySelectedKeyboardInputSourceChanged");
			private const nint kCFNotificationSuspensionBehaviorDeliverImmediately = 4;

			// The OS posts the layout-change notification to a single distributed center, so a
			// single static instance pointer is sufficient to route the unmanaged callback back
			// to the provider that registered for it.
			private static MacCharMapperProvider monitoringInstance;
			private bool monitoringStarted;

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
						nint layoutPtr,
						Rune targetRune,
						uint modifiers,
						out uint foundVk)
					{
						foundVk = 0;

						foreach (var keyCode in candidateKeyCodes)
						{
							if (!TryTranslate(layoutPtr, keyCode, modifiers, out var translatedRune))
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

			public bool TryMapKeystrokeToRune(uint vk, bool shift, bool altGr, out Rune rune)
			{
				rune = default;

				lock (mapperLock)
				{
					if (!vkToMacKeyCode.TryGetValue(vk, out var keyCode))
						return false;

					var layoutPtr = GetCurrentKeyboardLayoutPtr();

					if (layoutPtr == nint.Zero)
						return false;

					if (layoutPtr != lastLayoutPtr)
					{
						cache.Clear();
						lastLayoutPtr = layoutPtr;
					}

					uint modifiers = 0;

					if (shift)
						modifiers |= shiftKeyState;

					if (altGr)
						modifiers |= optionKeyState;

					return TryTranslate(layoutPtr, keyCode, modifiers, out rune);
				}
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

			public void StartLayoutChangeMonitoring()
			{
				lock (mapperLock)
				{
					if (monitoringStarted)
						return;

					monitoringStarted = true;
					monitoringInstance = this;
				}

				// TIS/Carbon notification registration is most reliable on the UI thread.
				Script.InvokeOnUIThread(() =>
				{
					var center = CFNotificationCenterGetDistributedCenter();

					if (center == nint.Zero)
						return;

					unsafe
					{
						CFNotificationCenterAddObserver(
							center,
							nint.Zero,
							&OnSelectedKeyboardInputSourceChanged,
							kTISNotifySelectedKeyboardInputSourceChanged,
							nint.Zero,
							kCFNotificationSuspensionBehaviorDeliverImmediately);
					}
				});
			}

			[UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
			private static void OnSelectedKeyboardInputSourceChanged(nint center, nint observer, nint name, nint obj, nint userInfo) =>
			monitoringInstance?.ConfigureLayout(null, null, null, null, null);

			public nint GetCurrentKeymapHandle()
			{
				lock (mapperLock)
					return GetCurrentKeyboardLayoutPtr();
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
					0x0A => VK_OEM_102,    // ISO extra key (<> or \|)
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

			/// <summary>
			/// Resolves the character that pressing a key (with the given Carbon modifier-key
			/// state, e.g. <see cref="shiftKeyState"/>/<see cref="optionKeyState"/>) would produce
			/// under the given keyboard layout's Unicode key layout data, via UCKeyTranslate.
			/// Returns false for dead keys (no completed character without a follow-up keystroke).
			/// </summary>
			private static bool TryTranslate(nint layoutPtr, ushort keyCode, uint modifierState, out Rune rune)
			{
				var (success, result) = TryTranslateCore(layoutPtr, keyCode, modifierState);
				rune = result;
				return success;
			}

			private static unsafe (bool success, Rune rune) TryTranslateCore(nint layoutPtr, ushort keyCode, uint modifierState)
			{
				if (layoutPtr == nint.Zero)
					return (false, default);

				uint deadKeyState = 0;
				const int bufferLen = 8;
				var chars = stackalloc ushort[bufferLen];

				var status = UCKeyTranslate(
					layoutPtr,
					keyCode,
					kUCKeyActionDown,
					modifierState,
					0,
					0,
					&deadKeyState,
					bufferLen,
					out var actualLength,
					chars);

				if (status != 0 || actualLength == 0)
					return (false, default);

				var len = (int)Math.Min(actualLength, bufferLen);
				var translated = new string((char*)chars, 0, len);

				if (translated.Length == 1 && translated[0] == '�')
					return (false, default);

				return (Rune.TryGetRuneAt(translated, 0, out var rune), rune);
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

				// Prefer the actual current input source so non-ASCII characters (e.g. "ä" on an
				// Estonian layout) can be translated. The ASCII-capable source is only a fallback,
				// since it represents a US-like layout used for keyboard shortcuts and would
				// never contain non-ASCII characters.
				var source = TISCopyCurrentKeyboardLayoutInputSource();

				if (source == nint.Zero)
					source = TISCopyCurrentASCIICapableKeyboardLayoutInputSource();

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


			[LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
			private static partial nint CFNotificationCenterGetDistributedCenter();

			[DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
			private static unsafe extern void CFNotificationCenterAddObserver(
				nint center,
				nint observer,
				delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, void> callBack,
				nint name,
				nint object_,
				nint suspensionBehavior);

			[DllImport("/System/Library/Frameworks/Carbon.framework/Carbon")]
			private static extern unsafe int UCKeyTranslate(
				nint keyLayoutPtr,
				ushort virtualKeyCode,
				ushort keyAction,
				uint modifierKeyState,
				uint keyboardType,
				uint keyTranslateOptions,
				uint* deadKeyState,
				uint maxStringLength,
				out uint actualStringLength,
				ushort* unicodeString);
		}
	}
}
#endif
