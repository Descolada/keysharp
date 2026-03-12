namespace Keysharp.Core.Common.Threading
{
	internal class HotstringMsg
	{
		internal CaseConformModes caseMode = CaseConformModes.None;
		internal nint criterionFoundHwnd = 0;
		internal char endChar = (char)0;
		internal bool recheckCriterionOnReceipt;
		internal uint triggerVk;

		//Might want to add skipchars here.//TODO
		internal HotstringDefinition hs = null;
	}

	internal sealed class HookHotkeyMsg
	{
		internal long criterionFoundHwnd;
		internal ulong extraInfo;
		internal HotkeyVariant variant;
	}

	internal class KeysharpMsg
	{
		//internal bool completed;
		internal nint hwnd = 0;

		internal nint lParam = 0;
		internal uint message;
		internal object obj;

		//internal System.Drawing.Point pt;
		//internal uint time;
		internal nint wParam = 0;
	}
}
