using Keysharp.Core.Common.Invoke;
using Keysharp.Core.Common.Threading;

namespace Keysharp.Scripting
{
	public partial class Script
	{
		private ScriptEventScheduler eventScheduler;
		internal ScriptEventScheduler EventScheduler => eventScheduler ??= new(this);
	}

	internal enum ScriptEventKind
	{
		Timer,
		ThreadLaunch,
		Callback,
		MessageCallback,
		Hotkey,
		Hotstring
	}

	internal enum ScriptEventQueue
	{
		Normal,
		Interactive
	}

	internal enum ScriptEventExecutionResult
	{
		Executed,
		GlobalBlocked,
		LocalBlocked,
		Dropped
	}

	internal sealed class ScriptEvent(ScriptEventKind kind, ScriptEventQueue queueType, long priority, Func<ScriptEventExecutionResult> tryExecute)
	{
		internal ScriptEventKind Kind { get; } = kind;
		internal long Priority { get; } = priority;
		internal ScriptEventQueue QueueType { get; } = queueType;
		internal Func<ScriptEventExecutionResult> TryExecute { get; } = tryExecute;
	}

	internal sealed class ScriptEventScheduler(Script owner)
	{
		private readonly object gate = new();
		// Interactive events are drained before normal ones so user-facing input actions
		// such as hotkeys and hotstrings remain responsive.
		private readonly Queue<ScriptEvent> interactiveQueue = new();
		private readonly Queue<ScriptEvent> normalQueue = new();
		private bool pumpScheduled;
		private bool pumping;
		private readonly Script script = owner;

		internal void Enqueue(ScriptEvent scriptEvent)
		{
			if (scriptEvent == null)
				return;

			lock (gate)
			{
				GetQueue(scriptEvent.QueueType).Enqueue(scriptEvent);
			}

			SchedulePump();
		}

		internal void EnqueueTimer(TimerWithTag timer, IFuncObj func, bool once)
		{
			if (timer == null || func == null)
				return;

			Enqueue(new ScriptEvent(
				ScriptEventKind.Timer,
				ScriptEventQueue.Normal,
				timer.Tag.Ai(),
				() => TryExecuteTimerEvent(timer, func, once)));
		}

		internal void EnqueueThreadLaunch(long priority, bool skipUninterruptible, bool isCritical, Action action, bool useTryCatch)
		{
			if (action == null)
				return;

			Enqueue(new ScriptEvent(
				ScriptEventKind.ThreadLaunch,
				ScriptEventQueue.Normal,
				priority,
				() => TryExecuteThreadLaunch(priority, skipUninterruptible, isCritical, action, useTryCatch)));
		}

		internal void EnqueueCallback(Action action, ScriptEventQueue queueType = ScriptEventQueue.Normal, bool useTryCatch = true)
		{
			if (action == null)
				return;

			Enqueue(new ScriptEvent(
				ScriptEventKind.Callback,
				queueType,
				0,
				() => TryExecuteCallback(action, useTryCatch)));
		}

		internal ScriptEventExecutionResult CheckPseudoThreadAdmission(long priority, bool skipUninterruptible)
		{
			if (script.hasExited)
				return ScriptEventExecutionResult.Dropped;

			var threads = script.Threads;

			if (!threads.AnyThreadsAvailable())
				return ScriptEventExecutionResult.GlobalBlocked;

			if (!skipUninterruptible && !threads.IsInterruptible())
				return ScriptEventExecutionResult.GlobalBlocked;

			if (priority < threads.CurrentThread.priority)
				return ScriptEventExecutionResult.Dropped;

			return ScriptEventExecutionResult.Executed;
		}

		internal (bool, ThreadVariables) BeginPseudoThread(long priority, bool skipUninterruptible, bool isCritical)
			=> script.Threads.PushThreadVariables(priority, skipUninterruptible, isCritical, false, true);

		internal void PumpPendingEvents()
		{
			bool blocked = false;
			bool stalledOnLocalBlock = false;
			var consecutiveInteractiveLocalBlocks = 0;
			var consecutiveNormalLocalBlocks = 0;
			var preferNormalOnce = false;

			lock (gate)
			{
				if (pumping)
					return;

				pumping = true;
				pumpScheduled = false;
			}

			try
			{
				while (true)
				{
					ScriptEvent next;

					lock (gate)
					{
						next = PeekNextUnsafe(preferNormalOnce);
						preferNormalOnce = false;

						if (next == null)
							return;
					}

					var result = next.TryExecute();

					if (result == ScriptEventExecutionResult.GlobalBlocked)
					{
						blocked = true;
						return;
					}

					if (result == ScriptEventExecutionResult.LocalBlocked)
					{
						var highPriorityCount = 0;
						var normalCount = 0;

						lock (gate)
						{
							DequeueUnsafe(next);
							GetQueue(next.QueueType).Enqueue(next);
							highPriorityCount = interactiveQueue.Count;
							normalCount = normalQueue.Count;
						}

						if (next.QueueType == ScriptEventQueue.Interactive)
						{
							consecutiveInteractiveLocalBlocks++;
							consecutiveNormalLocalBlocks = 0;

							// If every currently queued interactive event is only locally blocked, allow normal
							// queued work to proceed rather than stalling the entire pump behind one hotkey/hotstring.
							if (normalCount != 0 && consecutiveInteractiveLocalBlocks >= highPriorityCount)
							{
								consecutiveInteractiveLocalBlocks = 0;
								preferNormalOnce = true;
								continue;
							}

							if (consecutiveInteractiveLocalBlocks >= highPriorityCount)
							{
								stalledOnLocalBlock = true;
								return;
							}
						}
						else
						{
							consecutiveNormalLocalBlocks++;
							consecutiveInteractiveLocalBlocks = 0;

							if (consecutiveNormalLocalBlocks >= normalCount)
							{
								stalledOnLocalBlock = true;
								return;
							}
						}

						continue;
					}

					lock (gate)
					{
						DequeueUnsafe(next);
					}

					consecutiveInteractiveLocalBlocks = 0;
					consecutiveNormalLocalBlocks = 0;
				}
			}
			finally
			{
				lock (gate)
				{
					pumping = false;
				}

				if (!blocked && !stalledOnLocalBlock)
					SchedulePump();
			}
		}

		internal void SchedulePump()
		{
			bool shouldSchedule = false;

			lock (gate)
			{
				shouldSchedule = TryMarkPumpScheduledUnsafe();
			}

			if (shouldSchedule)
				Script.PostToUIThread(PumpPendingEvents);
		}

		private void DequeueUnsafe(ScriptEvent scriptEvent)
		{
			var queue = GetQueue(scriptEvent.QueueType);

			if (queue.Count != 0 && ReferenceEquals(queue.Peek(), scriptEvent))
				_ = queue.Dequeue();
		}

		private Queue<ScriptEvent> GetQueue(ScriptEventQueue queueType)
			=> queueType == ScriptEventQueue.Interactive ? interactiveQueue : normalQueue;

		private bool HasQueuedEventsUnsafe() => interactiveQueue.Count != 0 || normalQueue.Count != 0;

		private bool TryMarkPumpScheduledUnsafe()
		{
			if (!HasQueuedEventsUnsafe() || pumpScheduled || pumping)
				return false;

			pumpScheduled = true;
			return true;
		}

		private ScriptEvent PeekNextUnsafe(bool preferNormal = false)
			=> preferNormal && normalQueue.Count != 0
				? normalQueue.Peek()
				: interactiveQueue.Count != 0
				? interactiveQueue.Peek()
				: normalQueue.Count != 0
					? normalQueue.Peek()
					: null;

		private ScriptEventExecutionResult TryExecuteTimerEvent(TimerWithTag timer, IFuncObj func, bool once)
		{
			if (script.hasExited)
			{
				timer.ClearSchedulerQueueState();
				return ScriptEventExecutionResult.Dropped;
			}

			if (!script.FlowData.timers.TryGetValue(func, out var existingTimer) || !ReferenceEquals(existingTimer, timer))
			{
				timer.ClearSchedulerQueueState();
				return ScriptEventExecutionResult.Dropped;
			}

			var threads = script.Threads;

			if ((!Keysharp.Core.Ks.A_AllowTimers.Ab() && script.totalExistingThreads > 0)
					|| !threads.AnyThreadsAvailable()
					|| !threads.IsInterruptible())
				return ScriptEventExecutionResult.GlobalBlocked;

			var startResult = TryBeginPseudoThread(timer.Tag.Ai(), true, false, out var btv);

			if (startResult != ScriptEventExecutionResult.Executed)
				return startResult;

			_ = Flow.TryCatch(() =>
			{
				btv.Item2.currentTimer = timer;
				btv.Item2.eventInfo = func;
				_ = func.Call();
				_ = threads.EndThread(btv);
			}, true, btv);

			if (once)
			{
				_ = script.FlowData.timers.TryRemove(func, out _);
				timer.Stop();
				timer.Dispose();
				script.ExitIfNotPersistent();
			}
			else
			{
				timer.ClearSchedulerQueueState();

				if (!script.FlowData.timers.ContainsKey(func))
					script.ExitIfNotPersistent();
			}

			return ScriptEventExecutionResult.Executed;
		}

		private ScriptEventExecutionResult TryExecuteThreadLaunch(long priority, bool skipUninterruptible, bool isCritical, Action action, bool useTryCatch)
		{
			var startResult = TryBeginPseudoThread(priority, skipUninterruptible, isCritical, out var btv);

			if (startResult != ScriptEventExecutionResult.Executed)
				return startResult;

			var threads = script.Threads;

			if (useTryCatch)
			{
				_ = Flow.TryCatch(() =>
				{
					action();
					_ = threads.EndThread(btv);
				}, true, btv);
			}
			else
			{
				action();
				_ = threads.EndThread(btv);
			}

			return ScriptEventExecutionResult.Executed;
		}

		private ScriptEventExecutionResult TryBeginPseudoThread(long priority, bool skipUninterruptible, bool isCritical, out (bool, ThreadVariables) btv)
		{
			btv = default;
			var admissionResult = CheckPseudoThreadAdmission(priority, skipUninterruptible);

			if (admissionResult != ScriptEventExecutionResult.Executed)
				return admissionResult;

			btv = BeginPseudoThread(priority, skipUninterruptible, isCritical);
			return btv.Item1 ? ScriptEventExecutionResult.Executed : ScriptEventExecutionResult.GlobalBlocked;
		}

		private ScriptEventExecutionResult TryExecuteCallback(Action action, bool useTryCatch)
		{
			if (script.hasExited)
				return ScriptEventExecutionResult.Dropped;

			if (useTryCatch)
				_ = Flow.TryCatch(action, false, (false, null));
			else
				action();

			return ScriptEventExecutionResult.Executed;
		}
	}
}
