#if OSX
namespace Keysharp.Core.Unix
{
	partial class UnixKeyboardMouseSender
	{
		private sealed class MacCharMapperProvider : IUnixCharMapperProvider
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

			public void Dispose()
			{
			}
		}
	}
}
#endif
