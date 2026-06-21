using Keysharp.Builtins;
using System.Text;

namespace Keysharp.Internals.Input.Keyboard
{
	/// <summary>
	/// Single entry point for all key-code conversions in Keysharp. Fixed/table conversions
	/// (SharpHook <c>KeyCode</c>, X11 keysym, evdev, Mac kVK, US-ASCII, VK⇄SC) resolve directly;
	/// the layout-aware conversions delegate to a per-platform stateful provider singleton.
	///
	/// Terminology:
	///   VK  = Windows virtual key code (always, on every platform).
	///   SC  = the active hook backend's low-level code (Windows scan code, X11 XKeycode,
	///         inputd evdev code, or macOS kVK code).
	/// </summary>
	internal static partial class KeyCodes
	{
#if !WINDOWS
		/// <summary>Sentinel returned by the dead-key translation path when the provider cannot map the key.</summary>
		internal const int TranslateNotHandled = int.MinValue;

		private static readonly object providerLock = new();
		private static IKeyCodeMapperProvider provider;

		static KeyCodes()
		{
			AppDomain.CurrentDomain.ProcessExit += (_, __) =>
			{
				lock (providerLock)
				{
					provider?.Dispose();
					provider = null;
				}
			};
		}

		public static bool TryMapRuneToKeystroke(Rune rune, out uint vk, out bool needShift, out bool needAltGr)
		{
			EnsureProvider();

			if (provider != null && provider.TryMapRuneToKeystroke(rune, out vk, out needShift, out needAltGr))
				return true;

			needAltGr = false;
			return TryMapAsciiToVk(rune, out vk, out needShift);
		}

		/// <summary>
		/// Reverse of <see cref="TryMapRuneToKeystroke"/>: given a virtual key and the
		/// shift/AltGr state it would be pressed with, returns the character the active
		/// keyboard layout produces for that keystroke. Used to translate raw key events
		/// into typed characters (e.g. for hotstring collection) in a layout-aware way.
		/// </summary>
		public static bool TryMapKeystrokeToRune(uint vk, bool shift, bool altGr, out Rune rune)
		{
			EnsureProvider();
			rune = default;
			return provider != null && provider.TryMapKeystrokeToRune(vk, shift, altGr, out rune);
		}

		/// <summary>
		/// Stateful, dead-key-aware translation of a keystroke into characters. Mirrors the Windows
		/// ToUnicode return convention so the shared collection logic can treat all platforms alike:
		/// &gt;0 = chars written; 0 = no text; &lt;0 = dead key buffered for the next keystroke;
		/// <see cref="TranslateNotHandled"/> = provider can't translate (caller should fall back).
		/// </summary>
		public static int TranslateKeyWithDeadKeys(uint vk, bool shift, bool altGr, bool capsLock, Span<char> buffer)
		{
			EnsureProvider();
			return provider != null ? provider.TranslateKeyWithDeadKeys(vk, shift, altGr, capsLock, buffer) : TranslateNotHandled;
		}

		/// <summary>Clears any buffered dead-key composition state (e.g. when input collection (re)starts).</summary>
		public static void ResetDeadKeyState()
		{
			EnsureProvider();
			provider?.ResetDeadKeyState();
		}

		public static void ConfigureLayout(string rules = null, string model = null, string layout = null, string variant = null, string options = null)
		{
			EnsureProvider();
			provider?.ConfigureLayout(rules, model, layout, variant, options);
		}

		internal static nint GetCurrentKeymapHandle()
		{
			EnsureProvider();
			return provider?.GetCurrentKeymapHandle() ?? nint.Zero;
		}

#if LINUX
		internal static bool TryMapXKeycodeToVk(uint keycode, out uint vk)
		{
			EnsureProvider();
			vk = 0;
			return provider != null && provider.TryMapXKeycodeToVk(keycode, out vk);
		}

		internal static bool TryMapVkToXKeycode(uint vk, out uint keycode, bool returnSecondary = false)
		{
			EnsureProvider();
			keycode = 0;
			return provider != null && provider.TryMapVkToXKeycode(vk, out keycode, returnSecondary);
		}
#endif

#if OSX
		internal static bool TryMapMacCodeToVk(uint keycode, out uint vk)
		{
			EnsureProvider();
			vk = 0;
			return provider != null && provider.TryMapMacKeyCodeToVk(keycode, out vk);
		}

		internal static bool TryMapVkToMacCode(uint vk, out uint keycode)
		{
			EnsureProvider();
			keycode = 0;
			return provider != null && provider.TryMapVkToMacKeyCode(vk, out keycode);
		}
#endif

		private static void EnsureProvider()
		{
			if (provider != null)
				return;

			lock (providerLock)
			{
				if (provider != null)
					return;

				provider = CreateProvider();
				provider.StartLayoutChangeMonitoring();
			}
		}

		private static IKeyCodeMapperProvider CreateProvider()
		{
#if LINUX
			return new LinuxXkbCharMapperProvider();
#elif OSX
			return new MacCharMapperProvider();
#else
			return new NullKeyCodeMapperProvider();
#endif
		}
#endif
	}

#if !WINDOWS
	internal interface IKeyCodeMapperProvider : IDisposable
	{
		bool TryMapRuneToKeystroke(Rune rune, out uint vk, out bool needShift, out bool needAltGr);

		/// <summary>
		/// Reverse of <see cref="TryMapRuneToKeystroke"/>. Implementations that cannot do this
		/// (e.g. <see cref="NullKeyCodeMapperProvider"/>) should return false.
		/// </summary>
		bool TryMapKeystrokeToRune(uint vk, bool shift, bool altGr, out Rune rune)
		{
			rune = default;
			return false;
		}

		/// <summary>
		/// Stateful, dead-key-aware translation. See <see cref="KeyCodes.TranslateKeyWithDeadKeys"/>
		/// for the return convention. Providers without dead-key support leave this returning
		/// <see cref="KeyCodes.TranslateNotHandled"/> so the caller falls back to a stateless path.
		/// </summary>
		int TranslateKeyWithDeadKeys(uint vk, bool shift, bool altGr, bool capsLock, Span<char> buffer)
		{
			return KeyCodes.TranslateNotHandled;
		}

		/// <summary>Clears any buffered dead-key composition state.</summary>
		void ResetDeadKeyState() { }

		nint GetCurrentKeymapHandle();
#if LINUX
		bool TryMapXKeycodeToVk(uint keycode, out uint vk);
		bool TryMapVkToXKeycode(uint vk, out uint keycode, bool returnSecondary);
#endif

#if OSX
		bool TryMapMacKeyCodeToVk(uint keycode, out uint vk);
		bool TryMapVkToMacKeyCode(uint vk, out uint keycode);
#endif
		void ConfigureLayout(string rules, string model, string layout, string variant, string options);

		/// <summary>
		/// Starts listening for OS keyboard layout change notifications and invalidates
		/// any cached layout/character mappings (via <see cref="ConfigureLayout"/>) when
		/// the active layout changes. Called once when the provider is created.
		/// Implementations that have no such notification source can leave this as a no-op.
		/// </summary>
		void StartLayoutChangeMonitoring() { }
	}

	internal sealed class NullKeyCodeMapperProvider : IKeyCodeMapperProvider
	{
		public bool TryMapRuneToKeystroke(Rune rune, out uint vk, out bool needShift, out bool needAltGr)
		{
			vk = 0;
			needShift = false;
			needAltGr = false;
			return false;
		}

		public void ConfigureLayout(string rules, string model, string layout, string variant, string options)
		{
		}

		public nint GetCurrentKeymapHandle() => nint.Zero;

#if LINUX
		public bool TryMapXKeycodeToVk(uint keycode, out uint vk)
		{
			vk = 0;
			return false;
		}

		public bool TryMapVkToXKeycode(uint vk, out uint keycode, bool returnSecondary)
		{
			keycode = 0;
			return false;
		}
#endif

#if OSX
		public bool TryMapMacKeyCodeToVk(uint keycode, out uint vk)
		{
			vk = 0;
			return false;
		}

		public bool TryMapVkToMacKeyCode(uint vk, out uint keycode)
		{
			keycode = 0;
			return false;
		}
#endif

		public void Dispose()
		{
		}
	}
#endif
}
