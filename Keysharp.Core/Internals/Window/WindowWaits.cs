namespace Keysharp.Internals.Window
{
	/// <summary>
	/// Neutral poll loop for the Win* wait functions. Kept out of <see cref="WindowInfoBase"/> because a wait is
	/// NOT an operation on a cached window view — it watches LIVE state by re-reading a condition until it holds
	/// (or a timeout elapses). The caller supplies the live condition (e.g.
	/// <c>() =&gt; !Platform.Window.GetExists(handle)</c>), so this never touches a memoized property.
	/// </summary>
	internal static class WindowWaits
	{
		/// <summary>Polls <paramref name="condition"/> every <paramref name="delayMs"/> ms until it returns true,
		/// or <paramref name="seconds"/> elapse (0 = wait indefinitely). Returns whether the condition was met.</summary>
		internal static bool Until(Func<bool> condition, double seconds, int delayMs)
		{
			var start = DateTime.UtcNow;

			while (!condition())
			{
				if (seconds != 0 && (DateTime.UtcNow - start).TotalSeconds >= seconds)
					return false;

				Keysharp.Internals.Flow.Sleep(delayMs);
			}

			return true;
		}
	}
}
