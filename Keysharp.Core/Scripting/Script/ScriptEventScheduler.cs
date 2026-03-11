using Keysharp.Core.Common.Invoke;
using Keysharp.Core.Common.Keyboard;
using Keysharp.Core.Common.Threading;
using Keysharp.Core.Common.Window;
using System.Runtime.ExceptionServices;

namespace Keysharp.Scripting
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

		internal void SignalAllEventSchedulers()
		{
			if (eventSchedulers != null)
				foreach (var scheduler in eventSchedulers.Values.ToArray())
					scheduler?.SignalWorkerPump();
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
		private readonly Queue<Func<ScriptEventExecutionResult>> interactiveQueue = new();
		private readonly Queue<Func<ScriptEventExecutionResult>> normalQueue = new();
		private AutoResetEvent workerPumpSignal = new(false);
		private int workerDisposed;
		private int persistentRegistrationCount;
		private bool pumpScheduled;
		private bool pumping;
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

				GetQueue(queueType).Enqueue(tryExecute);
			}

			SchedulePump();
			return true;
		}

		internal bool EnqueueTimer(TimerWithTag timer, IFuncObj func, bool once)
		{
			return timer != null
					&& func != null
					&& Enqueue(ScriptEventQueue.Normal, () => TryExecuteTimerEvent(timer, func, once));
		}

		internal bool EnqueueThreadLaunch(long priority, bool skipUninterruptible, bool isCritical, Action action, bool useTryCatch)
		{
			return action != null
					&& Enqueue(ScriptEventQueue.Normal, () => TryExecuteThreadLaunch(priority, skipUninterruptible, isCritical, action, useTryCatch));
		}

		internal bool EnqueueCallback(Action action, ScriptEventQueue queueType = ScriptEventQueue.Normal, bool useTryCatch = true)
		{
			return action != null
					&& Enqueue(queueType, () =>
				{
					if (script.hasExited)
						return ScriptEventExecutionResult.Dropped;

					if (useTryCatch)
						_ = Flow.TryCatch(action);
					else
						action();

					return ScriptEventExecutionResult.Executed;
				});
		}

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
			}, ScriptEventQueue.Interactive, useTryCatch: false))
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
			var hotkeysChanged = HotkeyDefinition.DisableOwnedVariants(this);
			var hotstringsChanged = script.HotstringManager.DisableOwnedHotstrings(this);
			DisposeOwnedMessageHandlers();
			DisposeOwnedGuiHandlers();
			DisposeOwnedMenuHandlers();
			DelegateHolder.DisposeOwnedByScheduler(this);
			_ = Interlocked.Exchange(ref persistentRegistrationCount, 0);

			if (hotkeysChanged || hotstringsChanged)
				_ = HotkeyDefinition.ManifestAllHotkeysHotstringsHooks();

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
				Script.PostToUIThread(PumpPendingEvents);
			else
				SignalWorkerPump();
		}

		private Queue<Func<ScriptEventExecutionResult>> GetQueue(ScriptEventQueue queueType)
			=> queueType == ScriptEventQueue.Interactive ? interactiveQueue : normalQueue;

		private bool TryBeginPump()
		{
			lock (gate)
			{
				if (pumping)
					return false;

				pumping = true;
				pumpScheduled = false;
				return true;
			}
		}

		private void EndPump(bool blocked, bool stalledOnLocalBlock)
		{
			lock (gate)
				pumping = false;

			if (!blocked && !stalledOnLocalBlock)
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

			RemoveHead(queueType, next);
			consecutiveInteractiveLocalBlocks = 0;
			consecutiveNormalLocalBlocks = 0;
			return ScriptEventExecutionResult.Executed;
		}

		private bool TryGetNextQueuedEvent(bool preferNormal, out ScriptEventQueue queueType, out Func<ScriptEventExecutionResult> next)
		{
			lock (gate)
			{
				if (preferNormal && normalQueue.Count != 0)
				{
					queueType = ScriptEventQueue.Normal;
					next = normalQueue.Peek();
					return true;
				}

				if (interactiveQueue.Count != 0)
				{
					queueType = ScriptEventQueue.Interactive;
					next = interactiveQueue.Peek();
					return true;
				}

				if (normalQueue.Count != 0)
				{
					queueType = ScriptEventQueue.Normal;
					next = normalQueue.Peek();
					return true;
				}

				queueType = ScriptEventQueue.Normal;
				next = null;
				return false;
			}
		}

		private void RemoveHead(ScriptEventQueue queueType, Func<ScriptEventExecutionResult> work)
		{
			lock (gate)
			{
				var queue = GetQueue(queueType);

				if (queue.Count != 0 && ReferenceEquals(queue.Peek(), work))
					_ = queue.Dequeue();
			}
		}

		private bool HandleLocalBlock(ScriptEventQueue queueType, Func<ScriptEventExecutionResult> work, ref int consecutiveInteractiveLocalBlocks, ref int consecutiveNormalLocalBlocks, ref bool preferNormalOnce)
		{
			int interactiveCount;
			int normalCount;

			lock (gate)
			{
				var queue = GetQueue(queueType);

				if (queue.Count != 0 && ReferenceEquals(queue.Peek(), work))
				{
					_ = queue.Dequeue();
					queue.Enqueue(work);
				}

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
		{
			try
			{
				return Volatile.Read(ref workerPumpSignal)?.WaitOne(Timeout.Infinite) == true;
			}
			catch (ObjectDisposedException)
			{
				return false;
			}
		}

		private bool HasQueuedEventsUnsafe() => interactiveQueue.Count != 0 || normalQueue.Count != 0;

		private bool TryMarkPumpScheduledUnsafe()
		{
			if (!HasQueuedEventsUnsafe() || pumpScheduled || pumping)
				return false;

			pumpScheduled = true;
			return true;
		}

		private ScriptEventExecutionResult TryExecuteTimerEvent(TimerWithTag timer, IFuncObj func, bool once)
		{
			if (script.hasExited)
			{
				timer.ClearSchedulerQueueState();
				return ScriptEventExecutionResult.Dropped;
			}

			var timerRegistration = script.FlowData.GetTimerRegistration(timer);

			if (timerRegistration == null || !ReferenceEquals(timerRegistration.Callback, func))
			{
				timer.ClearSchedulerQueueState();
				return ScriptEventExecutionResult.Dropped;
			}

			var threads = script.Threads;

			if ((!Keysharp.Core.Ks.A_AllowTimers.Ab() && script.totalExistingThreads > 0)
					|| !threads.AnyThreadsAvailable()
					|| !threads.IsInterruptible())
				return ScriptEventExecutionResult.GlobalBlocked;

			var startResult = InvokePseudoThread(timer.Tag.Ai(), true, false, btv =>
			{
				btv.currentTimer = timer;
				btv.eventInfo = func;
				_ = btv.RunAndEnd(() => _ = func.Call());
				return true;
			}, out _);

			if (startResult != ScriptEventExecutionResult.Executed)
				return startResult;

			if (once)
			{
				_ = script.FlowData.RemoveTimerRegistration(timer, out _);
				timer.Stop();
				timer.Dispose();
				script.ExitIfNotPersistent();
			}
			else
			{
				timer.ClearSchedulerQueueState();

				if (script.FlowData.GetTimerRegistration(timer) == null)
					script.ExitIfNotPersistent();
			}

			return ScriptEventExecutionResult.Executed;
		}

		internal ScriptEventExecutionResult TryExecuteThreadLaunch(long priority, bool skipUninterruptible, bool isCritical, Action action, bool useTryCatch)
		{
			return InvokePseudoThread(priority, skipUninterruptible, isCritical, btv =>
			{
				if (useTryCatch)
				{
					_ = btv.RunAndEnd(action);
				}
				else
				{
					try
					{
						action();
					}
					finally
					{
						script.Threads.EndThread(btv);
					}
				}

				return true;
			}, out _);
		}

		internal ScriptEventExecutionResult InvokePseudoThread<T>(long priority, bool skipUninterruptible, bool isCritical, Func<ThreadVariables, T> action, out T result, bool allowEmergencyOverflow = false)
		{
			result = default;

			if (action == null || IsDisposed)
				return ScriptEventExecutionResult.Dropped;

			(ScriptEventExecutionResult status, T result) Execute()
			{
				var status = StartPseudoThread(priority, skipUninterruptible, isCritical, allowEmergencyOverflow, out var btv);
				return status != ScriptEventExecutionResult.Executed
					? (status, default)
					: (ScriptEventExecutionResult.Executed, action(btv));
			}

			var execution = OwnsCurrentThread ? Execute() : InvokeSynchronous(Execute);
			result = execution.result;
			return execution.status;
		}

		private void WaitForSynchronousCompletion(ManualResetEventSlim completed)
		{
			var waitingScheduler = script.CurrentSchedulerIfCreated;

			while (!completed.Wait(20))
			{
				if (script.hasExited)
					throw new Flow.UserRequestedExitException();

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
				script.FlowData.QueueOverdueTimersIfNeeded(Environment.TickCount64);
			}

			waitingScheduler?.PumpPendingEvents();
		}

		private ScriptEventExecutionResult StartPseudoThread(long priority, bool skipUninterruptible, bool isCritical, bool allowEmergencyOverflow, out ThreadVariables threadVariables)
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
				pumpScheduled = false;
			}
		}

		private void DisposeOwnedTimers()
		{
			foreach (var registration in script.FlowData.timers.GetSnapshot())
			{
				var timer = registration.Timer;

				if (!ReferenceEquals(timer?.OwnerScheduler, this))
					continue;

				if (script.FlowData.RemoveTimerRegistration(timer, out _))
				{
					timer.Stop();
					timer.Dispose();
				}
			}
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
