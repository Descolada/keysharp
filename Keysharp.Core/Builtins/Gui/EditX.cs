namespace Keysharp.Builtins
{
	public static class EditX
	{
		public static long EditGetCurrentCol(object control,
											 object winTitle = null,
											 object winText = null,
											 object excludeTitle = null,
											 object excludeText = null) => Platform.Control.EditGetCurrentCol(
													 control,
													 winTitle,
													 winText,
													 excludeTitle,
													 excludeText);

		public static long EditGetCurrentLine(object control,
											  object winTitle = null,
											  object winText = null,
											  object excludeTitle = null,
											  object excludeText = null) => Platform.Control.EditGetCurrentLine(
													  control,
													  winTitle,
													  winText,
													  excludeTitle,
													  excludeText);

		public static string EditGetLine(object n,
										 object control,
										 object winTitle = null,
										 object winText = null,
										 object excludeTitle = null,
										 object excludeText = null) => Platform.Control.EditGetLine(
											 n.Ai(),
											 control,
											 winTitle,
											 winText,
											 excludeTitle,
											 excludeText);

		public static long EditGetLineCount(object control,
											object winTitle = null,
											object winText = null,
											object excludeTitle = null,
											object excludeText = null) => Platform.Control.EditGetLineCount(
												control,
												winTitle,
												winText,
												excludeTitle,
												excludeText);

		public static string EditGetSelectedText(object control,
				object winTitle = null,
				object winText = null,
				object excludeTitle = null,
				object excludeText = null) => Platform.Control.EditGetSelectedText(
					control,
					winTitle,
					winText,
					excludeTitle,
					excludeText);

		public static object EditPaste(object @string,
									   object control,
									   object winTitle = null,
									   object winText = null,
									   object excludeTitle = null,
									   object excludeText = null)
		{
			Platform.Control.EditPaste(
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