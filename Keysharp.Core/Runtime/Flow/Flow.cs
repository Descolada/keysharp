using Keysharp.Builtins;
namespace Keysharp.Runtime
{
	public static class Flow
	{
		public static Exception UnwrapException(Exception ex)
		{
			if (ex == null)
				return null;

			var current = ex;

			while (current.InnerException != null)
				current = current.InnerException;

			return current;
		}

		/// <summary>
		/// Returns whether the passed in value is true and the script is running.
		/// This is used in generated loop conditions and is one of the main poll sites
		/// for preemptive message checks in compiled code.
		/// </summary>
		public static bool IsTrueAndRunning(object obj) => IsTrueAndRunning(obj is bool ob ? ob : Script.ForceBool(obj));

		public static bool IsTrueAndRunning(bool b)
		{
			var script = Script.TheScript;

			if (script.hasExited)
				return false;

			// Compiled code cannot check the message queue before every line like AHK's interpreter.
			// Instead, each execution context tracks its own last poll time and only performs
			// a preemptive check when its current peek frequency says another one is due.
			if (script.IsCurrentThreadPreemptiveCheckDue())
				Keysharp.Internals.Flow.TryDoEvents(true, false);

			return b;
		}
	}
}
