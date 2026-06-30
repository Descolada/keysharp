namespace Keysharp.Builtins
{
	public static class ControlX
	{
		private static void EnsureControlAutomationPermission(string operation)
			=> _ = Script.TheScript.Permissions.EnsureAccessibilityAutomation(operation: operation);

		private static void EnsureControlInputInjectionPermission(string operation)
			=> _ = Script.TheScript.Permissions.EnsureInputInjection(operation: operation);

		public static long ControlAddItem(object @string,
										  object control,
										  object winTitle = null,
										  object winText = null,
										  object excludeTitle = null,
										  object excludeText = null) => Platform.Control.ControlAddItem(
											  @string.As(),
											  control,
											  winTitle,
											  winText,
											  excludeTitle,
											  excludeText);

		public static object ControlChooseIndex(object n,
												object control,
												object winTitle = null,
												object winText = null,
												object excludeTitle = null,
												object excludeText = null)
		{
			Platform.Control.ControlChooseIndex(
				n.Ai(),
				control,
				winTitle,
				winText,
				excludeTitle,
				excludeText);
			return DefaultObject;
		}

		public static long ControlChooseString(object @string,
											   object control,
											   object winTitle = null,
											   object winText = null,
											   object excludeTitle = null,
											   object excludeText = null) => Platform.Control.ControlChooseString(
													   @string.As(),
													   control,
													   winTitle,
													   winText,
													   excludeTitle,
													   excludeText);

		public static object ControlClick(object ctrlOrPos = null,
										  object title = null,
										  object text = null,
										  object whichButton = null,
										  object clickCount = null,
										  object options = null,
										  object excludeTitle = null,
										  object excludeText = null)
		{
			EnsureControlAutomationPermission("ControlClick");
			EnsureControlInputInjectionPermission("ControlClick");
			Platform.Control.ControlClick(
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
											   object control,
											   object winTitle = null,
											   object winText = null,
											   object excludeTitle = null,
											   object excludeText = null)
		{
			Platform.Control.ControlDeleteItem(
				n.Ai(),
				control,
				winTitle,
				winText,
				excludeTitle,
				excludeText);
			return DefaultObject;
		}

		public static long ControlFindItem(object @string,
										   object control,
										   object winTitle = null,
										   object winText = null,
										   object excludeTitle = null,
										   object excludeText = null) => Platform.Control.ControlFindItem(
											   @string.As(),
											   control,
											   winTitle,
											   winText,
											   excludeTitle,
											   excludeText);

		public static object ControlFocus(object control,
										  object winTitle = null,
										  object winText = null,
										  object excludeTitle = null,
										  object excludeText = null)
		{
			EnsureControlAutomationPermission("ControlFocus");
			Platform.Control.ControlFocus(
				control,
				winTitle,
				winText,
				excludeTitle,
				excludeText);
			return DefaultObject;
		}

		public static long ControlGetChecked(object control,
											 object winTitle = null,
											 object winText = null,
											 object excludeTitle = null,
											 object excludeText = null) => Platform.Control.ControlGetChecked(
													 control,
													 winTitle,
													 winText,
													 excludeTitle,
													 excludeText);

		public static string ControlGetChoice(object control,
											  object winTitle = null,
											  object winText = null,
											  object excludeTitle = null,
											  object excludeText = null) => Platform.Control.ControlGetChoice(
													  control,
													  winTitle,
													  winText,
													  excludeTitle,
													  excludeText);

		public static string ControlGetClassNN(object control,
											   object winTitle = null,
											   object winText = null,
											   object excludeTitle = null,
											   object excludeText = null) => Platform.Control.ControlGetClassNN(
													   control,
													   winTitle,
													   winText,
													   excludeTitle,
													   excludeText);

		public static long ControlGetEnabled(object control,
											 object winTitle = null,
											 object winText = null,
											 object excludeTitle = null,
											 object excludeText = null) => Platform.Control.ControlGetEnabled(
													 control,
													 winTitle,
													 winText,
													 excludeTitle,
													 excludeText);

		public static long ControlGetExStyle(object control,
											 object winTitle = null,
											 object winText = null,
											 object excludeTitle = null,
											 object excludeText = null) => Platform.Control.ControlGetExStyle(
													 control,
													 winTitle,
													 winText,
													 excludeTitle,
													 excludeText);

		public static long ControlGetFocus(object winTitle = null,
										   object winText = null,
										   object excludeTitle = null,
										   object excludeText = null) => Platform.Control.ControlGetFocus(
											   winTitle,
											   winText,
											   excludeTitle,
											   excludeText);

		public static long ControlGetHwnd(object control,
										  object winTitle = null,
										  object winText = null,
										  object excludeTitle = null,
										  object excludeText = null) => Platform.Control.ControlGetHwnd(
											  control,
											  winTitle,
											  winText,
											  excludeTitle,
											  excludeText);

		public static long ControlGetIndex(object control,
										   object winTitle = null,
										   object winText = null,
										   object excludeTitle = null,
										   object excludeText = null) => Platform.Control.ControlGetIndex(
											   control,
											   winTitle,
											   winText,
											   excludeTitle,
											   excludeText);

		public static object ControlGetItems(object control,
											object winTitle = null,
											object winText = null,
											object excludeTitle = null,
											object excludeText = null) => Platform.Control.ControlGetItems(
												control,
												winTitle,
												winText,
												excludeTitle,
												excludeText);

		public static object ControlGetPos([ByRef] object outX = null,
										   [ByRef] object outY = null,
										   [ByRef] object outWidth = null,
										   [ByRef] object outHeight = null,
										   object ctrl = null,
										   object title = null,
										   object text = null,
										   object excludeTitle = null,
										   object excludeText = null)
		{
			object valX = null, valY = null, valWidth = null, valHeight = null;
			Platform.Control.ControlGetPos(
				ref valX,
				ref valY,
				ref valWidth,
				ref valHeight,
				ctrl,
				title,
				text,
				excludeTitle,
				excludeText);
			if (outX != null) Script.SetPropertyValue(outX, "__Value", valX);
			if (outY != null) Script.SetPropertyValue(outY, "__Value", valY);
			if (outWidth != null) Script.SetPropertyValue(outWidth, "__Value", valWidth);
			if (outHeight != null) Script.SetPropertyValue(outHeight, "__Value", valHeight);
            return DefaultObject;
		}

		public static long ControlGetStyle(object control,
										   object winTitle = null,
										   object winText = null,
										   object excludeTitle = null,
										   object excludeText = null) => Platform.Control.ControlGetStyle(
											   control,
											   winTitle,
											   winText,
											   excludeTitle,
											   excludeText);

		public static string ControlGetText(object control,
											object winTitle = null,
											object winText = null,
											object excludeTitle = null,
											object excludeText = null) => Platform.Control.ControlGetText(
												control,
												winTitle,
												winText,
												excludeTitle,
												excludeText);

		public static long ControlGetVisible(object control,
											 object winTitle = null,
											 object winText = null,
											 object excludeTitle = null,
											 object excludeText = null) => Platform.Control.ControlGetVisible(
													 control,
													 winTitle,
													 winText,
													 excludeTitle,
													 excludeText);

		public static object ControlHide(object control,
										 object winTitle = null,
										 object winText = null,
										 object excludeTitle = null,
										 object excludeText = null)
		{
			EnsureControlAutomationPermission("ControlHide");
			Platform.Control.ControlHide(
				control,
				winTitle,
				winText,
				excludeTitle,
				excludeText);
			return DefaultObject;
		}

		public static object ControlHideDropDown(object control,
				object winTitle = null,
				object winText = null,
				object excludeTitle = null,
				object excludeText = null)
		{
			EnsureControlAutomationPermission("ControlHideDropDown");
			Platform.Control.ControlHideDropDown(
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
										 object control = null,
										 object winTitle = null,
										 object winText = null,
										 object excludeTitle = null,
										 object excludeText = null)
		{
			EnsureControlAutomationPermission("ControlMove");
			Platform.Control.ControlMove(
				(x is null ? int.MinValue : x.ToInt()),
				(y is null ? int.MinValue : y.ToInt()),
				(width is null ? int.MinValue : width.ToInt()),
				(height is null ? int.MinValue : height.ToInt()),
				control,
				winTitle,
				winText,
				excludeTitle,
				excludeText);
			return DefaultObject;
		}

		public static object ControlSend(object keys,
										 object control = null,
										 object winTitle = null,
										 object winText = null,
										 object excludeTitle = null,
										 object excludeText = null)
		{
			EnsureControlAutomationPermission("ControlSend");
			EnsureControlInputInjectionPermission("ControlSend");
			Platform.Control.ControlSend(
				keys.As(),
				control,
				winTitle,
				winText,
				excludeTitle,
				excludeText);
			return DefaultObject;
		}

		public static object ControlSendText(object keys,
											 object control = null,
											 object winTitle = null,
											 object winText = null,
											 object excludeTitle = null,
											 object excludeText = null)
		{
			EnsureControlAutomationPermission("ControlSendText");
			EnsureControlInputInjectionPermission("ControlSendText");
			Platform.Control.ControlSendText(
				keys.As(),
				control,
				winTitle,
				winText,
				excludeTitle,
				excludeText);
			return DefaultObject;
		}

		public static object ControlSetChecked(object newSetting,
											   object control,
											   object winTitle = null,
											   object winText = null,
											   object excludeTitle = null,
											   object excludeText = null)
		{
			EnsureControlAutomationPermission("ControlSetChecked");
			Platform.Control.ControlSetChecked(
				newSetting,
				control,
				winTitle,
				winText,
				excludeTitle,
				excludeText);
			return DefaultObject;
		}

		public static object ControlSetEnabled(object newSetting,
											   object control,
											   object winTitle = null,
											   object winText = null,
											   object excludeTitle = null,
											   object excludeText = null)
		{
			EnsureControlAutomationPermission("ControlSetEnabled");
			Platform.Control.ControlSetEnabled(
				newSetting,
				control,
				winTitle,
				winText,
				excludeTitle,
				excludeText);
			return DefaultObject;
		}

		public static object ControlSetExStyle(object value,
											   object control,
											   object winTitle = null,
											   object winText = null,
											   object excludeTitle = null,
											   object excludeText = null)
		{
			EnsureControlAutomationPermission("ControlSetExStyle");
			Platform.Control.ControlSetExStyle(
				value,
				control,
				winTitle,
				winText,
				excludeTitle,
				excludeText);
			return DefaultObject;
		}

		public static object ControlSetStyle(object value,
											 object control,
											 object winTitle = null,
											 object winText = null,
											 object excludeTitle = null,
											 object excludeText = null)
		{
			EnsureControlAutomationPermission("ControlSetStyle");
			Platform.Control.ControlSetStyle(
				value,
				control,
				winTitle,
				winText,
				excludeTitle,
				excludeText);
			return DefaultObject;
		}

		public static object ControlSetText(object newText,
											object control,
											object winTitle = null,
											object winText = null,
											object excludeTitle = null,
											object excludeText = null)
		{
			EnsureControlAutomationPermission("ControlSetText");
			Platform.Control.ControlSetText(
				newText.As(),
				control,
				winTitle,
				winText,
				excludeTitle,
				excludeText);
			return DefaultObject;
		}

		public static object ControlShow(object control,
										 object winTitle = null,
										 object winText = null,
										 object excludeTitle = null,
										 object excludeText = null)
		{
			EnsureControlAutomationPermission("ControlShow");
			Platform.Control.ControlShow(
				control,
				winTitle,
				winText,
				excludeTitle,
				excludeText);
			return DefaultObject;
		}

		public static object ControlShowDropDown(object control,
				object winTitle = null,
				object winText = null,
				object excludeTitle = null,
				object excludeText = null)
		{
			EnsureControlAutomationPermission("ControlShowDropDown");
			Platform.Control.ControlShowDropDown(
				control,
				winTitle,
				winText,
				excludeTitle,
				excludeText);
			return DefaultObject;
		}
	}
}
