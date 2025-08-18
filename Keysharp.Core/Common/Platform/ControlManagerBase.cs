namespace Keysharp.Core.Common.Platform
{
	internal abstract class ControlManagerBase
	{
		internal static ToolStripMenuItem GetMenuItem(MenuStrip strip, params object[] items)
		{
			var topLevel = items[0].As();
			ToolStripMenuItem menuItem = null;

			//First get the top level menu.
			if (topLevel.EndsWith('&') && int.TryParse(topLevel.AsSpan().Trim('&'), out var n) && n > 0)
			{
				n--;

				if (n < strip.Items.Count)
					menuItem = strip.Items[n] as ToolStripMenuItem;
				else
				{
					_ = Errors.ValueErrorOccurred($"Index {n + 1} was outside the menu length of {strip.Items.Count}.", topLevel);
					return default;
				}
			}
			else
			{
				foreach (ToolStripItem tempItem in strip.Items)
				{
					if (MenuMatchHelper(tempItem.Text, topLevel))
					{
						menuItem = tempItem as ToolStripMenuItem;
						break;
					}
				}
			}

			for (var i = 1; i < items.Length && menuItem != null; i++)
			{
				var item = items[i].As();

				if (item == null || item.Length == 0)
					continue;

				if (item.EndsWith('&') && int.TryParse(item.AsSpan().Trim('&'), out n) && n > 0)
				{
					n--;

					if (n < menuItem.DropDownItems.Count)
					{
						menuItem = menuItem.DropDownItems[n] as ToolStripMenuItem;
						continue;
					}
					else
					{
						_ = Errors.ValueErrorOccurred($"Index {n + 1} was outside the menu length of {menuItem.DropDownItems.Count}.", item);
						return default;
					}
				}
				else
				{
					foreach (ToolStripItem tempItem in menuItem.DropDownItems)
					{
						if (MenuMatchHelper(tempItem.Text, item))
						{
							menuItem = tempItem as ToolStripMenuItem;
							break;
						}
					}
				}
			}

			return menuItem;
		}

		internal static bool MenuMatchHelper(string menuText, string match)
		{
			var matchFound = menuText.StartsWith(match, StringComparison.CurrentCultureIgnoreCase);

			if (!matchFound && menuText.Contains('&'))
			{
				var tempsb = new StringBuilder(menuText.Length);

				for (var ii = 0; ii < menuText.Length; ii++)//This logic gotten from AHK to remove every other ampersand.
				{
					if (menuText[ii] == '&')
						ii++;

					if (ii < menuText.Length)
						_ = tempsb.Append(menuText[ii]);
					else
						break;
				}

				menuText = tempsb.ToString();
				matchFound = menuText.StartsWith(match, StringComparison.CurrentCultureIgnoreCase);
			}

			return matchFound;
		}

		internal abstract long ControlAddItem(string str, KsValue ctrl, KsValue title, string text, string excludeTitle, string excludeText);

		internal abstract void ControlChooseIndex(int n, KsValue ctrl, KsValue title, string text, string excludeTitle, string excludeText);

		internal abstract long ControlChooseString(string str, KsValue ctrl, KsValue title, string text, string excludeTitle, string excludeText);

		internal abstract void ControlClick(KsValue ctrlorpos, KsValue title, string text, string whichButton, int clickCount, string options, string excludeTitle, string excludeText);

		internal abstract void ControlDeleteItem(int n, KsValue ctrl, KsValue title, string text, string excludeTitle, string excludeText);

		internal abstract long ControlFindItem(string str, KsValue ctrl, KsValue title, string text, string excludeTitle, string excludeText);

		internal abstract void ControlFocus(KsValue ctrl, KsValue title, string text, string excludeTitle, string excludeText);

		internal abstract long ControlGetChecked(KsValue ctrl, KsValue title, string text, string excludeTitle, string excludeText);

		internal abstract string ControlGetChoice(KsValue ctrl, KsValue title, string text, string excludeTitle, string excludeText);

		internal virtual string ControlGetClassNN(KsValue ctrl, KsValue title, string text, string excludeTitle, string excludeText)
		{
			if (WindowSearch.SearchControl(ctrl, title, text, excludeTitle, excludeText) is WindowItemBase item)
				return item.ClassNN;

			return DefaultObject;
		}

		internal virtual long ControlGetEnabled(KsValue ctrl, KsValue title, string text, string excludeTitle, string excludeText)
		{
			if (WindowSearch.SearchControl(ctrl, title, text, excludeTitle, excludeText) is WindowItemBase item)
				return item.Enabled ? 1L : 0L;

			return 0L;
		}

		internal abstract long ControlGetExStyle(KsValue ctrl, KsValue title, string text, string excludeTitle, string excludeText);

		internal abstract long ControlGetFocus(KsValue title, string text, string excludeTitle, string excludeText);

		internal virtual long ControlGetHwnd(KsValue ctrl, KsValue title, string text, string excludeTitle, string excludeText)
		{
			if (WindowSearch.SearchControl(ctrl, title, text, excludeTitle, excludeText) is WindowItemBase item)
				return item.Handle.ToInt64();
			else
				return 0L;
		}

		internal abstract long ControlGetIndex(KsValue ctrl, KsValue title, string text, string excludeTitle, string excludeText);

		internal abstract object ControlGetItems(KsValue ctrl, KsValue title, string text, string excludeTitle, string excludeText);

		internal abstract void ControlGetPos(ref long outX, ref long outY, ref long outWidth, ref long outHeight, KsValue ctrl = default, KsValue title = default, string text = null, string excludeTitle = null, string excludeText = null);

		internal abstract long ControlGetStyle(KsValue ctrl, KsValue title, string text, string excludeTitle, string excludeText);

		internal abstract string ControlGetText(KsValue ctrl, KsValue title, string text, string excludeTitle, string excludeText);

		internal virtual long ControlGetVisible(KsValue ctrl, KsValue title, string text, string excludeTitle, string excludeText)
		{
			if (WindowSearch.SearchControl(ctrl, title, text, excludeTitle, excludeText) is WindowItemBase item)
			{
				if (Control.FromHandle(item.Handle) is Control ctrl2)
					return ctrl2.Visible ? 1L : 0L;
				else
					_ = item.Visible;
			}

			return 0L;
		}

		internal virtual void ControlHide(KsValue ctrl, KsValue title, string text, string excludeTitle, string excludeText) =>
		ShowHideHelper(false, ctrl, title, text, excludeTitle, excludeText);

		internal abstract void ControlHideDropDown(KsValue ctrl, KsValue title, string text, string excludeTitle, string excludeText);

		internal virtual void ControlMove(int x, int y, int width, int height, KsValue ctrl, KsValue title, string text, string excludeTitle, string excludeText)
		{
			if (WindowSearch.SearchControl(ctrl, title, text, excludeTitle, excludeText) is WindowItemBase item)
			{
				if (Control.FromHandle(item.Handle) is Control ctrl2)
				{
					ctrl2.Location = new Point(x == int.MinValue ? ctrl2.Location.X : x, y == int.MinValue ? ctrl2.Location.Y : y);
					ctrl2.Size = new Size(width == int.MinValue ? ctrl2.Size.Width : width, height == int.MinValue ? ctrl2.Size.Height : height);
				}
				else
				{
					item.Location = new Rectangle(x == int.MinValue ? item.Location.X : x, y == int.MinValue ? item.Location.Y : y, 0, 0);//Width and height are ignored.
					item.Size = new Size(width == int.MinValue ? item.Size.Width : width, height == int.MinValue ? item.Size.Height : height);
				}

				WindowItemBase.DoControlDelay();
			}
		}

		internal abstract void ControlSend(string str, KsValue ctrl, KsValue title, string text, string excludeTitle, string excludeText);

		internal abstract void ControlSendText(string str, KsValue ctrl, KsValue title, string text, string excludeTitle, string excludeText);

		internal abstract void ControlSetChecked(object val, KsValue ctrl, KsValue title, string text, string excludeTitle, string excludeText);

		internal abstract void ControlSetEnabled(object val, KsValue ctrl, KsValue title, string text, string excludeTitle, string excludeText);

		internal abstract void ControlSetExStyle(object val, KsValue ctrl, KsValue title, string text, string excludeTitle, string excludeText);

		internal abstract void ControlSetStyle(object val, KsValue ctrl, KsValue title, string text, string excludeTitle, string excludeText);

		//internal abstract void ControlSetText(string str, KsValue ctrl, KsValue title, string text, string excludeTitle, string excludeText);
		internal virtual void ControlSetText(string str, KsValue ctrl, KsValue title, string text, string excludeTitle, string excludeText)
		{
			if (WindowSearch.SearchControl(ctrl, title, text, excludeTitle, excludeText) is WindowItemBase item)
			{
				if (Control.FromHandle(item.Handle) is Control ctrl2)//No matter what I've tried, sending WM_SETTEXT will not work with buttons, however this method works flawlessly.
					ctrl2.Text = str;
				else
					item.Title = str;//Just in case... it seems to work on text boxes.

				WindowItemBase.DoControlDelay();
			}
		}

		internal virtual void ControlShow(KsValue ctrl, KsValue title, string text, string excludeTitle, string excludeText) =>
		ShowHideHelper(true, ctrl, title, text, excludeTitle, excludeText);

		internal abstract void ControlShowDropDown(KsValue ctrl, KsValue title, string text, string excludeTitle, string excludeText);

		internal abstract long EditGetCurrentCol(KsValue ctrl, KsValue title, string text, string excludeTitle, string excludeText);

		internal abstract long EditGetCurrentLine(KsValue ctrl, KsValue title, string text, string excludeTitle, string excludeText);

		internal abstract string EditGetLine(int n, KsValue ctrl, KsValue title, string text, string excludeTitle, string excludeText);

		internal abstract long EditGetLineCount(KsValue ctrl, KsValue title, string text, string excludeTitle, string excludeText);

		internal abstract string EditGetSelectedText(KsValue ctrl, KsValue title, string text, string excludeTitle, string excludeText);

		internal abstract void EditPaste(string str, KsValue ctrl, KsValue title, string text, string excludeTitle, string excludeText);

		internal abstract object ListViewGetContent(string options, KsValue ctrl, KsValue title, string text, string excludeTitle, string excludeText);

		internal abstract void MenuSelect(KsValue title, string text, object menu, object sub1, object sub2, object sub3, object sub4, object sub5, object sub6, string excludeTitle, string excludeText);

		internal abstract void PostMessage(uint msg, nint wparam, nint lparam, KsValue ctrl, KsValue title, string text, string excludeTitle, string excludeText);

		internal abstract long SendMessage(uint msg, object wparam, object lparam, KsValue ctrl, KsValue title, string text, string excludeTitle, string excludeText, int timeout);

		private static void ShowHideHelper(bool val, KsValue ctrl, KsValue title, string text, string excludeTitle, string excludeText)
		{
			if (WindowSearch.SearchControl(ctrl, title, text, excludeTitle, excludeText) is WindowItemBase item)
			{
				if (Control.FromHandle(item.Handle) is Control ctrl2)
					ctrl2.Visible = val;
				else
					_ = val ? item.Show() : item.Hide();

				WindowItemBase.DoControlDelay();
			}
		}
	}
}