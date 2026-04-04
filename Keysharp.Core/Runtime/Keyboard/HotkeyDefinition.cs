using Keysharp.Builtins;

namespace Keysharp.Runtime.Keyboard
{
	[PublicHiddenFromUser]
	public static class HotkeyDefinition
	{
		public static object AddHotkey(IFuncObj callback, uint hookAction, string name)
			=> Keysharp.Internals.Input.Keyboard.HotkeyDefinition.AddHotkey(callback, hookAction, name);

		public static object ManifestAllHotkeysHotstringsHooks()
			=> Keysharp.Internals.Input.Keyboard.HotkeyDefinition.ManifestAllHotkeysHotstringsHooks();
	}
}
