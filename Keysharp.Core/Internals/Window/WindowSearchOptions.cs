namespace Keysharp.Internals.Window
{
	internal sealed class WindowSearchOptions
	{
		internal static readonly WindowSearchOptions Empty = new();

		internal long? TitleMatchMode { get; set; }
		internal bool? TitleMatchModeSpeed { get; set; }
		internal bool? DetectHiddenWindows { get; set; }
		internal bool? DetectHiddenText { get; set; }
		internal bool IsEmpty => TitleMatchMode == null && TitleMatchModeSpeed == null && DetectHiddenWindows == null && DetectHiddenText == null;

		internal static WindowSearchOptions Merge(WindowSearchOptions primary, WindowSearchOptions fallback = null)
		{
			if (primary == null || primary.IsEmpty)
				return fallback == null || fallback.IsEmpty ? Empty : fallback;

			if (fallback == null || fallback.IsEmpty)
				return primary;

			return new WindowSearchOptions
			{
				TitleMatchMode = primary?.TitleMatchMode ?? fallback?.TitleMatchMode,
				TitleMatchModeSpeed = primary?.TitleMatchModeSpeed ?? fallback?.TitleMatchModeSpeed,
				DetectHiddenWindows = primary?.DetectHiddenWindows ?? fallback?.DetectHiddenWindows,
				DetectHiddenText = primary?.DetectHiddenText ?? fallback?.DetectHiddenText
			};
		}

		internal bool ApplyToken(string token)
		{
			switch (token.ToLowerInvariant())
			{
				case "1":
					TitleMatchMode = 1L;
					return true;

				case "2":
					TitleMatchMode = 2L;
					return true;

				case "3":
					TitleMatchMode = 3L;
					return true;

				case Keyword_RegEx:
					TitleMatchMode = 4L;
					return true;

				case Keyword_Fast:
					TitleMatchModeSpeed = true;
					return true;

				case Keyword_Slow:
					TitleMatchModeSpeed = false;
					return true;

				case Keyword_Hidden:
				case "hidden1":
					DetectHiddenWindows = true;
					return true;

				case "hidden0":
					DetectHiddenWindows = false;
					return true;

				case "hiddentext":
				case "hiddentext1":
					DetectHiddenText = true;
					return true;

				case "hiddentext0":
					DetectHiddenText = false;
					return true;
			}

			return false;
		}
	}
}
