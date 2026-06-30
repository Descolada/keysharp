namespace Keysharp.Internals
{
	internal static partial class Platform
	{
		/// <summary>Window-message posting. Windows uses the Win32 message queue; on Unix there is no such
		/// queue so these are no-ops that report success (no OS queue to post to).</summary>
		internal static class Messages
		{
#if WINDOWS
			public static bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam) => Os.Windows.WindowsAPI.PostMessage(hWnd, msg, wParam, lParam);

			public static bool PostMessage(nint hWnd, uint msg, uint wParam, uint lParam) => Os.Windows.WindowsAPI.PostMessage(hWnd, msg, wParam, lParam);
#else
			public static bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam) => true;

			public static bool PostMessage(nint hWnd, uint msg, uint wParam, uint lParam) => true;
#endif
		}
	}
}
