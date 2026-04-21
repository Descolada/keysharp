using Keysharp.Builtins;
namespace Keysharp.Internals.Window
{
	internal abstract class StatusBarBase
	{
		protected const int timeout = 2000;
		protected string[] captions;
		protected nint handle;
		protected int panelCount;
		protected uint pid;

		internal string Caption => string.Join(" | ", Captions);

		internal string[] Captions
		{
			get
			{
				if (captions == null)
					captions = GetCaptions();

				return captions;
			}
		}

		internal uint OwningPID
		{
			get
			{
				if (pid == 0)
					pid = GetOwningPid();

				return pid;
			}
		}

		internal int PanelCount
		{
			get
			{
				if (panelCount == -1)
					panelCount = GetPanelCount();

				return panelCount;
			}
		}

		internal StatusBarBase(nint hWnd)
		{
			handle = hWnd;
			panelCount = -1;
			pid = 0;
		}

		//May need to add wait functionality here the way AHK does in StatusBarUtil().
		protected abstract string GetCaption(uint index);

		protected abstract uint GetOwningPid();

		protected abstract int GetPanelCount();

		private string[] GetCaptions()
		{
			var count = PanelCount;
			var caps = new string[count];

			for (uint i = 0; i < count; i++)
				caps[i] = GetCaption(i);

			return caps;
		}
	}
}