using Keysharp.Builtins;
namespace Keysharp.Internals.Platform
{
	internal interface IWindowManager
	{
		internal static abstract WindowItemBase ActiveWindow { get; }
		internal static abstract IEnumerable<WindowItemBase> AllWindows { get; }
		internal static abstract WindowItemBase LastFound { get; set; }

		internal static abstract WindowItemBase CreateWindow(nint id);
		internal static abstract IEnumerable<WindowItemBase> EnumerateWindows(bool detectHiddenWindows);

		internal static abstract IEnumerable<WindowItemBase> FilterForGroups(IEnumerable<WindowItemBase> windows);

		internal static abstract WindowItemBase FindWindow(SearchCriteria criteria, bool last = false);

		internal static abstract WindowItemBase FindWindow(object winTitle, object winText, object excludeTitle, object excludeText, bool last = false, bool ignorePureID = false);

		internal static abstract List<WindowItemBase> FindWindowGroup(SearchCriteria criteria, bool forceAll = false, bool ignorePureID = false);

		internal static abstract (List<WindowItemBase>, SearchCriteria) FindWindowGroup(object winTitle,
				object winText,
				object excludeTitle,
				object excludeText,
				bool forceAll = false,
				bool ignorePureID = false);

		internal static abstract uint GetFocusedCtrlThread(ref nint apControl, nint aWindow);

		internal static abstract nint GetForegroundWindowHandle();

		internal static abstract bool IsWindow(nint handle);

		internal static abstract void MaximizeAll();

		internal static abstract void MinimizeAll();

		internal static abstract void MinimizeAllUndo();

		internal static abstract WindowItemBase WindowFromPoint(POINT location);

		internal static abstract WindowItemBase ChildWindowFromPoint(POINT location);
	}
	internal class WindowGroup
	{
		internal Stack<long> activated = new ();
		internal Stack<long> deactivated = new ();
		internal bool lastWasDeactivate = false;
		internal List<SearchCriteria> sc = [];
	}

	/// <summary>
	/// Platform independent window manager.
	/// </summary>
	internal abstract class WindowManagerBase
	{
		internal Dictionary<string, WindowGroup> Groups { get; } = new Dictionary<string, WindowGroup>(StringComparer.OrdinalIgnoreCase);

		public static WindowItemBase LastFound
		{
			get => WindowManager.CreateWindow((nint)Script.TheScript.HwndLastUsed);
			set => Script.TheScript.HwndLastUsed = value.Handle;
		}

		public static WindowItemBase FindWindow(SearchCriteria criteria, bool last = false)
		{
			WindowItemBase found = null;

			if (criteria.IsEmpty)
				return found;

			var matchOptions = WindowSearchOptions.Merge(criteria.Options);
			var detectHiddenWindows = ShouldDetectHiddenWindows(criteria);

			if (criteria.Active)
			{
				var activeWindow = WindowManager.ActiveWindow;
				return activeWindow is WindowItemBase active && active.IsSpecified && active.Equals(criteria, matchOptions) ? active : null;
			}

			if (criteria.ID != 0)
			{
				if (WindowManager.IsWindow(criteria.ID) && WindowManager.CreateWindow(criteria.ID) is WindowItemBase temp && temp.Equals(criteria, matchOptions))
					return temp;

				return null;
			}

			foreach (var window in WindowManager.EnumerateWindows(detectHiddenWindows))
			{
				if (window.Equals(criteria, matchOptions))
				{
					found = window;

					if (!last)
						break;
				}
			}

			return found;
		}

		public static WindowItemBase FindWindow(object winTitle, object winText, object excludeTitle, object excludeText, bool last = false, bool ignorePureID = false)
		{
			WindowItemBase foundWindow = null;
			var (parsed, ptr) = WindowHelper.CtrlTonint(winTitle);

			if (parsed)
			{
				if (!ignorePureID && WindowManager.IsWindow(ptr))
					return LastFound = WindowManager.CreateWindow(ptr);
#if !WINDOWS
				if (Control.FromHandle(ptr) is Control ctrl)
					return LastFound = new ControlItem(ctrl);
#endif
			}

			var text = winText.As();
			var exclTitle = excludeTitle.As();
			var exclTxt = excludeText.As();

			if (winTitle is Gui gui)
			{
				return LastFound = WindowManager.CreateWindow((nint)gui.Hwnd);
			}
			else if ((winTitle == null || winTitle is string s && string.IsNullOrEmpty(s)) &&
					 string.IsNullOrEmpty(text) &&
					 string.IsNullOrEmpty(exclTitle) &&
					 string.IsNullOrEmpty(exclTxt))
			{
				foundWindow = LastFound;
			}
			else
			{
				var criteria = SearchCriteria.FromString(winTitle, text, excludeTitle, excludeText);
				foundWindow = WindowManager.FindWindow(criteria, last);
			}

			if (foundWindow != null && foundWindow.IsSpecified)
				LastFound = foundWindow;

			return foundWindow;
		}

		public static List<WindowItemBase> FindWindowGroup(SearchCriteria criteria, bool forceAll = false, bool ignorePureID = false)
		{
			var found = new List<WindowItemBase>();
			var matchOptions = WindowSearchOptions.Merge(criteria.Options);
			var detectHiddenWindows = ShouldDetectHiddenWindows(criteria);

			if (criteria.Active)
			{
				var activeWindow = WindowManager.ActiveWindow;

				if (activeWindow is WindowItemBase active && active.IsSpecified && active.Equals(criteria, matchOptions))
					found.Add(active);

				return found;
			}

			if (!ignorePureID && criteria.IsPureID)
			{
				if (WindowManager.IsWindow(criteria.ID))
				{
					var window = WindowManager.CreateWindow(criteria.ID);

					if (window.Equals(criteria, matchOptions)) // Other criteria may be present such as ExcludeTitle etc
						found.Add(window);
				}

				return found;
			}

			foreach (var window in WindowManager.EnumerateWindows(detectHiddenWindows))
			{
				if (criteria.IsEmpty || window.Equals(criteria, matchOptions))
				{
					found.Add(window);

					if (!forceAll && string.IsNullOrEmpty(criteria.Group))//If it was a group match, or if the caller specified that they want all matched windows, add it and keep going.
						break;
				}
			}

			return found;
		}

		public static (List<WindowItemBase>, SearchCriteria) FindWindowGroup(object winTitle,
				object winText,
				object excludeTitle,
				object excludeText,
				bool forceAll = false,
				bool ignorePureID = false)
		{
			SearchCriteria criteria = null;
			var foundWindows = new List<WindowItemBase>();
			var text = winText.As();
			var exclTitle = excludeTitle.As();
			var exclText = excludeText.As();

			if ((winTitle == null || winTitle is string s && string.IsNullOrEmpty(s)) &&
					string.IsNullOrEmpty(text) &&
					string.IsNullOrEmpty(exclTitle) &&
					string.IsNullOrEmpty(exclText))
			{
				if (LastFound != null)
					foundWindows.Add(LastFound);
			}
			else
			{
				criteria = SearchCriteria.FromString(winTitle, text, exclTitle, exclText);
				foundWindows = WindowManager.FindWindowGroup(criteria, forceAll, ignorePureID);

				if (foundWindows != null && foundWindows.Count > 0 && foundWindows[0].IsSpecified)
					LastFound = foundWindows[0];
			}

			return (foundWindows, criteria);
		}

		internal static bool ShouldDetectHiddenWindows(SearchCriteria criteria, WindowSearchOptions inheritedOptions = null)
		{
			var matchOptions = WindowSearchOptions.Merge(criteria.Options, inheritedOptions);
			var detectHiddenWindows = matchOptions.DetectHiddenWindows ?? ThreadAccessors.A_DetectHiddenWindows;

			if (detectHiddenWindows || criteria.HasNonGroupCriteria || string.IsNullOrEmpty(criteria.Group))
				return detectHiddenWindows;

			if (RequiresHiddenWindowEnumeration(criteria, matchOptions, new HashSet<string>(StringComparer.OrdinalIgnoreCase)))
				return true;

			return false;
		}

		private static bool RequiresHiddenWindowEnumeration(SearchCriteria criteria, WindowSearchOptions matchOptions, HashSet<string> visitedGroups)
		{
			if (matchOptions.DetectHiddenWindows ?? ThreadAccessors.A_DetectHiddenWindows)
				return true;

			if (string.IsNullOrEmpty(criteria.Group) || criteria.HasNonGroupCriteria || !visitedGroups.Add(criteria.Group))
				return false;

			if (!TheScript.WindowProvider.Manager.Groups.TryGetValue(criteria.Group, out var group))
				return false;

			foreach (var memberCrit in group.sc)
			{
				if (RequiresHiddenWindowEnumeration(memberCrit, WindowSearchOptions.Merge(memberCrit.Options, matchOptions), visitedGroups))
					return true;
			}

			return false;
		}
	}
}
