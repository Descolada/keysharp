namespace Keysharp.Internals.Input.Keyboard
{
	/// <summary>
	/// Runs criteria outside latency-sensitive native input callbacks while placing a hard upper bound
	/// on callbacks which can remain alive after their caller times out. User callbacks cannot be safely
	/// aborted, so admission control is the only reliable way to prevent unbounded worker accumulation.
	/// </summary>
	internal sealed class BoundedCriterionExecutor
	{
		private readonly SemaphoreSlim admission;

		internal BoundedCriterionExecutor(int capacity)
		{
			if (capacity <= 0)
				throw new ArgumentOutOfRangeException(nameof(capacity));

			admission = new SemaphoreSlim(capacity, capacity);
		}

		internal CriterionExecutionStatus Execute<T>(Func<T> callback, TimeSpan timeout, out T value, out Exception error)
		{
			ArgumentNullException.ThrowIfNull(callback);
			value = default;
			error = null;
			if (!admission.Wait(0))
				return CriterionExecutionStatus.Rejected;

			var completedValue = default(T);
			Exception completedError = null;
			Task task;
			try
			{
				task = Task.Run(() =>
				{
					try { completedValue = callback(); }
					catch (Exception ex) { completedError = ex; }
					finally { admission.Release(); }
				});
			}
			catch
			{
				admission.Release();
				throw;
			}

			if (!task.Wait(timeout))
				return CriterionExecutionStatus.TimedOut;
			value = completedValue;
			error = completedError;
			return error == null ? CriterionExecutionStatus.Completed : CriterionExecutionStatus.Failed;
		}
	}

	internal enum CriterionExecutionStatus : byte
	{
		Completed,
		Failed,
		TimedOut,
		Rejected
	}

	/// <summary>Separates watchdog-sensitive hook work from ordinary script/UI criterion calls.</summary>
	internal sealed class ScriptCriterionExecutors
	{
		private readonly BoundedCriterionExecutor hookLane = new(2);
		private readonly BoundedCriterionExecutor ordinaryLane = new(4);
		private int hookRejections;
		private int ordinaryRejections;

		internal BoundedCriterionExecutor Select(bool hookCallback) => hookCallback ? hookLane : ordinaryLane;

		internal int RecordRejection(bool hookCallback)
			=> hookCallback
				? Interlocked.Increment(ref hookRejections)
				: Interlocked.Increment(ref ordinaryRejections);
	}
}
