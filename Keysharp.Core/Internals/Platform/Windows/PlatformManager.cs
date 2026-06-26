using Keysharp.Builtins;
#if WINDOWS
namespace Keysharp.Internals.Platform.Windows
{
	/// <summary>
	/// Concrete implementation of PlatformManager for the Windows platfrom.
	/// </summary>
	internal class PlatformManager : PlatformManagerBase, IPlatformManager
	{
		public static uint CurrentThreadId() => WindowsAPI.GetCurrentThreadId();

		public static bool DestroyIcon(nint icon) => WindowsAPI.DestroyIcon(icon);

		public static bool ExitProgram(uint flags, uint reason) => WindowsAPI.ExitWindowsEx(flags, reason);

		public static nint GetKeyboardLayout(uint idThread)=> WindowsAPI.GetKeyboardLayout(idThread);

		// The Win32 loader only accepts '\' path separators (a '/' yields ERROR_MOD_NOT_FOUND, including when
		// resolving a DLL's sibling dependencies), so normalize any '/' first. Allocates only when one is present.
		public static nint LoadLibrary(string path) => WindowsAPI.LoadLibrary(path != null && path.IndexOf('/') >= 0 ? path.Replace('/', '\\') : path);

		public static bool PostHotkeyMessage(nint hWnd, uint wParam, uint lParam) => WindowsAPI.PostMessage(hWnd, WindowsAPI.WM_HOTKEY, wParam, lParam);

		public static bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam) => WindowsAPI.PostMessage(hWnd, msg, wParam, lParam);

		public static bool PostMessage(nint hWnd, uint msg, uint wParam, uint lParam) => WindowsAPI.PostMessage(hWnd, msg, wParam, lParam);

		public static bool RegisterHotKey(nint hWnd, uint id, KeyModifiers fsModifiers, uint vk) => WindowsAPI.RegisterHotKey(hWnd, id, fsModifiers, vk);

		public static bool SetDllDirectory(string path) => WindowsAPI.SetDllDirectory(path);

		public static int ToUnicode(uint wVirtKey, uint wScanCode, byte[] lpKeyState, [Out] char[] pwszBuff, uint wFlags, nint dwhkl)
			=> WindowsAPI.ToUnicodeEx(wVirtKey, wScanCode, lpKeyState, pwszBuff, pwszBuff.Length, wFlags, dwhkl);

		public static uint MapVirtualKeyToChar(uint wVirtKey, nint hkl) => WindowsAPI.MapVirtualKeyEx(wVirtKey, WindowsAPI.MAPVK_VK_TO_CHAR, hkl);

		public static bool UnregisterHotKey(nint hWnd, uint id) => WindowsAPI.UnregisterHotKey(hWnd, id);

		public static bool GetCursorPos(out POINT lpPoint) => WindowsAPI.GetCursorPos(out lpPoint);

		/// <summary>
		/// Maps AHK-style cursor names to their corresponding <see cref="Cursor"/> objects.<br/>
		/// Names not present here (e.g. Icon, Size) have no direct WinForms equivalent and are reported as Unknown.
		/// </summary>
		private static readonly Dictionary<string, Cursor> cursorMap = new (StringComparer.OrdinalIgnoreCase)
		{
			{ "AppStarting", Cursors.AppStarting },
			{ "Arrow", Cursors.Arrow },
			{ "Cross", Cursors.Cross },
			{ "Help", Cursors.Help },
			{ "IBeam", Cursors.IBeam },
			{ "No", Cursors.No },
			{ "SizeAll", Cursors.SizeAll },
			{ "SizeNESW", Cursors.SizeNESW },
			{ "SizeNS", Cursors.SizeNS },
			{ "SizeNWSE", Cursors.SizeNWSE },
			{ "SizeWE", Cursors.SizeWE },
			{ "UpArrow", Cursors.UpArrow },
			{ "Wait", Cursors.WaitCursor }
		};

		public static string GetCursor()
		{
			var current = Cursor.Current;

			foreach (var (name, cursor) in cursorMap)
				if (current == cursor)
					return name;

			return "Unknown";
		}

		public static void SetCursor(string cursorName)
		{
			if (cursorMap.TryGetValue(cursorName, out var cursor))
				Cursor.Current = cursor;
		}
	}
}

#endif