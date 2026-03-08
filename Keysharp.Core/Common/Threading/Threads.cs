namespace Keysharp.Core.Common.Threading
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

		public Threads()
		{
			_ = PushThreadVariables(0, true, false, true);//Ensure there is always one thread in existence for reference purposes, but do not increment the actual thread counter.
		}

		public (bool, ThreadVariables) BeginThread(bool onlyIfEmpty = false)
		{
			var skip = Script.TheScript.FlowData.allowInterruption == false;//This will be false when exiting the program.
			return PushThreadVariables(0, skip, false, onlyIfEmpty, true);
		}

		public (bool, ThreadVariables) BeginSynchronousEmergencyThread(bool onlyIfEmpty = false)
		{
			var skip = Script.TheScript.FlowData.allowInterruption == false;
			return PushThreadVariables(0, skip, false, onlyIfEmpty, true, true);
		}

		public object EndThread((bool, ThreadVariables) btv, bool checkThread = false)
		{
			var script = Script.TheScript;
			var pushed = btv.Item1;

			if (pushed)
				btv.Item2.task = false;

			PopThreadVariables(pushed, checkThread);
			_ = Interlocked.Decrement(ref script.totalExistingThreads);

			script.EventScheduler.SchedulePump();

			return null;
		}

		public (bool, ThreadVariables) PushThreadVariables(long priority, bool skipUninterruptible,
				bool isCritical = false, bool onlyIfEmpty = false, bool inc = false, bool allowEmergencyOverflow = false)
		{
			var script = Script.TheScript;
			var max = script.MaxThreadsTotal;

			// Fast path: mimic what tvm would have done
			if (onlyIfEmpty && tvm.threadVars.Index != 0)
			{
				return (false, tvm.threadVars.TryPeek());
			}

			if (inc)
			{
				while (true)
				{
					var existingCount = Volatile.Read(ref script.totalExistingThreads);

					if (existingCount >= max)
					{
						if (!allowEmergencyOverflow)
							return (false, tvm.threadVars.TryPeek());

						if (existingCount >= max + Script.maxEmergencyThreads)
							return (false, tvm.threadVars.TryPeek());
					}

					if (Interlocked.CompareExchange(ref script.totalExistingThreads, existingCount + 1, existingCount) == existingCount)
						break;
				}
			}

			var (success, tv) = tvm.PushThreadVariables(priority, skipUninterruptible, isCritical, onlyIfEmpty);

			if (!success)
			{
				// Roll back counter (if we incremented it) and undo pause
				if (inc)
					_ = Interlocked.Decrement(ref script.totalExistingThreads);

				return (success, tv);
			}

			CurrentThread = tv;
			script.RestartPreemptiveMessageTimer();

			//We successfully pushed—and if inc == true, we’ve already counted it
			tv.task = true;
			return (true, tv);
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

					script.RestartPreemptiveMessageTimer();
				}

			return tv.allowThreadToBeInterrupted;
		}

		internal void LaunchThreadInMain(Action act, long priority = 0, bool skipUninterruptible = false,
							 bool isCritical = false)
		{
			try
			{
				Script.TheScript.EventScheduler.EnqueueThreadLaunch(priority, skipUninterruptible, isCritical, act, true);
			}
			catch (Exception ex)
			{
				if (ex.InnerException != null)
					throw ex.InnerException;
				else
					throw;//Do not pass ex because it will reset the stack information.
			}
		}

		internal void LaunchInThread(long priority, bool skipUninterruptible,
									 bool isCritical, object func, object[] o, bool tryCatch)
		{
			try
			{
				void Execute()
				{
					object ret = null;

					if (func is VariadicFunction vf)
						ret = vf(o);
					else if (func is IFuncObj ifo)
						ret = ifo.Call(o);
					else
						ret = "";
				}

				Script.TheScript.EventScheduler.EnqueueThreadLaunch(priority, skipUninterruptible, isCritical, Execute, tryCatch);
			}
			catch (Exception ex)
			{
				if (ex.InnerException != null)
					throw ex.InnerException;
				else
					throw;//Do not pass ex because it will reset the stack information.
			}
		}

		internal void PopThreadVariables(bool pushed, bool checkThread = false)
		{
			tvm.PopThreadVariables(pushed, checkThread);
			CurrentThread = GetThreadVariables();
			Script.TheScript.RestartPreemptiveMessageTimer();
		}
	}
}
