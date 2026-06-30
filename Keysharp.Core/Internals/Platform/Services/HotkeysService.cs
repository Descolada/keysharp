namespace Keysharp.Internals
{
#if WINDOWS
	internal sealed class WindowsHotkeys : IHotkeys
	{
		public bool Register(nint hWnd, uint id, KeyModifiers modifiers, uint vk) => Os.Windows.WindowsAPI.RegisterHotKey(hWnd, id, modifiers, vk);
		public bool Unregister(nint hWnd, uint id) => Os.Windows.WindowsAPI.UnregisterHotKey(hWnd, id);
		public bool PostHotkeyMessage(nint hWnd, uint wParam, uint lParam) => Os.Windows.WindowsAPI.PostMessage(hWnd, Os.Windows.WindowsAPI.WM_HOTKEY, wParam, lParam);
	}
#elif LINUX
	// X11 has no OS-level global-hotkey registration that delivers WM_HOTKEY; hotkeys are driven by the
	// input hook, so these are no-ops that report success (no OS queue to post to).
	internal sealed class LinuxHotkeys : IHotkeys
	{
		public bool Register(nint hWnd, uint id, KeyModifiers modifiers, uint vk) => true;
		public bool Unregister(nint hWnd, uint id) => true;
		public bool PostHotkeyMessage(nint hWnd, uint wParam, uint lParam) => true;
	}
#elif OSX
	internal sealed class MacHotkeys : IHotkeys
	{
		public bool Register(nint hWnd, uint id, KeyModifiers modifiers, uint vk) => true;
		public bool Unregister(nint hWnd, uint id) => true;
		public bool PostHotkeyMessage(nint hWnd, uint wParam, uint lParam) => true;
	}
#endif
}
