namespace Keysharp.Builtins
{
	public static class ControlX
	{
		private static void EnsureControlAutomationPermission(string operation)
			=> _ = Script.TheScript.Permissions.EnsureAccessibilityAutomation(operation: operation);

		private static void EnsureControlInputInjectionPermission(string operation)
			=> _ = Script.TheScript.Permissions.EnsureInputInjection(operation: operation);

		public static long ControlAddItem(object @string,
										  object controlID,
										  object winTitle = null,
										  object winText = null,
										  object excludeTitle = null,
										  object excludeText = null) => Platform.Control.ControlAddItem(
											  @string.As(),
											  controlID,
											  winTitle,
											  winText,
											  excludeTitle,
											  excludeText);

		public static object ControlChooseIndex(object n,
												object controlID,
												object winTitle = null,
												object winText = null,
												object excludeTitle = null,
												object excludeText = null)
		{
			Platform.Control.ControlChooseIndex(
				n.Ai(),
				controlID,
				winTitle,
				winText,
				excludeTitle,
				excludeText);
			return DefaultObject;
		}

		public static long ControlChooseString(object @string,
											   object controlID,
											   object winTitle = null,
											   object winText = null,
											   object excludeTitle = null,
											   object excludeText = null) => Platform.Control.ControlChooseString(
													   @string.As(),
													   controlID,
													   winTitle,
													   winText,
													   excludeTitle,
													   excludeText);

		public static object ControlClick(object controlOrPos = null,
										  object winTitle = null,
										  object winText = null,
										  object whichButton = null,
										  object clickCount = null,
										  object options = null,
										  object excludeTitle = null,
										  object excludeText = null)
		{
			EnsureControlAutomationPermission("ControlClick");
			EnsureControlInputInjectionPermission("ControlClick");
			Platform.Control.ControlClick(
				controlOrPos,
				winTitle,
				winText,
				whichButton.As(),
				clickCount.Ai(1),
				options.As(),
				excludeTitle,
				excludeText);
			return DefaultObject;
		}

		public static object ControlDeleteItem(object n,
											   object controlID,
											   object winTitle = null,
											   object winText = null,
											   object excludeTitle = null,
											   object excludeText = null)
		{
			Platform.Control.ControlDeleteItem(
				n.Ai(),
				controlID,
				winTitle,
				winText,
				excludeTitle,
				excludeText);
			return DefaultObject;
		}

		public static long ControlFindItem(object @string,
										   object controlID,
										   object winTitle = null,
										   object winText = null,
										   object excludeTitle = null,
										   object excludeText = null) => Platform.Control.ControlFindItem(
											   @string.As(),
											   controlID,
											   winTitle,
											   winText,
											   excludeTitle,
											   excludeText);

		public static object ControlFocus(object controlID,
										  object winTitle = null,
										  object winText = null,
										  object excludeTitle = null,
										  object excludeText = null)
		{
			EnsureControlAutomationPermission("ControlFocus");
			Platform.Control.ControlFocus(
				controlID,
				winTitle,
				winText,
				excludeTitle,
				excludeText);
			return DefaultObject;
		}

		public static long ControlGetChecked(object controlID,
											 object winTitle = null,
											 object winText = null,
											 object excludeTitle = null,
											 object excludeText = null) => Platform.Control.ControlGetChecked(
													 controlID,
													 winTitle,
													 winText,
													 excludeTitle,
													 excludeText);

		public static string ControlGetChoice(object controlID,
											  object winTitle = null,
											  object winText = null,
											  object excludeTitle = null,
											  object excludeText = null) => Platform.Control.ControlGetChoice(
													  controlID,
													  winTitle,
													  winText,
													  excludeTitle,
													  excludeText);

		public static string ControlGetClassNN(object controlID,
											   object winTitle = null,
											   object winText = null,
											   object excludeTitle = null,
											   object excludeText = null) => Platform.Control.ControlGetClassNN(
													   controlID,
													   winTitle,
													   winText,
													   excludeTitle,
													   excludeText);

		public static long ControlGetEnabled(object controlID,
											 object winTitle = null,
											 object winText = null,
											 object excludeTitle = null,
											 object excludeText = null) => Platform.Control.ControlGetEnabled(
													 controlID,
													 winTitle,
													 winText,
													 excludeTitle,
													 excludeText);

		public static long ControlGetExStyle(object controlID,
											 object winTitle = null,
											 object winText = null,
											 object excludeTitle = null,
											 object excludeText = null) => Platform.Control.ControlGetExStyle(
													 controlID,
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

		public static long ControlGetHwnd(object controlID,
										  object winTitle = null,
										  object winText = null,
										  object excludeTitle = null,
										  object excludeText = null) => Platform.Control.ControlGetHwnd(
											  controlID,
											  winTitle,
											  winText,
											  excludeTitle,
											  excludeText);

		public static long ControlGetIndex(object controlID,
										   object winTitle = null,
										   object winText = null,
										   object excludeTitle = null,
										   object excludeText = null) => Platform.Control.ControlGetIndex(
											   controlID,
											   winTitle,
											   winText,
											   excludeTitle,
											   excludeText);

		public static object ControlGetItems(object controlID,
											object winTitle = null,
											object winText = null,
											object excludeTitle = null,
											object excludeText = null) => Platform.Control.ControlGetItems(
												controlID,
												winTitle,
												winText,
												excludeTitle,
												excludeText);

		public static object ControlGetPos([ByRef] object outX = null,
										   [ByRef] object outY = null,
										   [ByRef] object outWidth = null,
										   [ByRef] object outHeight = null,
										   object controlID = null,
										   object winTitle = null,
										   object winText = null,
										   object excludeTitle = null,
										   object excludeText = null)
		{
			object valX = null, valY = null, valWidth = null, valHeight = null;
			Platform.Control.ControlGetPos(
				ref valX,
				ref valY,
				ref valWidth,
				ref valHeight,
				controlID,
				winTitle,
				winText,
				excludeTitle,
				excludeText);
			if (outX != null) Script.SetPropertyValue(outX, "__Value", valX);
			if (outY != null) Script.SetPropertyValue(outY, "__Value", valY);
			if (outWidth != null) Script.SetPropertyValue(outWidth, "__Value", valWidth);
			if (outHeight != null) Script.SetPropertyValue(outHeight, "__Value", valHeight);
            return DefaultObject;
		}

		public static long ControlGetStyle(object controlID,
										   object winTitle = null,
										   object winText = null,
										   object excludeTitle = null,
										   object excludeText = null) => Platform.Control.ControlGetStyle(
											   controlID,
											   winTitle,
											   winText,
											   excludeTitle,
											   excludeText);

		public static string ControlGetText(object controlID,
											object winTitle = null,
											object winText = null,
											object excludeTitle = null,
											object excludeText = null) => Platform.Control.ControlGetText(
												controlID,
												winTitle,
												winText,
												excludeTitle,
												excludeText);

		public static long ControlGetVisible(object controlID,
											 object winTitle = null,
											 object winText = null,
											 object excludeTitle = null,
											 object excludeText = null) => Platform.Control.ControlGetVisible(
													 controlID,
													 winTitle,
													 winText,
													 excludeTitle,
													 excludeText);

		public static object ControlHide(object controlID,
										 object winTitle = null,
										 object winText = null,
										 object excludeTitle = null,
										 object excludeText = null)
		{
			EnsureControlAutomationPermission("ControlHide");
			Platform.Control.ControlHide(
				controlID,
				winTitle,
				winText,
				excludeTitle,
				excludeText);
			return DefaultObject;
		}

		public static object ControlHideDropDown(object controlID,
				object winTitle = null,
				object winText = null,
				object excludeTitle = null,
				object excludeText = null)
		{
			EnsureControlAutomationPermission("ControlHideDropDown");
			Platform.Control.ControlHideDropDown(
				controlID,
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
										 object controlID = null,
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
				controlID,
				winTitle,
				winText,
				excludeTitle,
				excludeText);
			return DefaultObject;
		}

		public static object ControlSend(object keys,
										 object controlID = null,
										 object winTitle = null,
										 object winText = null,
										 object excludeTitle = null,
										 object excludeText = null)
		{
			EnsureControlAutomationPermission("ControlSend");
			EnsureControlInputInjectionPermission("ControlSend");
			Platform.Control.ControlSend(
				keys.As(),
				controlID,
				winTitle,
				winText,
				excludeTitle,
				excludeText);
			return DefaultObject;
		}

		public static object ControlSendText(object keys,
											 object controlID = null,
											 object winTitle = null,
											 object winText = null,
											 object excludeTitle = null,
											 object excludeText = null)
		{
			EnsureControlAutomationPermission("ControlSendText");
			EnsureControlInputInjectionPermission("ControlSendText");
			Platform.Control.ControlSendText(
				keys.As(),
				controlID,
				winTitle,
				winText,
				excludeTitle,
				excludeText);
			return DefaultObject;
		}

		public static object ControlSetChecked(object newSetting,
											   object controlID,
											   object winTitle = null,
											   object winText = null,
											   object excludeTitle = null,
											   object excludeText = null)
		{
			EnsureControlAutomationPermission("ControlSetChecked");
			Platform.Control.ControlSetChecked(
				newSetting,
				controlID,
				winTitle,
				winText,
				excludeTitle,
				excludeText);
			return DefaultObject;
		}

		public static object ControlSetEnabled(object newSetting,
											   object controlID,
											   object winTitle = null,
											   object winText = null,
											   object excludeTitle = null,
											   object excludeText = null)
		{
			EnsureControlAutomationPermission("ControlSetEnabled");
			Platform.Control.ControlSetEnabled(
				newSetting,
				controlID,
				winTitle,
				winText,
				excludeTitle,
				excludeText);
			return DefaultObject;
		}

		public static object ControlSetExStyle(object value,
											   object controlID,
											   object winTitle = null,
											   object winText = null,
											   object excludeTitle = null,
											   object excludeText = null)
		{
			EnsureControlAutomationPermission("ControlSetExStyle");
			Platform.Control.ControlSetExStyle(
				value,
				controlID,
				winTitle,
				winText,
				excludeTitle,
				excludeText);
			return DefaultObject;
		}

		public static object ControlSetStyle(object value,
											 object controlID,
											 object winTitle = null,
											 object winText = null,
											 object excludeTitle = null,
											 object excludeText = null)
		{
			EnsureControlAutomationPermission("ControlSetStyle");
			Platform.Control.ControlSetStyle(
				value,
				controlID,
				winTitle,
				winText,
				excludeTitle,
				excludeText);
			return DefaultObject;
		}

		public static object ControlSetText(object newText,
											object controlID,
											object winTitle = null,
											object winText = null,
											object excludeTitle = null,
											object excludeText = null)
		{
			EnsureControlAutomationPermission("ControlSetText");
			Platform.Control.ControlSetText(
				newText.As(),
				controlID,
				winTitle,
				winText,
				excludeTitle,
				excludeText);
			return DefaultObject;
		}

		public static object ControlShow(object controlID,
										 object winTitle = null,
										 object winText = null,
										 object excludeTitle = null,
										 object excludeText = null)
		{
			EnsureControlAutomationPermission("ControlShow");
			Platform.Control.ControlShow(
				controlID,
				winTitle,
				winText,
				excludeTitle,
				excludeText);
			return DefaultObject;
		}

		public static object ControlShowDropDown(object controlID,
				object winTitle = null,
				object winText = null,
				object excludeTitle = null,
				object excludeText = null)
		{
			EnsureControlAutomationPermission("ControlShowDropDown");
			Platform.Control.ControlShowDropDown(
				controlID,
				winTitle,
				winText,
				excludeTitle,
				excludeText);
			return DefaultObject;
		}
	}
}
