#if LINUX
namespace Keysharp.Internals.Window.Linux.Wayland
{
	/// <summary>
	/// Identifies this Keysharp process to a compositor-side overlay host (the GNOME and Cinnamon shell
	/// extensions, which retain overlay actors inside the compositor process). The extension tags every
	/// overlay with this key so it can reap our overlays when we die - see each extension's
	/// RegisterHighlightOwner / NameOwnerChanged watch / periodic /proc liveness sweep.
	/// <para/>
	/// The key is "&lt;pid&gt;:&lt;starttime&gt;" - our pid plus the process start-time jiffies from field 22
	/// of /proc/&lt;pid&gt;/stat. That stays stable for our lifetime yet differs across PID reuse, so a new
	/// process that happens to reuse our pid is not mistaken for us. It is computed once and shared by every
	/// Wayland backend bridge (the owning process is the same regardless of which compositor we talk to).
	/// </summary>
	internal static class WaylandOverlayOwner
	{
		/// <summary>The "&lt;pid&gt;:&lt;starttime&gt;" owner key for this process.</summary>
		internal static readonly string Key = Build();

		private static string Build()
		{
			var pid = Environment.ProcessId;
			var startTime = "";

			try
			{
				var stat = File.ReadAllText($"/proc/{pid}/stat");
				var end = stat.LastIndexOf(')');

				if (end >= 0 && end + 2 < stat.Length)
				{
					var fields = stat[(end + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);

					if (fields.Length > 19)
						startTime = fields[19];
				}
			}
			catch
			{
			}

			return $"{pid}:{startTime}";
		}
	}
}
#endif
