﻿namespace Keysharp.Core.Common.Window
{
	internal abstract class StatusBarBase
	{
		protected const int timeout = 2000;
		protected string[] captions;
		protected nint handle;
		protected int panelCount;
		protected uint pid;

		internal string Caption
		{
			get => string.Join(" | ", Captions);
			set => SetCaptions(-1, value);
		}

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

		internal void SetCaptions(int index, string caption)//Everything here resolves to SetCaption() below which will throw.//TODO
		{
			if (index == -1)
			{
				var oldParts = Captions;
				var newParts = caption.Split([" | "], StringSplitOptions.None);

				if ((oldParts.Length == newParts.Length) && (newParts.Length > 0))
				{
					for (var i = 0; i < oldParts.Length; i++)
					{
						if (oldParts[i] != newParts[i])
							SetCaption(i, newParts[i]);
					}
				}
			}
			else
			{
				SetCaption(index, caption);
			}
		}

		//May need to add wait functionality here the way AHK does in StatusBarUtil().
		protected abstract string GetCaption(uint index);

		protected abstract uint GetOwningPid();

		protected abstract int GetPanelCount();

		protected void SetCaption(int index, string caption) => throw new NotImplementedException();//Did we really never implement this?//TODO

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