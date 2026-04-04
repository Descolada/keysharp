using Keysharp.Builtins;
#if !WINDOWS
using static Keysharp.Internals.Input.Keyboard.VirtualKeys;

namespace Keysharp.Internals.Input.Unix
{
	partial class UnixKeyboardMouseSender
	{
		internal static class UnixCharMapper
		{
			private static readonly object providerLock = new();
			private static IUnixCharMapperProvider provider;

			static UnixCharMapper()
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
				return TextFastMapUS.TryMapCharToVkShift_US(rune, out vk, out needShift);
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

			private static void EnsureProvider()
			{
				if (provider != null)
					return;

				lock (providerLock)
				{
					if (provider != null)
						return;

					provider = CreateProvider();
				}
			}

			private static IUnixCharMapperProvider CreateProvider()
			{
#if LINUX
				return new LinuxXkbCharMapperProvider();
#elif OSX
				return new MacCharMapperProvider();
#else
				return new NullCharMapperProvider();
#endif
			}
		}

		private interface IUnixCharMapperProvider : IDisposable
		{
			bool TryMapRuneToKeystroke(Rune rune, out uint vk, out bool needShift, out bool needAltGr);
			nint GetCurrentKeymapHandle();
#if LINUX
			bool TryMapXKeycodeToVk(uint keycode, out uint vk);
			bool TryMapVkToXKeycode(uint vk, out uint keycode, bool returnSecondary);
#endif
			void ConfigureLayout(string rules, string model, string layout, string variant, string options);
		}

		private sealed class NullCharMapperProvider : IUnixCharMapperProvider
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

			public void Dispose()
			{
			}
		}

		internal static class TextFastMapUS
		{
			public static bool TryMapCharToVkShift_US(Rune rune, out uint vk, out bool needShift)
			{
				vk = 0;
				needShift = false;

				if (!rune.IsAscii)
					return false;

				char ch = (char)rune.Value;

				switch (ch)
				{
					case ' ': vk = VK_SPACE; return true;
					case '\t': vk = VK_TAB; return true;
					case '\r':
					case '\n': vk = VK_RETURN; return true;
					case '\b': vk = VK_BACK; return true;
				}

				if (ch is >= 'a' and <= 'z')
				{
					vk = (uint)char.ToUpperInvariant(ch);
					return true;
				}

				if (ch is >= 'A' and <= 'Z')
				{
					vk = (uint)ch;
					needShift = true;
					return true;
				}

				if (ch is >= '0' and <= '9')
				{
					vk = (uint)ch;
					return true;
				}

				switch (ch)
				{
					case '!': vk = (uint)'1'; needShift = true; return true;
					case '@': vk = (uint)'2'; needShift = true; return true;
					case '#': vk = (uint)'3'; needShift = true; return true;
					case '$': vk = (uint)'4'; needShift = true; return true;
					case '%': vk = (uint)'5'; needShift = true; return true;
					case '^': vk = (uint)'6'; needShift = true; return true;
					case '&': vk = (uint)'7'; needShift = true; return true;
					case '*': vk = (uint)'8'; needShift = true; return true;
					case '(': vk = (uint)'9'; needShift = true; return true;
					case ')': vk = (uint)'0'; needShift = true; return true;

					case '-': vk = VK_OEM_MINUS; return true;
					case '_': vk = VK_OEM_MINUS; needShift = true; return true;

					case '=': vk = VK_OEM_PLUS; return true;
					case '+': vk = VK_OEM_PLUS; needShift = true; return true;

					case '[': vk = VK_OEM_4; return true;
					case '{': vk = VK_OEM_4; needShift = true; return true;

					case ']': vk = VK_OEM_6; return true;
					case '}': vk = VK_OEM_6; needShift = true; return true;

					case '\\': vk = VK_OEM_5; return true;
					case '|': vk = VK_OEM_5; needShift = true; return true;

					case ';': vk = VK_OEM_1; return true;
					case ':': vk = VK_OEM_1; needShift = true; return true;

					case '\'': vk = VK_OEM_7; return true;
					case '"': vk = VK_OEM_7; needShift = true; return true;

					case ',': vk = VK_OEM_COMMA; return true;
					case '<': vk = VK_OEM_COMMA; needShift = true; return true;

					case '.': vk = VK_OEM_PERIOD; return true;
					case '>': vk = VK_OEM_PERIOD; needShift = true; return true;

					case '/': vk = VK_OEM_2; return true;
					case '?': vk = VK_OEM_2; needShift = true; return true;

					case '`': vk = VK_OEM_3; return true;
					case '~': vk = VK_OEM_3; needShift = true; return true;
				}

				return false;
			}
		}
	}
}
#endif
