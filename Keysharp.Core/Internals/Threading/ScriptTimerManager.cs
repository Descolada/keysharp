using Keysharp.Builtins;
using Keysharp.Internals.Scripting;
using Keysharp.Runtime;

namespace Keysharp.Internals.Threading
{
	internal sealed class ScriptTimerState : CallbackRegistration
	{
		internal ScriptTimerState(IFuncObj callback, ScriptEventScheduler ownerScheduler)
			: base(callback, ownerScheduler, true)
		{
		}

		internal long PeriodMs { get; set; }
		internal long NextDueTick { get; set; }
		internal long Priority { get; set; }
		internal bool RunOnce { get; set; }
		internal bool Enabled { get; set; }
		internal bool Queued { get; set; }
		internal int RunningCount { get; set; }
		internal bool DeletePending { get; set; }
	}

	internal sealed class ScriptTimerKeyComparer : IEqualityComparer<(IFuncObj Callback, ScriptEventScheduler OwnerScheduler)>
	{
		internal static readonly ScriptTimerKeyComparer Instance = new();

		private ScriptTimerKeyComparer()
		{
		}

		public bool Equals((IFuncObj Callback, ScriptEventScheduler OwnerScheduler) x, (IFuncObj Callback, ScriptEventScheduler OwnerScheduler) y)
			=> Equals(x.Callback, y.Callback) && ReferenceEquals(x.OwnerScheduler, y.OwnerScheduler);

		public int GetHashCode((IFuncObj Callback, ScriptEventScheduler OwnerScheduler) obj)
		{
			unchecked
			{
				return ((obj.Callback?.GetHashCode() ?? 0) * 397)
					^ (obj.OwnerScheduler != null ? RuntimeHelpers.GetHashCode(obj.OwnerScheduler) : 0);
			}
		}
	}

	internal sealed class ScriptTimerManager : IDisposable
	{
		internal delegate bool TimerDispatchCallback(ScriptTimerState timer);

		private readonly object gate = new();
		private readonly Dictionary<(IFuncObj Callback, ScriptEventScheduler OwnerScheduler), ScriptTimerState> timers = new(ScriptTimerKeyComparer.Instance);
		private readonly AutoResetEvent wakeEvent = new(false);
		private readonly TimerDispatchCallback dispatchCallback;
		private readonly Thread timerThread;
		private readonly int scanIntervalMs;
		private bool disposed;

		internal int Count
		{
			get
			{
				lock (gate)
					return timers.Count;
			}
		}

		internal bool IsEmpty
		{
			get
			{
				lock (gate)
					return timers.Count == 0;
			}
		}

		internal ScriptTimerManager(TimerDispatchCallback dispatchCallback, int scanIntervalMs = 10)
		{
			this.dispatchCallback = dispatchCallback ?? throw new ArgumentNullException(nameof(dispatchCallback));
			this.scanIntervalMs = Math.Max(1, scanIntervalMs);
			timerThread = new Thread(Run)
			{
				IsBackground = true,
				Name = "Keysharp Script Timer Manager"
			};
			timerThread.Start();
		}

		internal ScriptTimerState[] GetSnapshot()
		{
			lock (gate)
			{
				if (timers.Count == 0)
					return [];

				return [.. timers.Values];
			}
		}

		internal ScriptTimerState Find(IFuncObj callback, ScriptEventScheduler ownerScheduler)
		{
			if (callback == null)
				return null;

			lock (gate)
			{
				return timers.TryGetValue((callback, ownerScheduler), out var timer) ? timer : null;
			}
		}

		internal ScriptTimerState Upsert(IFuncObj callback, ScriptEventScheduler ownerScheduler, long periodMs, bool runOnce, long priority)
		{
			ArgumentNullException.ThrowIfNull(callback);

			ScriptTimerState timer;

			lock (gate)
			{
				ThrowIfDisposed();

				var key = (callback, ownerScheduler);

				if (!timers.TryGetValue(key, out timer))
				{
					timer = new ScriptTimerState(callback, ownerScheduler);
					timers.Add(key, timer);
				}

				timer.PeriodMs = Math.Max(1, periodMs);
				timer.NextDueTick = Environment.TickCount64 + timer.PeriodMs;
				timer.Priority = priority;
				timer.RunOnce = runOnce;
				timer.Enabled = true;
				timer.DeletePending = false;
				timer.SetActive(true);
			}

			Wake();
			return timer;
		}

		internal void ResetTimer(ScriptTimerState timer)
		{
			if (timer == null)
				return;

			lock (gate)
			{
				if (disposed)
					return;

				timer.NextDueTick = Environment.TickCount64 + Math.Max(1, timer.PeriodMs);
				timer.Enabled = true;
				timer.DeletePending = false;
				timer.SetActive(true);
			}

			Wake();
		}

		internal void UpdatePriority(ScriptTimerState timer, long priority)
		{
			if (timer == null)
				return;

			lock (gate)
			{
				if (disposed)
					return;

				timer.Priority = priority;
			}
		}

		internal void DisableOrDelete(ScriptTimerState timer)
		{
			if (timer == null)
				return;

			lock (gate)
			{
				if (disposed)
					return;

				timer.Enabled = false;
				timer.SetActive(false);

				if (timer.Queued || timer.RunningCount > 0)
				{
					timer.DeletePending = true;
					return;
				}

				_ = timers.Remove((timer.Callback, timer.OwnerScheduler));
				ClearTimerState(timer);
			}

			Wake();
		}

		internal void MarkCallbackStarted(ScriptTimerState timer)
		{
			if (timer == null)
				return;

			lock (gate)
			{
				if (disposed)
					return;

				timer.Queued = false;

				if (timer.RunOnce)
				{
					timer.Enabled = false;
					timer.DeletePending = true;
					timer.SetActive(false);
				}
				else
				{
					timer.NextDueTick = Environment.TickCount64 + Math.Max(1, timer.PeriodMs);
				}

				timer.RunningCount++;
			}
		}

		internal void MarkCallbackFinished(ScriptTimerState timer)
		{
			if (timer == null)
				return;

			lock (gate)
			{
				if (disposed)
					return;

				if (timer.RunningCount > 0)
					timer.RunningCount--;

				if (!timer.Enabled && timer.DeletePending && timer.RunningCount == 0 && !timer.Queued)
				{
					_ = timers.Remove((timer.Callback, timer.OwnerScheduler));
					ClearTimerState(timer);
				}
			}

			Wake();
		}

		internal void ReleaseQueuedTimer(ScriptTimerState timer)
		{
			if (timer == null)
				return;

			lock (gate)
			{
				if (disposed)
					return;

				timer.Queued = false;

				if (!timer.Enabled && timer.DeletePending && timer.RunningCount == 0)
				{
					_ = timers.Remove((timer.Callback, timer.OwnerScheduler));
					ClearTimerState(timer);
				}
			}

			Wake();
		}

		internal bool RemoveOwned(ScriptEventScheduler ownerScheduler)
		{
			if (ownerScheduler == null)
				return false;

			var removed = false;

			lock (gate)
			{
				if (disposed)
					return false;

				foreach (var timer in timers.Values.ToArray())
				{
					if (!ReferenceEquals(timer.OwnerScheduler, ownerScheduler))
						continue;

					timer.Enabled = false;
					timer.SetActive(false);

					if (timer.RunningCount > 0)
					{
						timer.DeletePending = true;
						removed = true;
						continue;
					}

					_ = timers.Remove((timer.Callback, timer.OwnerScheduler));
					ClearTimerState(timer);
					removed = true;
				}
			}

			if (removed)
				Wake();

			return removed;
		}

		internal void Clear()
		{
			lock (gate)
			{
				if (disposed)
					return;

				foreach (var timer in timers.Values)
					ClearTimerState(timer);

				timers.Clear();
			}

			Wake();
		}

		public void Dispose()
		{
			lock (gate)
			{
				if (disposed)
					return;

				disposed = true;
				foreach (var timer in timers.Values)
					ClearTimerState(timer);
				timers.Clear();
			}

			Wake();

			if (!ReferenceEquals(Thread.CurrentThread, timerThread))
				timerThread.Join();

			wakeEvent.Dispose();
		}

		private void Run()
		{
			while (true)
			{
				var dueTimers = new List<ScriptTimerState>();

				lock (gate)
				{
					if (disposed)
						return;

					var now = Environment.TickCount64;

					foreach (var timer in timers.Values)
					{
						if (!timer.Enabled || timer.Queued || timer.RunningCount > 0)
							continue;

						if (now < timer.NextDueTick)
							continue;

						timer.Queued = true;
						dueTimers.Add(timer);
					}
				}

				foreach (var timer in dueTimers)
				{
					if (!dispatchCallback(timer))
						ClearQueueStateAfterFailedDispatch(timer);
				}

				try
				{
					_ = wakeEvent.WaitOne(scanIntervalMs);
				}
				catch (ObjectDisposedException)
				{
					return;
				}
			}
		}

		private void ClearQueueStateAfterFailedDispatch(ScriptTimerState timer)
		{
			lock (gate)
			{
				if (disposed)
					return;

				timer.Queued = false;

				if (!timer.Enabled && timer.DeletePending && timer.RunningCount == 0)
				{
					_ = timers.Remove((timer.Callback, timer.OwnerScheduler));
					ClearTimerState(timer);
			}
		}
		}

		private void Wake()
		{
			try
			{
				_ = wakeEvent.Set();
			}
			catch (ObjectDisposedException)
			{
			}
		}

		private void ThrowIfDisposed()
		{
			if (disposed)
				throw new ObjectDisposedException(nameof(ScriptTimerManager));
		}

		private static void ClearTimerState(ScriptTimerState timer)
		{
			if (timer == null)
				return;

			timer.Enabled = false;
			timer.Queued = false;
			timer.RunningCount = 0;
			timer.DeletePending = false;
			timer.Set(null, null, false);
		}
	}
}
