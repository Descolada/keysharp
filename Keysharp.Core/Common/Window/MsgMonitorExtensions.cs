namespace Keysharp.Core.Common.Window
{
	internal static class MsgMonitorExtensions
	{
		private static long ExecuteRegistration(this MsgMonitorRegistration registration, Script script, object[] args, object eventInfo, long hwnd, bool allowEmergencyOverflow)
		{
			object res = null;
			registration.InstanceCount++;

			try
			{
				var (pushed, tv) = allowEmergencyOverflow
					? script.Threads.BeginSynchronousEmergencyThread()
					: script.Threads.BeginThread();

				if (!pushed)
					return 0L;

				tv.eventInfo = eventInfo;
				tv.hwndLastUsed = hwnd;
				_ = Flow.TryCatch(() =>
				{
					res = registration.Callback.Call(args);
					_ = script.Threads.EndThread((pushed, tv));
				}, true, (pushed, tv));
			}
			finally
			{
				registration.InstanceCount--;
			}

			return Script.ForceLong(res);
		}

		internal static ScriptEventExecutionResult TryExecuteBuffered(this MsgMonitorRegistration registration, Script script, object[] args, object eventInfo, long hwnd, out long result)
		{
			result = 0L;

			if (registration == null || !registration.IsActive)
				return ScriptEventExecutionResult.Dropped;

			var ptv = script.Threads.CurrentThread;

			if (ptv.priority > 0)
				return ScriptEventExecutionResult.Dropped;

			if (!script.Threads.AnyThreadsAvailable() || !script.Threads.IsInterruptible())
				return ScriptEventExecutionResult.GlobalBlocked;

			if (registration.InstanceCount >= registration.MaxInstances)
				return ScriptEventExecutionResult.LocalBlocked;

			result = registration.ExecuteRegistration(script, args, eventInfo, hwnd, false);
			script.ExitIfNotPersistent();
			return ScriptEventExecutionResult.Executed;
		}

		internal static bool TryExecuteEmergency(this MsgMonitor monitor, Script script, object[] args, object eventInfo, long hwnd, out long result)
		{
			result = 0L;

			if (monitor == null)
				return false;

			var executedAny = false;

			foreach (var registration in monitor.GetRegistrationsSnapshot())
			{
				if (registration == null || !registration.IsActive || registration.InstanceCount >= registration.MaxInstances)
					continue;

				result = registration.ExecuteRegistration(script, args, eventInfo, hwnd, true);
				executedAny = true;

				if (result != 0L)
					break;
			}

			if (executedAny)
				script.ExitIfNotPersistent();

			return executedAny;
		}
	}
}
