using Keysharp.Builtins;
namespace Keysharp.Internals.Threading
{
	internal class TimerWithTag : UITimer
	{
#if !WINDOWS
		public bool Enabled
		{
			get => !IsDisposed && Started;
			set {
				if (IsDisposed)
					return;

				if (value)
					Start();
				else
					Stop();
			}
		}
		public new int Interval
		{
			get => IsDisposed ? 0 : (int)(base.Interval * 1000);
			set
			{
				if (!IsDisposed)
					base.Interval = (float)value / 1000;
			}
		}
		public event EventHandler<EventArgs> Tick
		{
			add => Elapsed += value;
			remove => Elapsed -= value;
		}
#else
		[DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
		internal bool IsDisposed { get; private set; }
#endif
		/// <summary>
		/// Guard so we never queue more than one pending invoke.
		/// </summary>
		private bool schedulerQueued;
		private long lastSignalTick;
		private long fallbackDueTick = long.MaxValue;
		private readonly SchedulerRegistration ownerState = new();

		[DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
		public new object Tag { get; set; }

		[DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
		internal IFuncObj ScriptFunc { get; set; }

		[DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
		internal bool RunsOnce { get; set; }

		[DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
		internal ScriptEventScheduler OwnerScheduler
		{
			get => ownerState.OwnerScheduler;
			set => ownerState.Set(value, value != null);
		}

		[DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
		internal long FallbackDueTick => fallbackDueTick;

		public TimerWithTag() : base()
		{
		}

		public TimerWithTag(double interval) : this()
		{
			Interval = (int)interval;
		}

		/// <summary>
		/// Pushes a Tick to the main thread message queue immediately.
		/// </summary>
		public void PushToMessageQueue(long nowTick = -1L, bool allowEarly = false)
		{
			if (nowTick < 0L)
				nowTick = Environment.TickCount64;

			if (IsDisposed || schedulerQueued || !Enabled || ScriptFunc == null)
				return;

			// WinForms can deliver an immediate WM_TIMER after a long blocked period even though
			// we already queued the overdue firing. Ignore signals that arrive before the next
			// interval boundary so a blocked timer coalesces to one callback.
			if (!allowEarly && lastSignalTick != 0L && nowTick - lastSignalTick < Math.Max(1, Interval))
				return;

			schedulerQueued = true;
			lastSignalTick = nowTick;
			fallbackDueTick = long.MaxValue;
			Script.TheScript.FlowData.MarkTimerFallbackDueTickDirty();
			(OwnerScheduler ?? Script.TheScript.UIEventScheduler).EnqueueTimer(this, ScriptFunc, RunsOnce);
		}

		internal void QueueIfOverdue(long nowTick)
		{
			if (IsDisposed || schedulerQueued || !Enabled || ScriptFunc == null)
				return;

			if (nowTick < fallbackDueTick)
				return;

			PushToMessageQueue(nowTick);
		}

		internal void ClearSchedulerQueueState(long nowTick = -1L)
		{
			schedulerQueued = false;
			UpdateFallbackDueTick();
		}

		internal void ResetFallbackSchedule(long nowTick = -1L)
		{
			if (nowTick < 0L)
				nowTick = Environment.TickCount64;

			lastSignalTick = nowTick;
			UpdateFallbackDueTick();
		}

		/// <summary>
		/// Start (or restart) the timer at the full interval.
		/// </summary>
		public new void Start()
		{
			if (IsDisposed)
				return;

			lastSignalTick = Environment.TickCount64;
			try
			{
				base.Start();
			}
			catch (ObjectDisposedException)
			{
				return;
			}
			UpdateFallbackDueTick();
		}

		/// <summary>
		/// Stops entirely and clears any pause state.
		/// </summary>
		public new void Stop()
		{
			if (!IsDisposed)
			{
				try
				{
					base.Stop();
				}
				catch (ObjectDisposedException)
				{
				}
			}

			schedulerQueued = false;
			fallbackDueTick = long.MaxValue;
			Script.TheScript?.FlowData.MarkTimerFallbackDueTickDirty();
		}

		private void UpdateFallbackDueTick()
		{
			fallbackDueTick = !IsDisposed && Enabled && ScriptFunc != null && !schedulerQueued
				? lastSignalTick + Math.Max(1, Interval)
				: long.MaxValue;

			Script.TheScript?.FlowData.NoteTimerFallbackDueTick(fallbackDueTick);
		}

		protected override void Dispose(bool disposing)
		{
			if (IsDisposed)
				return;
#if WINDOWS
			IsDisposed = true;
#endif
			schedulerQueued = false;
			fallbackDueTick = long.MaxValue;
			ScriptFunc = null;
			OwnerScheduler = null;
			Script.TheScript?.FlowData.MarkTimerFallbackDueTickDirty();
			base.Dispose(disposing);
		}

#if WINDOWS
		protected override void OnTick(EventArgs e) => base.OnTick(e);
#else
		protected void OnTick(EventArgs e) => OnElapsed(e);
		protected override void OnElapsed(EventArgs e) => base.OnElapsed(e);
#endif
	}
}
