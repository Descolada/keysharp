namespace Keysharp.Core
{
	public static class ControlX
	{
		public static long ControlAddItem(object @string,
										  KsValue control,
										  KsValue winTitle = default,
										  string winText = null,
										  string excludeTitle = null,
										  string excludeText = null) => Script.TheScript.ControlProvider.Manager.ControlAddItem(
											  @string.As(),
											  control,
											  winTitle,
											  winText,
											  excludeTitle,
											  excludeText);

		public static object ControlChooseIndex(object n,
												KsValue control,
												KsValue winTitle = default,
												string winText = null,
												string excludeTitle = null,
												string excludeText = null)
		{
			Script.TheScript.ControlProvider.Manager.ControlChooseIndex(
				n.Ai(),
				control,
				winTitle,
				winText,
				excludeTitle,
				excludeText);
			return DefaultObject;
		}

		public static long ControlChooseString(object @string,
											   KsValue control,
											   KsValue winTitle = default,
											   string winText = null,
											   string excludeTitle = null,
											   string excludeText = null) => Script.TheScript.ControlProvider.Manager.ControlChooseString(
													   @string.As(),
													   control,
													   winTitle,
													   winText,
													   excludeTitle,
													   excludeText);

		public static object ControlClick(KsValue ctrlOrPos = default,
										  KsValue title = default,
										  string text = null,
										  object whichButton = null,
										  object clickCount = null,
										  object options = null,
										  string excludeTitle = null,
										  string excludeText = null)
		{
			Script.TheScript.ControlProvider.Manager.ControlClick(
				ctrlOrPos,
				title,
				text,
				whichButton.As(),
				clickCount.Ai(1),
				options.As(),
				excludeTitle,
				excludeText);
			return DefaultObject;
		}

		public static object ControlDeleteItem(object n,
											   KsValue control,
											   KsValue winTitle = default,
											   string winText = null,
											   string excludeTitle = null,
											   string excludeText = null)
		{
			Script.TheScript.ControlProvider.Manager.ControlDeleteItem(
				n.Ai(),
				control,
				winTitle,
				winText,
				excludeTitle,
				excludeText);
			return DefaultObject;
		}

		public static long ControlFindItem(object @string,
										   KsValue control,
										   KsValue winTitle = default,
										   string winText = null,
										   string excludeTitle = null,
										   string excludeText = null) => Script.TheScript.ControlProvider.Manager.ControlFindItem(
											   @string.As(),
											   control,
											   winTitle,
											   winText,
											   excludeTitle,
											   excludeText);

		public static object ControlFocus(KsValue control,
										  KsValue winTitle = default,
										  string winText = null,
										  string excludeTitle = null,
										  string excludeText = null)
		{
			Script.TheScript.ControlProvider.Manager.ControlFocus(
				control,
				winTitle,
				winText,
				excludeTitle,
				excludeText);
			return DefaultObject;
		}

		public static long ControlGetChecked(KsValue control,
											 KsValue winTitle = default,
											 string winText = null,
											 string excludeTitle = null,
											 string excludeText = null) => Script.TheScript.ControlProvider.Manager.ControlGetChecked(
													 control,
													 winTitle,
													 winText,
													 excludeTitle,
													 excludeText);

		public static string ControlGetChoice(KsValue control,
											  KsValue winTitle = default,
											  string winText = null,
											  string excludeTitle = null,
											  string excludeText = null) => Script.TheScript.ControlProvider.Manager.ControlGetChoice(
													  control,
													  winTitle,
													  winText,
													  excludeTitle,
													  excludeText);

		public static string ControlGetClassNN(KsValue control,
											   KsValue winTitle = default,
											   string winText = null,
											   string excludeTitle = null,
											   string excludeText = null) => Script.TheScript.ControlProvider.Manager.ControlGetClassNN(
													   control,
													   winTitle,
													   winText,
													   excludeTitle,
													   excludeText);

		public static long ControlGetEnabled(KsValue control,
											 KsValue winTitle = default,
											 string winText = null,
											 string excludeTitle = null,
											 string excludeText = null) => Script.TheScript.ControlProvider.Manager.ControlGetEnabled(
													 control,
													 winTitle,
													 winText,
													 excludeTitle,
													 excludeText);

		public static long ControlGetExStyle(KsValue control,
											 KsValue winTitle = default,
											 string winText = null,
											 string excludeTitle = null,
											 string excludeText = null) => Script.TheScript.ControlProvider.Manager.ControlGetExStyle(
													 control,
													 winTitle,
													 winText,
													 excludeTitle,
													 excludeText);

		public static long ControlGetFocus(KsValue winTitle = default,
										   string winText = null,
										   string excludeTitle = null,
										   string excludeText = null) => Script.TheScript.ControlProvider.Manager.ControlGetFocus(
											   winTitle,
											   winText,
											   excludeTitle,
											   excludeText);

		public static long ControlGetHwnd(KsValue control,
										  KsValue winTitle = default,
										  string winText = null,
										  string excludeTitle = null,
										  string excludeText = null) => Script.TheScript.ControlProvider.Manager.ControlGetHwnd(
											  control,
											  winTitle,
											  winText,
											  excludeTitle,
											  excludeText);

		public static long ControlGetIndex(KsValue control,
										   KsValue winTitle = default,
										   string winText = null,
										   string excludeTitle = null,
										   string excludeText = null) => Script.TheScript.ControlProvider.Manager.ControlGetIndex(
											   control,
											   winTitle,
											   winText,
											   excludeTitle,
											   excludeText);

		public static object ControlGetItems(KsValue control,
											KsValue winTitle = default,
											string winText = null,
											string excludeTitle = null,
											string excludeText = null) => Script.TheScript.ControlProvider.Manager.ControlGetItems(
												control,
												winTitle,
												winText,
												excludeTitle,
												excludeText);

		public static object ControlGetPos([ByRef][Optional()][DefaultParameterValue(null)] Any outX,
										   [ByRef][Optional()][DefaultParameterValue(null)] Any outY,
										   [ByRef][Optional()][DefaultParameterValue(null)] Any outWidth,
										   [ByRef][Optional()][DefaultParameterValue(null)] Any outHeight,
										   KsValue ctrl = default,
										   KsValue title = default,
										   string text = null,
										   string excludeTitle = null,
										   string excludeText = null)
		{
			outX ??= VarRef.Empty; outY ??= VarRef.Empty; outWidth ??= VarRef.Empty; outHeight ??= VarRef.Empty;
			long valX = (long)Script.GetPropertyValue(outX, "__Value"), valY = (long)Script.GetPropertyValue(outY, "__Value"), valWidth = (long)Script.GetPropertyValue(outWidth, "__Value"), valHeight = (long)Script.GetPropertyValue(outHeight, "__Value");

            Script.TheScript.ControlProvider.Manager.ControlGetPos(
				ref valX,
				ref valY,
				ref valWidth,
				ref valHeight,
				ctrl,
				title,
				text,
				excludeTitle,
				excludeText);
			Script.SetPropertyValue(outX, "__Value", valX); Script.SetPropertyValue(outY, "__Value", valY); Script.SetPropertyValue(outWidth, "__Value", valWidth); Script.SetPropertyValue(outHeight, "__Value", valHeight);
            return DefaultObject;
		}

		public static long ControlGetStyle(KsValue control,
										   KsValue winTitle = default,
										   string winText = null,
										   string excludeTitle = null,
										   string excludeText = null) => Script.TheScript.ControlProvider.Manager.ControlGetStyle(
											   control,
											   winTitle,
											   winText,
											   excludeTitle,
											   excludeText);

		public static string ControlGetText(KsValue control,
											KsValue winTitle = default,
											string winText = null,
											string excludeTitle = null,
											string excludeText = null) => Script.TheScript.ControlProvider.Manager.ControlGetText(
												control,
												winTitle,
												winText,
												excludeTitle,
												excludeText);

		public static long ControlGetVisible(KsValue control,
											 KsValue winTitle = default,
											 string winText = null,
											 string excludeTitle = null,
											 string excludeText = null) => Script.TheScript.ControlProvider.Manager.ControlGetVisible(
													 control,
													 winTitle,
													 winText,
													 excludeTitle,
													 excludeText);

		public static object ControlHide(KsValue control,
										 KsValue winTitle = default,
										 string winText = null,
										 string excludeTitle = null,
										 string excludeText = null)
		{
			Script.TheScript.ControlProvider.Manager.ControlHide(
				control,
				winTitle,
				winText,
				excludeTitle,
				excludeText);
			return DefaultObject;
		}

		public static object ControlHideDropDown(KsValue control,
				KsValue winTitle = default,
				string winText = null,
				string excludeTitle = null,
				string excludeText = null)
		{
			Script.TheScript.ControlProvider.Manager.ControlHideDropDown(
				control,
				winTitle,
				winText,
				excludeTitle,
				excludeText);
			return DefaultObject;
		}

		public static object ControlMove(object x = null,
										 object y = null,
										 object width = null,
										 object height = null,
										 KsValue control = default,
										 KsValue winTitle = default,
										 string winText = null,
										 string excludeTitle = null,
										 string excludeText = null)
		{
			Script.TheScript.ControlProvider.Manager.ControlMove(
				x.Ai(int.MinValue),
				y.Ai(int.MinValue),
				width.Ai(int.MinValue),
				height.Ai(int.MinValue),
				control,
				winTitle,
				winText,
				excludeTitle,
				excludeText);
			return DefaultObject;
		}

		public static object ControlSend(string keys,
										 KsValue control = default,
										 KsValue winTitle = default,
										 string winText = null,
										 string excludeTitle = null,
										 string excludeText = null)
		{
			Script.TheScript.ControlProvider.Manager.ControlSend(
				keys,
				control,
				winTitle,
				winText,
				excludeTitle,
				excludeText);
			return DefaultObject;
		}

		public static object ControlSendText(string keys,
											 KsValue control = default,
											 KsValue winTitle = default,
											 string winText = null,
											 string excludeTitle = null,
											 string excludeText = null)
		{
			Script.TheScript.ControlProvider.Manager.ControlSendText(
				keys.As(),
				control,
				winTitle,
				winText,
				excludeTitle,
				excludeText);
			return DefaultObject;
		}

		public static object ControlSetChecked(object newSetting,
											   KsValue control,
											   KsValue winTitle = default,
											   string winText = null,
											   string excludeTitle = null,
											   string excludeText = null)
		{
			Script.TheScript.ControlProvider.Manager.ControlSetChecked(
				newSetting,
				control,
				winTitle,
				winText,
				excludeTitle,
				excludeText);
			return DefaultObject;
		}

		public static object ControlSetEnabled(object newSetting,
											   KsValue control,
											   KsValue winTitle = default,
											   string winText = null,
											   string excludeTitle = null,
											   string excludeText = null)
		{
			Script.TheScript.ControlProvider.Manager.ControlSetEnabled(
				newSetting,
				control,
				winTitle,
				winText,
				excludeTitle,
				excludeText);
			return DefaultObject;
		}

		public static object ControlSetExStyle(object value,
											   KsValue control,
											   KsValue winTitle = default,
											   string winText = null,
											   string excludeTitle = null,
											   string excludeText = null)
		{
			Script.TheScript.ControlProvider.Manager.ControlSetExStyle(
				value,
				control,
				winTitle,
				winText,
				excludeTitle,
				excludeText);
			return DefaultObject;
		}

		public static object ControlSetStyle(object value,
											 KsValue control,
											 KsValue winTitle = default,
											 string winText = null,
											 string excludeTitle = null,
											 string excludeText = null)
		{
			Script.TheScript.ControlProvider.Manager.ControlSetStyle(
				value,
				control,
				winTitle,
				winText,
				excludeTitle,
				excludeText);
			return DefaultObject;
		}

		public static object ControlSetText(object newText,
											KsValue control,
											KsValue winTitle = default,
											string winText = null,
											string excludeTitle = null,
											string excludeText = null)
		{
			Script.TheScript.ControlProvider.Manager.ControlSetText(
				newText.As(),
				control,
				winTitle,
				winText,
				excludeTitle,
				excludeText);
			return DefaultObject;
		}

		public static object ControlShow(KsValue control,
										 KsValue winTitle = default,
										 string winText = null,
										 string excludeTitle = null,
										 string excludeText = null)
		{
			Script.TheScript.ControlProvider.Manager.ControlShow(
				control,
				winTitle,
				winText,
				excludeTitle,
				excludeText);
			return DefaultObject;
		}

		public static object ControlShowDropDown(KsValue control,
				KsValue winTitle = default,
				string winText = null,
				string excludeTitle = null,
				string excludeText = null)
		{
			Script.TheScript.ControlProvider.Manager.ControlShowDropDown(
				control,
				winTitle,
				winText,
				excludeTitle,
				excludeText);
			return DefaultObject;
		}
	}
}