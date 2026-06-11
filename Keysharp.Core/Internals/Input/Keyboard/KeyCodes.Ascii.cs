using System.Text;
using static Keysharp.Internals.Input.Keyboard.VirtualKeys;

namespace Keysharp.Internals.Input.Keyboard
{
	/// <summary>
	/// Layout-independent US-keyboard rune → VK + Shift fallback, used when no layout-aware
	/// provider can resolve a character. Part of the unified <see cref="KeyCodes"/> facade.
	/// </summary>
	internal static partial class KeyCodes
	{
		public static bool TryMapAsciiToVk(Rune rune, out uint vk, out bool needShift)
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
