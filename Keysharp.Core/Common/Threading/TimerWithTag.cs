using Keysharp.Core.Common.Invoke;

namespace Keysharp.Core.Common.Threading
{
	internal class TimerWithTag : UITimer
	{
#if !WINDOWS
		public bool Enabled 
		{
			get => Started;
			set {
				if (value)
					Start();
				else
					Stop();
			}
		}
		public new int Interval
		{
			get => (int)(base.Interval * 1000);
			set => base.Interval = (float)value / 1000;
		}
		public event EventHandler<EventArgs> Tick
		{
			add => Elapsed += value;
			remove => Elapsed -= value;
		}
#endif
		/// <summary>
		/// Guard so we never queue more than one pending invoke.
		/// </summary>
		private bool schedulerQueued;
		private long lastSignalTick;
		private long fallbackDueTick = long.MaxValue;

		[DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
		public new object Tag { get; set; }

		[DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
		internal IFuncObj ScriptFunc { get; set; }

		[DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
		internal bool RunsOnce { get; set; }

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
		public void PushToMessageQueue(long nowTick = -1L)
		{
			if (schedulerQueued || !Enabled || ScriptFunc == null)
				return;

			if (nowTick < 0L)
				nowTick = Environment.TickCount64;

			schedulerQueued = true;
			lastSignalTick = nowTick;
			fallbackDueTick = long.MaxValue;
			Script.TheScript.FlowData.MarkTimerFallbackDueTickDirty();
			Script.TheScript.EventScheduler.EnqueueTimer(this, ScriptFunc, RunsOnce);
		}

		internal void QueueIfOverdue(long nowTick)
		{
			if (schedulerQueued || !Enabled || ScriptFunc == null)
				return;

			if (nowTick < fallbackDueTick)
				return;

			PushToMessageQueue(nowTick);
		}

		internal void ClearSchedulerQueueState(long nowTick = -1L)
		{
			schedulerQueued = false;

			if (nowTick < 0L)
				nowTick = Environment.TickCount64;

			lastSignalTick = nowTick;
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
			lastSignalTick = Environment.TickCount64;
			base.Start();
			UpdateFallbackDueTick();
		}

		/// <summary>
		/// Stops entirely and clears any pause state.
		/// </summary>
		public new void Stop()
		{
			base.Stop();
			schedulerQueued = false;
			fallbackDueTick = long.MaxValue;
			Script.TheScript.FlowData.MarkTimerFallbackDueTickDirty();
		}

		private void UpdateFallbackDueTick()
		{
			fallbackDueTick = Enabled && ScriptFunc != null && !schedulerQueued
				? lastSignalTick + Math.Max(1, Interval)
				: long.MaxValue;

			Script.TheScript.FlowData.NoteTimerFallbackDueTick(fallbackDueTick);
		}

#if WINDOWS
		protected override void OnTick(EventArgs e) => base.OnTick(e);
#else
		protected void OnTick(EventArgs e) => OnElapsed(e);
		protected override void OnElapsed(EventArgs e) => base.OnElapsed(e);
#endif
	}
}
