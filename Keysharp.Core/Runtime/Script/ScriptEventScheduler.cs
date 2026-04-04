using Keysharp.Builtins;
using Keysharp.Internals.Invoke;
using Keysharp.Runtime.Keyboard;
using Keysharp.Internals.Threading;
using Keysharp.Internals.Window;
using System.Runtime.ExceptionServices;

namespace Keysharp.Runtime
{
	public partial class Script
	{
		private ThreadLocal<ScriptEventScheduler> eventSchedulers;
		internal ScriptEventScheduler uiEventScheduler;
		internal ScriptEventScheduler EventScheduler => (eventSchedulers ??= new(() => new(this, Thread.CurrentThread.ManagedThreadId, IsOnMainThread), true)).Value;

		internal ScriptEventScheduler UIEventScheduler
			=> uiEventScheduler ?? throw new InvalidOperationException("UI event scheduler has not been bound yet.");

		internal ScriptEventScheduler CurrentSchedulerIfCreated
			=> eventSchedulers != null && eventSchedulers.IsValueCreated ? eventSchedulers.Value : null;

		internal void ScheduleAllEventSchedulers()
			=> ScheduleEventSchedulers(static _ => true);

		internal void ScheduleBlockedEventSchedulers()
			=> ScheduleEventSchedulers(static scheduler => scheduler.HasBlockedQueuedWork);

		private void ScheduleEventSchedulers(Predicate<ScriptEventScheduler> shouldSchedule)
		{
			if (eventSchedulers == null)
				return;

			foreach (var scheduler in eventSchedulers.Values.ToArray())
			{
				if (scheduler != null && (shouldSchedule == null || shouldSchedule(scheduler)))
					scheduler.SchedulePump();
			}
		}
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

	internal sealed class ScriptEventSynchronizationContext(ScriptEventScheduler scheduler) : SynchronizationContext
	{
		private readonly ScriptEventScheduler scheduler = scheduler;

		public override SynchronizationContext CreateCopy()
			=> new ScriptEventSynchronizationContext(scheduler);

		public override void Post(SendOrPostCallback d, object state)
		{
			if (d != null)
				scheduler.EnqueueCallback(() => d(state));
		}

		public override void Send(SendOrPostCallback d, object state)
		{
			if (d == null)
				return;

			if (scheduler.OwnsCurrentThread)
				d(state);
			else
				_ = scheduler.InvokeSynchronous(() =>
				{
					d(state);
					return true;
				});
		}
	}

	internal sealed class ScriptEventScheduler
	{
		private readonly object gate = new();
		private readonly Lock ownedDelegateGate = new();
		private readonly HashSet<DelegateHolder> ownedDelegates = [];
		private readonly LinkedList<Func<ScriptEventExecutionResult>> interactiveQueue = new();
		private readonly LinkedList<Func<ScriptEventExecutionResult>> normalQueue = new();
		private AutoResetEvent workerPumpSignal = new(false);
		private int workerDisposed;
		private int persistentRegistrationCount;
		private bool blockedQueuedWork;
		private bool pumpScheduled;
		private int pumpDepth;
		private readonly bool isUiScheduler;
		private readonly int ownerManagedThreadId;
		private readonly Script script;

		internal ScriptEventScheduler(Script owner, int ownerManagedThreadId, bool isUiScheduler)
		{
			script = owner;
			this.ownerManagedThreadId = ownerManagedThreadId;
			this.isUiScheduler = isUiScheduler;
			if (isUiScheduler)
				owner.uiEventScheduler = this;
		}

		internal bool OwnsCurrentThread => Thread.CurrentThread.ManagedThreadId == ownerManagedThreadId;
		internal int OwnerManagedThreadId => ownerManagedThreadId;
		internal bool IsDisposed => Volatile.Read(ref workerDisposed) != 0;
		internal bool HasBlockedQueuedWork
		{
			get
			{
				lock (gate)
					return HasBlockedQueuedWorkUnsafe();
			}
		}

		internal void AdjustPersistenceRoot(int delta)
		{
			if (delta == 0 || isUiScheduler || delta > 0 && IsDisposed)
				return;

			if (delta > 0)
				_ = Interlocked.Add(ref persistentRegistrationCount, delta);
			else
			{
				var remaining = Interlocked.Add(ref persistentRegistrationCount, delta);

				if (remaining < 0)
					_ = Interlocked.Exchange(ref persistentRegistrationCount, 0);
			}

			SignalWorkerPump();
		}

		internal void RegisterOwnedDelegate(DelegateHolder holder)
		{
			if (holder == null)
				return;

			lock (ownedDelegateGate)
				_ = ownedDelegates.Add(holder);
		}

		internal void UnregisterOwnedDelegate(DelegateHolder holder)
		{
			if (holder == null)
				return;

			lock (ownedDelegateGate)
				_ = ownedDelegates.Remove(holder);
		}

		internal DelegateHolder[] GetOwnedDelegatesSnapshot()
		{
			lock (ownedDelegateGate)
				return ownedDelegates.Count != 0 ? [.. ownedDelegates] : [];
		}

		internal bool Enqueue(ScriptEventQueue queueType, Func<ScriptEventExecutionResult> tryExecute)
		{
			if (tryExecute == null)
				return false;

			lock (gate)
			{
				if (IsDisposed)
					return false;

				GetQueue(queueType).AddLast(tryExecute);
			}

			SchedulePump();
			return true;
		}

		internal bool EnqueueTimer(ScriptTimerState timer)
		{
			return timer != null
					&& timer.Callback != null
					&& Enqueue(ScriptEventQueue.Normal, () => TryExecuteTimerEvent(timer));
		}

		internal bool EnqueueThreadLaunch(long priority, bool skipUninterruptible, bool isCritical, Action action)
			=> action != null
				&& Enqueue(ScriptEventQueue.Normal, () => TryExecuteThreadLaunch(priority, skipUninterruptible, isCritical, _ => action()));

		internal bool EnqueueThreadLaunch(long priority, bool skipUninterruptible, bool isCritical, Action action, bool useTryCatch)
			=> useTryCatch
				? EnqueueThreadLaunch(priority, skipUninterruptible, isCritical, () => _ = Keysharp.Internals.Flow.TryCatch(action))
				: EnqueueThreadLaunch(priority, skipUninterruptible, isCritical, action);

		internal bool EnqueueCallback(Action action, ScriptEventQueue queueType = ScriptEventQueue.Normal)
			=> action != null
				&& Enqueue(queueType, () =>
			{
				if (script.hasExited)
					return ScriptEventExecutionResult.Dropped;

				action();
				return ScriptEventExecutionResult.Executed;
			});

		internal bool EnqueueCallback(Action action, ScriptEventQueue queueType, bool useTryCatch)
			=> useTryCatch
				? action != null
					&& Enqueue(queueType, () =>
					{
						if (script.hasExited)
							return ScriptEventExecutionResult.Dropped;

						_ = Keysharp.Internals.Flow.TryCatch(action);
						return ScriptEventExecutionResult.Executed;
					})
				: EnqueueCallback(action, queueType);

		internal T InvokeSynchronous<T>(Func<T> func)
		{
			if (func == null)
				return default;

			if (IsDisposed)
				throw new ObjectDisposedException(nameof(ScriptEventScheduler));

			if (OwnsCurrentThread)
				return func();

			if (isUiScheduler)
				return Script.InvokeOnUIThread(func);

			using var completed = new ManualResetEventSlim(false);
			ExceptionDispatchInfo captured = null;
			T result = default;

			if (!EnqueueCallback(() =>
			{
				try
				{
					result = func();
				}
				catch (Exception ex)
				{
					captured = ExceptionDispatchInfo.Capture(ex);
				}
				finally
				{
					completed.Set();
				}
			}, ScriptEventQueue.Interactive))
				throw new ObjectDisposedException(nameof(ScriptEventScheduler));

			WaitForSynchronousCompletion(completed);
			captured?.Throw();
			return result;
		}

		internal void PumpPendingEvents()
		{
			if (OwnsCurrentThread && !IsDisposed)
				PumpQueuedEvents();
		}

		private void PumpQueuedEvents()
		{
			bool blocked = false;
			bool stalledOnLocalBlock = false;
			var consecutiveInteractiveLocalBlocks = 0;
			var consecutiveNormalLocalBlocks = 0;
			var preferNormalOnce = false;

			if (!TryBeginPump())
				return;

			try
			{
				while (true)
				{
					switch (TryProcessNextQueuedEvent(ref consecutiveInteractiveLocalBlocks, ref consecutiveNormalLocalBlocks, ref preferNormalOnce))
					{
					case ScriptEventExecutionResult.Executed:
						continue;
					case ScriptEventExecutionResult.GlobalBlocked:
						blocked = true;
						return;
					case ScriptEventExecutionResult.LocalBlocked:
						stalledOnLocalBlock = true;
						return;
					case ScriptEventExecutionResult.Dropped:
						return;
					}
				}
			}
			finally
			{
				EndPump(blocked, stalledOnLocalBlock);
			}
		}

		internal void RunWorkerEventLoop()
		{
			while (!script.hasExited && !IsDisposed)
			{
				if (HasBlockedQueuedWork)
				{
					// Blocked work can become runnable either when another scheduler finishes a pseudo-thread
					// or when interruptibility naturally times out, so wait briefly rather than spinning.
					_ = WaitForWorkerPumpSignal((int)ThreadVariables.DefaultUninterruptiblePeekFrequency);
					continue;
				}

				if (!HasQueuedEvents())
				{
					if (script.Threads.ActivePseudoThreadCount == 0 && Volatile.Read(ref persistentRegistrationCount) == 0)
						return;

					_ = WaitForWorkerPumpSignal();
					continue;
				}

				PumpPendingEvents();

				if (script.Threads.ActivePseudoThreadCount > 0)
					return;
			}
		}

		internal void DisposeWorker()
		{
			if (isUiScheduler || Interlocked.Exchange(ref workerDisposed, 1) != 0)
				return;

			ClearQueues();
			DisposeOwnedTimers();
			DisposeOwnedClipboardHandlers();
			var hotkeysChanged = Keysharp.Internals.Input.Keyboard.HotkeyDefinition.DisableOwnedVariants(this);
			var hotstringsChanged = script.HotstringManager.DisableOwnedHotstrings(this);
			DisposeOwnedMessageHandlers();
			DisposeOwnedGuiHandlers();
			DisposeOwnedMenuHandlers();
			DelegateHolder.DisposeOwnedByScheduler(this);
			_ = Interlocked.Exchange(ref persistentRegistrationCount, 0);

			if (hotkeysChanged || hotstringsChanged)
				_ = Keysharp.Internals.Input.Keyboard.HotkeyDefinition.ManifestAllHotkeysHotstringsHooks();

			SignalWorkerPump();

			var signal = Interlocked.Exchange(ref workerPumpSignal, null);
			signal?.Dispose();
		}

		internal void SchedulePump()
		{
			if (IsDisposed)
				return;

			lock (gate)
			{
				if (!TryMarkPumpScheduledUnsafe())
					return;
			}

			if (isUiScheduler)
			{
				if (OwnsCurrentThread)
					Script.PostToUIThread(PumpPendingEvents);
				else
					_ = ThreadPool.UnsafeQueueUserWorkItem(static state => Script.InvokeOnUIThread(((ScriptEventScheduler)state).PumpPendingEvents), this, false);
			}
			else
				SignalWorkerPump();
		}

		private LinkedList<Func<ScriptEventExecutionResult>> GetQueue(ScriptEventQueue queueType)
			=> queueType == ScriptEventQueue.Interactive ? interactiveQueue : normalQueue;

		private bool TryBeginPump()
		{
			lock (gate)
			{
				if (pumpDepth++ != 0)
					return true;

				blockedQueuedWork = false;
				pumpScheduled = false;
				return true;
			}
		}

		private void EndPump(bool blocked, bool stalledOnLocalBlock)
		{
			var isOuterPump = false;

			lock (gate)
			{
				pumpDepth--;

				if (pumpDepth != 0)
					return;

				isOuterPump = true;
				blockedQueuedWork = blocked || stalledOnLocalBlock;
			}

			if (isOuterPump && !blocked && !stalledOnLocalBlock)
				SchedulePump();
		}

		private ScriptEventExecutionResult TryProcessNextQueuedEvent(ref int consecutiveInteractiveLocalBlocks, ref int consecutiveNormalLocalBlocks, ref bool preferNormalOnce)
		{
			if (!TryGetNextQueuedEvent(preferNormalOnce, out var queueType, out var next))
				return ScriptEventExecutionResult.Dropped;

			preferNormalOnce = false;
			var result = next();

			if (result == ScriptEventExecutionResult.GlobalBlocked)
				return result;

			if (result == ScriptEventExecutionResult.LocalBlocked)
				return HandleLocalBlock(queueType, next, ref consecutiveInteractiveLocalBlocks, ref consecutiveNormalLocalBlocks, ref preferNormalOnce)
					? ScriptEventExecutionResult.LocalBlocked
					: ScriptEventExecutionResult.Executed;

			if (result == ScriptEventExecutionResult.GlobalBlocked)
				RestoreBlockedWork(queueType, next);

			consecutiveInteractiveLocalBlocks = 0;
			consecutiveNormalLocalBlocks = 0;
			return result == ScriptEventExecutionResult.Dropped
				? ScriptEventExecutionResult.Executed
				: result;
		}

		private bool TryGetNextQueuedEvent(bool preferNormal, out ScriptEventQueue queueType, out Func<ScriptEventExecutionResult> next)
		{
			lock (gate)
			{
				if (preferNormal && normalQueue.Count != 0)
				{
					queueType = ScriptEventQueue.Normal;
					next = normalQueue.First.Value;
					normalQueue.RemoveFirst();
					return true;
				}

				if (interactiveQueue.Count != 0)
				{
					queueType = ScriptEventQueue.Interactive;
					next = interactiveQueue.First.Value;
					interactiveQueue.RemoveFirst();
					return true;
				}

				if (normalQueue.Count != 0)
				{
					queueType = ScriptEventQueue.Normal;
					next = normalQueue.First.Value;
					normalQueue.RemoveFirst();
					return true;
				}

				queueType = ScriptEventQueue.Normal;
				next = null;
				return false;
			}
		}

		private void RestoreBlockedWork(ScriptEventQueue queueType, Func<ScriptEventExecutionResult> work)
		{
			lock (gate)
				GetQueue(queueType).AddFirst(work);
		}

		private bool HandleLocalBlock(ScriptEventQueue queueType, Func<ScriptEventExecutionResult> work, ref int consecutiveInteractiveLocalBlocks, ref int consecutiveNormalLocalBlocks, ref bool preferNormalOnce)
		{
			int interactiveCount;
			int normalCount;

			lock (gate)
			{
				GetQueue(queueType).AddLast(work);
				interactiveCount = interactiveQueue.Count;
				normalCount = normalQueue.Count;
			}

			if (queueType == ScriptEventQueue.Interactive)
			{
				consecutiveInteractiveLocalBlocks++;
				consecutiveNormalLocalBlocks = 0;

				// If every currently queued interactive event is only locally blocked, allow normal
				// queued work to proceed rather than stalling the entire pump behind one hotkey/hotstring.
				if (normalCount != 0 && consecutiveInteractiveLocalBlocks >= interactiveCount)
				{
					consecutiveInteractiveLocalBlocks = 0;
					preferNormalOnce = true;
					return false;
				}

				return consecutiveInteractiveLocalBlocks >= interactiveCount;
			}

			consecutiveNormalLocalBlocks++;
			consecutiveInteractiveLocalBlocks = 0;
			return consecutiveNormalLocalBlocks >= normalCount;
		}

		private bool HasQueuedEvents()
		{
			lock (gate)
				return HasQueuedEventsUnsafe();
		}

		internal void SignalWorkerPump()
		{
			try
			{
				_ = Volatile.Read(ref workerPumpSignal)?.Set();
			}
			catch (ObjectDisposedException)
			{
			}
		}

		private bool WaitForWorkerPumpSignal()
			=> WaitForWorkerPumpSignal(Timeout.Infinite);

		private bool WaitForWorkerPumpSignal(int timeout)
		{
			try
			{
				return Volatile.Read(ref workerPumpSignal)?.WaitOne(timeout) == true;
			}
			catch (ObjectDisposedException)
			{
				return false;
			}
		}

		private bool HasQueuedEventsUnsafe() => interactiveQueue.Count != 0 || normalQueue.Count != 0;
		private bool HasBlockedQueuedWorkUnsafe() => blockedQueuedWork && HasQueuedEventsUnsafe();

		private bool TryMarkPumpScheduledUnsafe()
		{
			if (!HasQueuedEventsUnsafe() || pumpScheduled || pumpDepth != 0)
				return false;

			pumpScheduled = true;
			return true;
		}

		private ScriptEventExecutionResult TryExecuteTimerEvent(ScriptTimerState timer)
		{
			var timers = script.FlowData.timers;

			if (script.hasExited)
			{
				timers.ReleaseQueuedTimer(timer);
				return ScriptEventExecutionResult.Dropped;
			}

			var timerRegistration = timers.Find(timer.Callback, timer.OwnerScheduler);

			if (!ReferenceEquals(timerRegistration, timer) || timer.Callback == null || !timer.Enabled)
			{
				timers.ReleaseQueuedTimer(timer);
				return ScriptEventExecutionResult.Dropped;
			}

			var threads = script.Threads;

			if ((!Keysharp.Builtins.Ks.A_AllowTimers.Ab() && script.totalExistingThreads > 0)
					|| !threads.AnyThreadsAvailable()
					|| !threads.IsInterruptible())
			{
				timers.ReleaseQueuedTimer(timer);
				return ScriptEventExecutionResult.Dropped;
			}

			timers.MarkCallbackStarted(timer);

			var startResult = TryExecuteThreadLaunch(timer.Priority, true, false, btv =>
			{
				btv.currentTimer = timer;
				btv.eventInfo = timer.Callback;
				_ = Keysharp.Internals.Flow.TryCatch(() => _ = timer.Callback.Call());
			});

			timers.MarkCallbackFinished(timer);

			if (startResult != ScriptEventExecutionResult.Executed)
			{
				if (timer.Callback == null)
					script.ExitIfNotPersistent();

				return ScriptEventExecutionResult.Dropped;
			}

			if (timer.Callback == null)
				script.ExitIfNotPersistent();

			return ScriptEventExecutionResult.Executed;
		}

		// Pseudo-thread execution has three separate concerns:
		// 1. Admission: TryStartPseudoThread decides whether a pseudo-thread may start.
		// 2. Affinity: TryExecuteThreadLaunch/TryInvokePseudoThread either execute immediately on the
		//    owning scheduler thread or marshal through InvokeSynchronous to get there.
		// 3. Execution policy: the scheduler core is raw. Semantic callers that want handled behavior
		//    wrap their callback with Keysharp.Internals.Flow.TryCatch before calling into the scheduler. The scheduler
		//    runners always end the pseudo-thread in one place.
		internal ScriptEventExecutionResult TryExecuteThreadLaunch(long priority, bool skipUninterruptible, bool isCritical, Action<ThreadVariables> action, bool allowEmergencyOverflow = false)
		{
			if (action == null || IsDisposed)
				return ScriptEventExecutionResult.Dropped;

			if (OwnsCurrentThread)
			{
				var status = TryStartPseudoThread(priority, skipUninterruptible, isCritical, allowEmergencyOverflow, out var threadVariables);
				return status != ScriptEventExecutionResult.Executed
					? status
					: RunPseudoThreadAction(threadVariables, action);
			}

			return InvokeSynchronous(() =>
			{
				var status = TryStartPseudoThread(priority, skipUninterruptible, isCritical, allowEmergencyOverflow, out var threadVariables);
				return status != ScriptEventExecutionResult.Executed
					? status
					: RunPseudoThreadAction(threadVariables, action);
			});
		}

		internal ScriptEventExecutionResult TryInvokePseudoThread<T>(long priority, bool skipUninterruptible, bool isCritical, Func<ThreadVariables, T> action, out T result, bool allowEmergencyOverflow = false)
		{
			result = default;

			if (action == null || IsDisposed)
				return ScriptEventExecutionResult.Dropped;

			(ScriptEventExecutionResult status, T result) execution;

			if (OwnsCurrentThread)
			{
				var status = TryStartPseudoThread(priority, skipUninterruptible, isCritical, allowEmergencyOverflow, out var threadVariables);
				execution = status != ScriptEventExecutionResult.Executed
					? (status, default)
					: EvaluatePseudoThreadFunc(threadVariables, () => action(threadVariables));
			}
			else
			{
				execution = InvokeSynchronous(() =>
				{
					var status = TryStartPseudoThread(priority, skipUninterruptible, isCritical, allowEmergencyOverflow, out var threadVariables);
					return status != ScriptEventExecutionResult.Executed
						? (status, default(T))
						: EvaluatePseudoThreadFunc(threadVariables, () => action(threadVariables));
				});
			}

			result = execution.result;
			return execution.status;
		}

		private ScriptEventExecutionResult RunPseudoThreadAction(ThreadVariables threadVariables, Action<ThreadVariables> action)
		{
			try
			{
				action(threadVariables);
				return ScriptEventExecutionResult.Executed;
			}
			finally
			{
				script.Threads.EndThread(threadVariables);
			}
		}

		private (ScriptEventExecutionResult status, T result) EvaluatePseudoThreadFunc<T>(ThreadVariables threadVariables, Func<T> func)
		{
			try
			{
				return (ScriptEventExecutionResult.Executed, func());
			}
			finally
			{
				script.Threads.EndThread(threadVariables);
			}
		}

		private void WaitForSynchronousCompletion(ManualResetEventSlim completed)
		{
			var waitingScheduler = script.CurrentSchedulerIfCreated;

			while (!completed.Wait(20))
			{
				if (script.hasExited)
					throw new Keysharp.Builtins.Flow.UserRequestedExitException();

				if (IsDisposed)
					throw new ObjectDisposedException(nameof(ScriptEventScheduler));

				PumpDuringSynchronousWait(waitingScheduler);
			}
		}

		private void PumpDuringSynchronousWait(ScriptEventScheduler waitingScheduler)
		{
			if (script.IsOnMainThread)
			{
#if WINDOWS
				Application.DoEvents();
#else
				Application.Instance?.RunIteration();
#endif
			}

			waitingScheduler?.PumpPendingEvents();
		}

		private ScriptEventExecutionResult TryStartPseudoThread(long priority, bool skipUninterruptible, bool isCritical, bool allowEmergencyOverflow, out ThreadVariables threadVariables)
		{
			threadVariables = null;

			if (script.hasExited)
				return ScriptEventExecutionResult.Dropped;

			var threads = script.Threads;

			if (!allowEmergencyOverflow && !threads.AnyThreadsAvailable())
				return ScriptEventExecutionResult.GlobalBlocked;

			if (!skipUninterruptible && !threads.IsInterruptible())
				return ScriptEventExecutionResult.GlobalBlocked;

			if (priority < threads.CurrentThread.priority)
				return ScriptEventExecutionResult.Dropped;

			return threads.TryPushThreadVariables(priority, skipUninterruptible, isCritical, true, allowEmergencyOverflow, out threadVariables)
				? ScriptEventExecutionResult.Executed
				: ScriptEventExecutionResult.GlobalBlocked;
		}

		private void ClearQueues()
		{
			lock (gate)
			{
				interactiveQueue.Clear();
				normalQueue.Clear();
				blockedQueuedWork = false;
				pumpScheduled = false;
			}
		}

		private void DisposeOwnedTimers()
		{
			if (script.FlowData.timers.RemoveOwned(this))
				script.ExitIfNotPersistent();
		}

		private void DisposeOwnedClipboardHandlers()
		{
			if (script.ClipFunctions.RemoveOwned(this))
				script.UpdateClipboardMonitoring();
		}

		private void DisposeOwnedMessageHandlers()
		{
			foreach (var kv in script.GuiData.onMessageHandlers.ToArray())
			{
				if (kv.Value == null || !kv.Value.RemoveOwned(this))
					continue;

				if (kv.Value.IsEmpty)
					_ = script.GuiData.onMessageHandlers.TryRemove(kv.Key, out _);
			}
		}

		private void DisposeOwnedGuiHandlers()
		{
			foreach (var gui in new HashSet<Gui>(script.GuiData.allGuiHwnds.Values))
			{
				_ = gui?.form?.RemoveOwnedHandlers(this);

				foreach (var control in gui?.controls?.Values.OfType<Gui.Control>() ?? [])
					_ = control?.RemoveOwnedHandlers(this);
			}
		}

		private void DisposeOwnedMenuHandlers()
		{
			foreach (var kv in script.GuiData.allMenus.ToArray())
			{
				if (!kv.Value.TryGetTarget(out var menu))
				{
					_ = script.GuiData.allMenus.TryRemove(kv.Key, out _);
					continue;
				}

				_ = menu.RemoveOwnedHandlers(this);
			}
		}
	}
}
