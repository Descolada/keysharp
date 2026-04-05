using Keysharp.Builtins;
namespace Keysharp.Internals.Threading
{
	public class Threads
	{
		/// <summary>
		/// Each thread has its own TVM. This means the main UI thread gets one, and the hook thread gets one.
		/// This allows the hook thread to run #HotIf evaluations separately without interfering with the main thread.
		/// It could conceivably lead to more threads being in existence than the user allowed, for a very brief moment.
		/// This shouldn't be a problem though.
		/// Note that we use ThreadLocal<T> here because it allows initialization for each thread, whereas
		/// [ThreadStatic] doesn't.
		/// Always add 1 because a dummy entry is always added in the constructor.
		/// Add the emergency reserve so synchronous unbufferable emergencies can exceed the normal limit safely.
		/// </summary>
		private readonly ThreadVariableManager tvm = new((int)Script.TheScript.MaxThreadsTotal + Script.maxEmergencyThreads + 1);

		internal ThreadVariables CurrentThread;

		internal ThreadVariables UnderlyingThread => tvm.threadVars.TryPeekSecond();
		internal int ActivePseudoThreadCount => Math.Max(0, tvm.threadVars.Index - 1);

		public Threads()
		{
			EnsureCurrentThreadVariables();
		}

		internal void EnsureCurrentThreadVariables()
		{
			if (tvm.threadVars.Index != 0)
			{
				CurrentThread = tvm.threadVars.TryPeek();
				return;
			}

			CurrentThread = tvm.PushThreadVariables(0, true, false);//Ensure there is always one thread in existence for reference purposes, but do not increment the actual thread counter.
		}

		public bool TryBeginThread(out ThreadVariables tv)
		{
			var skip = Script.TheScript.FlowData.allowInterruption == false;
			return TryPushThreadVariables(0, skip, false, true, false, out tv);
		}

		public bool TryBeginEmergencyThread(out ThreadVariables tv)
		{
			var skip = Script.TheScript.FlowData.allowInterruption == false;
			return TryPushThreadVariables(0, skip, false, true, true, out tv);
		}

		internal bool TryReserveThreadCount(bool allowEmergencyOverflow = false)
		{
			var script = Script.TheScript;

			while (true)
			{
				var existingCount = Volatile.Read(ref script.totalExistingThreads);

				if (existingCount >= script.MaxThreadsTotal)
				{
					if (!allowEmergencyOverflow)
						return false;

					if (existingCount >= script.MaxThreadsTotal + Script.maxEmergencyThreads)
						return false;
				}

				if (Interlocked.CompareExchange(ref script.totalExistingThreads, existingCount + 1, existingCount) == existingCount)
					return true;
			}
		}

		public void EndThread(ThreadVariables tv, bool checkThread = false)
		{
			if (tv == null)
				return;

			var script = Script.TheScript;

			tv.task = false;

			PopThreadVariables(tv, checkThread);
			_ = Interlocked.Decrement(ref script.totalExistingThreads);

			script.EventScheduler.SchedulePump();
			script.ScheduleBlockedEventSchedulers();
		}

		internal bool TryPushThreadVariables(long priority, bool skipUninterruptible,
				bool isCritical, bool inc, bool allowEmergencyOverflow, out ThreadVariables tv)
		{
			var script = Script.TheScript;
			tv = null;

			if (inc)
			{
				if (!TryReserveThreadCount(allowEmergencyOverflow))
					return false;
			}

			tv = tvm.PushThreadVariables(priority, skipUninterruptible, isCritical);

			if (tv == null)
			{
				// Roll back counter (if we incremented it) and undo pause
				if (inc)
					_ = Interlocked.Decrement(ref script.totalExistingThreads);

				return false;
			}

			CurrentThread = tv;

			//We successfully pushed—and if inc == true, we’ve already counted it
			tv.task = true;
			return true;
		}

		internal bool AnyThreadsAvailable()
		{
			var script = Script.TheScript;
			return Volatile.Read(ref script.totalExistingThreads) < script.MaxThreadsTotal;
		}

		internal ThreadVariables GetThreadVariables()
		{
			return tvm.GetThreadVariables();
		}

		internal bool IsInterruptible()
		{
			var script = Script.TheScript;

			if (!script.FlowData.allowInterruption)
				return false;

			if (Volatile.Read(ref script.totalExistingThreads) == 0)//Before _ks_UserMainCode() starts to run.1
				return true;

			var tv = CurrentThread;

			if (!tv.isCritical//Added this whereas AHK doesn't check it. We should never make a critical thread interruptible.
					&& !tv.allowThreadToBeInterrupted // Those who check whether g->AllowThreadToBeInterrupted==false should then check whether it should be made true.
					&& tv.UninterruptibleDuration > -1 // Must take precedence over the below.  g_script.mUninterruptibleTime is not checked because it's supposed to go into effect during thread creation, not after the thread is running and has possibly changed the timeout via 'Thread "Interrupt"'.
					&& Environment.TickCount64 - tv.threadStartTick >= tv.UninterruptibleDuration// See big comment section above.
					&& !script.FlowData.callingCritical // In case of "Critical" on the first line.  See v2.0 comment above.
			   )
			{
				// Once the thread becomes interruptible by any means, g->ThreadStartTime/UninterruptibleDuration
				// can never matter anymore because only Critical (never "Thread Interrupt") can turn off the
				// interruptibility again, and it resets g->UninterruptibleDuration.
					tv.allowThreadToBeInterrupted = true; // Avoids issues with 49.7 day limit of 32-bit TickCount, and also helps performance future callers of this function (they can skip most of the checking above).

					if (!tv.isCritical)
						tv.configData.peekFrequency = ThreadVariables.DefaultPeekFrequency;
				}

			return tv.allowThreadToBeInterrupted;
		}

		internal void LaunchThreadInMain(Action act, long priority = 0, bool skipUninterruptible = false,
							 bool isCritical = false)
		{
			try
			{
				Script.TheScript.UIEventScheduler.EnqueueThreadLaunch(priority, skipUninterruptible, isCritical, act);
			}
			catch (Exception ex) when (ex.InnerException is not null)
			{
				ExceptionDispatchInfo.Throw(ex.InnerException);
			}
		}

		internal void PopThreadVariables(ThreadVariables tv, bool checkThread = false)
		{
			tvm.PopThreadVariables(tv, checkThread);
			CurrentThread = GetThreadVariables();
		}
	}
}
