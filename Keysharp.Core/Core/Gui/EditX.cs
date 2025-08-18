namespace Keysharp.Core
{
	public static class EditX
	{
		public static long EditGetCurrentCol(KsValue control,
											 KsValue winTitle = default,
											 string winText = null,
											 string excludeTitle = null,
											 string excludeText = null) => Script.TheScript.ControlProvider.Manager.EditGetCurrentCol(
													 control,
													 winTitle,
													 winText,
													 excludeTitle,
													 excludeText);

		public static long EditGetCurrentLine(KsValue control,
											  KsValue winTitle = default,
											  string winText = null,
											  string excludeTitle = null,
											  string excludeText = null) => Script.TheScript.ControlProvider.Manager.EditGetCurrentLine(
													  control,
													  winTitle,
													  winText,
													  excludeTitle,
													  excludeText);

		public static string EditGetLine(object n,
										 KsValue control,
										 KsValue winTitle = default,
										 string winText = null,
										 string excludeTitle = null,
										 string excludeText = null) => Script.TheScript.ControlProvider.Manager.EditGetLine(
											 n.Ai(),
											 control,
											 winTitle,
											 winText,
											 excludeTitle,
											 excludeText);

		public static long EditGetLineCount(KsValue control,
											KsValue winTitle = default,
											string winText = null,
											string excludeTitle = null,
											string excludeText = null) => Script.TheScript.ControlProvider.Manager.EditGetLineCount(
												control,
												winTitle,
												winText,
												excludeTitle,
												excludeText);

		public static string EditGetSelectedText(KsValue control,
				KsValue winTitle = default,
				string winText = null,
				string excludeTitle = null,
				string excludeText = null) => Script.TheScript.ControlProvider.Manager.EditGetSelectedText(
					control,
					winTitle,
					winText,
					excludeTitle,
					excludeText);

		public static object EditPaste(object @string,
									   KsValue control,
									   KsValue winTitle = default,
									   string winText = null,
									   string excludeTitle = null,
									   string excludeText = null)
		{
			Script.TheScript.ControlProvider.Manager.EditPaste(
				@string.As(),
				control,
				winTitle,
				winText,
				excludeTitle,
				excludeText);
			return DefaultObject;
		}
	}
}