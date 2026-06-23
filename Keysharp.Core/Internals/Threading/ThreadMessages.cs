using Keysharp.Builtins;
namespace Keysharp.Internals.Threading
{
	internal class HotstringMsg
	{
		internal CaseConformModes caseMode = CaseConformModes.None;
		internal nint criterionFoundHwnd = 0;
		internal char endChar = (char)0;
		internal bool recheckCriterionOnReceipt;
		internal int skipChars;
		internal uint triggerVk;
		internal HotstringDefinition hs = null;
	}

	internal sealed class HookHotkeyMsg
	{
		internal long criterionFoundHwnd;
		internal object eventInfo;
		internal ulong extraInfo;
		internal HotkeyVariant variant;
	}

	internal class KeysharpMsg
	{
		//internal bool completed;
		internal nint hwnd = 0;
		internal object eventInfo;

		internal nint lParam = 0;
		internal uint message;
		internal object obj;

		// Mouse cursor coordinates for AHK_INPUT_MOUSEDOWN/UP/MOVE. Stored as plain ints rather than
		// bit-packed into lParam so negative/multi-monitor coordinates survive on any pointer width.
		internal int mouseX, mouseY;

		//internal System.Drawing.Point pt;
		//internal uint time;
		internal nint wParam = 0;
	}
}
