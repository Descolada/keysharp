using Keysharp.Builtins;

namespace Keysharp.Runtime.Keyboard
{
	[PublicHiddenFromUser]
	public static class HotstringManager
	{
		public static object AddHotstring(
			string name,
			IFuncObj funcObj,
			ReadOnlySpan<char> options,
			string hotstring,
			string replacement,
			bool hasContinuationSection,
			int suspend = 0)
			=> Keysharp.Internals.Input.Keyboard.HotstringManager.AddHotstring(
				name,
				funcObj,
				options,
				hotstring,
				replacement,
				hasContinuationSection,
				suspend);
	}
}
