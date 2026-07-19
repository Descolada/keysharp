namespace Keysharp.Internals.ExtensionMethods
{
	/// <summary>
	/// Extension methods for <see cref="Task"/> that wait for completion WITHOUT freezing the message loop. Both
	/// variants keep the UI / windows responsive by pumping messages while they wait; they differ in whether the wait
	/// is INTERRUPTIBLE — i.e. whether new pseudo-threads (hotkey / timer subroutines) may launch during it. This
	/// mirrors the engine's <see cref="Keysharp.Internals.Flow.Sleep"/> (interruptible) vs
	/// <see cref="Keysharp.Internals.Flow.SleepWithoutInterruption"/> (pumps, but the current thread stays
	/// uninterruptible) pair — pick by whether the waiting code may safely be preempted.
	/// </summary>
	internal static class TaskExtensions
	{
		/// <summary>
		/// Waits up to <paramref name="timeoutMs"/> for <paramref name="task"/>, pumping the message loop but keeping
		/// the current pseudo-thread UNINTERRUPTIBLE — no new hotkey/timer subroutine launches mid-wait. Use this when
		/// the wait sits inside a subroutine that must run atomically w.r.t. the thread scheduler (e.g. an overlay
		/// teardown issued from a hotkey action, where launching a nested subroutine mid-hide could reenter and tangle
		/// state). Returns true if the task completed within the timeout, false on timeout.
		/// </summary>
		internal static bool WaitWithoutInterruption(this Task task, int timeoutMs) => PumpUntil(task, timeoutMs, interruptible: false);

		/// <summary>Like <see cref="WaitWithoutInterruption(Task, int)"/>, but waits indefinitely.</summary>
		internal static void WaitWithoutInterruption(this Task task) => PumpForever(task, interruptible: false);

		/// <summary>
		/// Waits up to <paramref name="timeoutMs"/> for <paramref name="task"/>, pumping the message loop AND allowing
		/// interruption — new pseudo-threads (hotkey/timer subroutines) may launch during the wait, exactly as a plain
		/// AHK <c>Sleep</c> would. Use this when the waiting code is happy to be preempted while the task runs. Returns
		/// true if the task completed within the timeout, false on timeout.
		/// </summary>
		internal static bool WaitInterruptible(this Task task, int timeoutMs) => PumpUntil(task, timeoutMs, interruptible: true);

		/// <summary>Like <see cref="WaitInterruptible(Task, int)"/>, but waits indefinitely.</summary>
		internal static void WaitInterruptible(this Task task) => PumpForever(task, interruptible: true);

		private static bool PumpUntil(Task task, int timeoutMs, bool interruptible)
		{
			if (task == null)
				return true;

			// These helpers are also used by cold-start/background compositor probes. Only the fully initialized
			// script's main/UI thread may pump Keysharp events; a worker must never reach Flow.Sleep*, which is
			// intentionally backed by Script.TheScript and its thread scheduler.
			if (Script.TheScript?.CanPumpTaskWait != true)
				return task.Wait(timeoutMs);

			var deadline = Environment.TickCount64 + timeoutMs;

			while (!task.Wait(10))
			{
				if (Environment.TickCount64 >= deadline)
					return false;

				Pump(interruptible);
			}

			return true;
		}

		private static void PumpForever(Task task, bool interruptible)
		{
			if (task == null)
				return;

			if (Script.TheScript?.CanPumpTaskWait != true)
			{
				task.Wait();
				return;
			}

			while (!task.Wait(10))
				Pump(interruptible);
		}

		// One pass of the message loop. interruptible => plain Sleep (new pseudo-threads may launch); otherwise
		// SleepWithoutInterruption (messages pumped, but the current thread stays uninterruptible).
		private static void Pump(bool interruptible)
		{
			if (interruptible)
				Keysharp.Internals.Flow.Sleep(-1);
			else
				Keysharp.Internals.Flow.SleepWithoutInterruption();
		}
	}
}
