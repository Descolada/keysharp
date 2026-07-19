namespace Keysharp.Internals
{
	/// <summary>Maintains a preferred subscription, an optional fallback, and a bounded reattach policy.</summary>
	internal sealed class RecoveringSubscription : IDisposable
	{
		private const int MaximumAttempts = 3;
		private readonly object sync = new();
		private readonly Func<Action<Exception>, IDisposable> subscribePreferred;
		private readonly Func<IDisposable> subscribeFallback;
		private readonly Func<bool> preferredAvailable;
		private readonly Action<bool> stateChanged;
		private readonly bool keepFallbackWarm;
		private readonly Timer timer;
		private IDisposable preferred, fallback, availability;
		private int generation, activeGeneration, failedGeneration, failures, attaching;
		private long epoch;
		private bool disposed;

		internal RecoveringSubscription(Func<Action<Exception>, IDisposable> subscribePreferred,
			Func<IDisposable> subscribeFallback, Func<bool> preferredAvailable,
			Func<Action, IDisposable> subscribeAvailability, Action<bool> stateChanged = null,
			bool keepFallbackWarm = false, int retryIntervalMs = 1000)
		{
			this.subscribePreferred = subscribePreferred;
			this.subscribeFallback = subscribeFallback;
			this.preferredAvailable = preferredAvailable;
			this.stateChanged = stateChanged;
			this.keepFallbackWarm = keepFallbackWarm;
			timer = new Timer(_ => TryAttachPreferred(), null, Timeout.Infinite, Timeout.Infinite);
			RetryIntervalMs = Math.Max(1, retryIntervalMs);
			availability = subscribeAvailability?.Invoke(AvailabilityChanged);
		}

		private int RetryIntervalMs { get; }
		internal bool IsPreferred { get { lock (sync) return preferred != null; } }

		internal static RecoveringSubscription Create(Func<Action<Exception>, IDisposable> preferred,
			Func<IDisposable> fallback, Func<bool> available, Func<Action, IDisposable> availability,
			Action<bool> changed = null, bool keepFallbackWarm = false, int retryIntervalMs = 1000)
		{
			var subscription = new RecoveringSubscription(preferred, fallback, available, availability,
				changed, keepFallbackWarm, retryIntervalMs);
			subscription.Start();
			return subscription;
		}

		internal void Start()
		{
			if (keepFallbackWarm)
				EnsureFallback();

			if (!TryAttachPreferred())
				EnsureFallback();
		}

		internal bool TryAttachPreferred()
		{
			if (Interlocked.Exchange(ref attaching, 1) != 0)
				return false;

			var retryStale = false;
			try
			{
				lock (sync)
					if (disposed || preferred != null)
						return preferred != null;

				bool available;
				try { available = preferredAvailable?.Invoke() != false; }
				catch { available = false; }

				if (!available)
				{
					if (availability == null)
						FailedAttempt();
					return false;
				}

				var candidate = Interlocked.Increment(ref generation);
				long candidateEpoch;
				lock (sync) candidateEpoch = epoch;
				IDisposable subscription;
				try { subscription = subscribePreferred?.Invoke(error => PreferredFailed(candidate)); }
				catch { subscription = null; }

				if (subscription == null || Volatile.Read(ref failedGeneration) == candidate)
				{
					subscription?.Dispose();
					FailedAttempt();
					return false;
				}

				IDisposable retireFallback = null;
				lock (sync)
				{
					if (disposed || preferred != null || epoch != candidateEpoch)
					{
						subscription.Dispose();
						retryStale = !disposed && preferred == null;
						return preferred != null;
					}

					preferred = subscription;
					activeGeneration = candidate;
					failures = 0;
					timer.Change(Timeout.Infinite, Timeout.Infinite);
					if (!keepFallbackWarm) { retireFallback = fallback; fallback = null; }
				}

				retireFallback?.Dispose();
				stateChanged?.Invoke(true);
				return true;
			}
			finally
			{
				Volatile.Write(ref attaching, 0);
				if (retryStale) ThreadPool.QueueUserWorkItem(_ => TryAttachPreferred());
			}
		}

		private void PreferredFailed(int failed)
		{
			Volatile.Write(ref failedGeneration, failed);
			IDisposable retire;
			lock (sync)
			{
				if (disposed || failed != activeGeneration)
					return;
				retire = preferred;
				preferred = null;
				activeGeneration = 0;
			}

			retire?.Dispose();
			EnsureFallback();
			stateChanged?.Invoke(false);
			FailedAttempt();
		}

		private void AvailabilityChanged()
		{
			IDisposable retire;
			lock (sync)
			{
				if (disposed) return;
				epoch++;
				failures = 0;
				retire = preferred;
				preferred = null;
				activeGeneration = 0;
			}
			retire?.Dispose();
			stateChanged?.Invoke(false);
			if (!TryAttachPreferred()) EnsureFallback();
		}

		private void FailedAttempt()
		{
			lock (sync)
			{
				failures = Math.Min(MaximumAttempts, failures + 1);
				if (!disposed && preferred == null && failures < MaximumAttempts)
					timer.Change(RetryIntervalMs * (1 << Math.Min(failures, 2)), Timeout.Infinite);
			}
		}

		private void EnsureFallback()
		{
			lock (sync)
				if (!disposed && preferred == null && fallback == null)
					try { fallback = subscribeFallback?.Invoke(); } catch { }
		}

		public void Dispose()
		{
			IDisposable p, f, a;
			lock (sync)
			{
				if (disposed) return;
				disposed = true; epoch++;
				p = preferred; f = fallback; a = availability;
				preferred = fallback = availability = null;
			}
			timer.Dispose();
			a?.Dispose(); p?.Dispose(); f?.Dispose();
		}
	}
}
