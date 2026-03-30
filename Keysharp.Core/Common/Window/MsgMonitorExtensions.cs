using static System.Collections.Generic.SystemCollectionsGenericExtensions;

namespace Keysharp.Core.Common.Window
{
	internal static class MsgMonitorExtensions
	{
		private static ScriptEventExecutionResult ExecuteRegistration(this MsgMonitorRegistration registration, Script script, object[] args, object eventInfo, long hwnd, bool allowEmergencyOverflow, out long result)
		{
			result = 0L;
			var targetScheduler = registration.OwnerScheduler ?? script.EventScheduler;
			registration.InstanceCount++;

			try
			{
				return targetScheduler.TryInvokePseudoThread(
					0,
					false,
					false,
					tv =>
					{
						long localResult = 0L;
						_ = Flow.TryCatch(() => localResult = Script.ForceLong(ExecuteHandler(script, registration.Callback, args, tv, eventInfo, hwnd)));
						return localResult;
					},
					out result,
					allowEmergencyOverflow);
			}
			finally
			{
				registration.InstanceCount--;
			}
		}

		internal static ScriptEventExecutionResult TryExecuteBuffered(this MsgMonitorRegistration registration, Script script, object[] args, object eventInfo, long hwnd, out long result)
		{
			result = 0L;

			if (!registration.IsActive)
				return ScriptEventExecutionResult.Dropped;

			if (registration.InstanceCount >= registration.MaxInstances)
				return ScriptEventExecutionResult.LocalBlocked;

			var executionResult = registration.ExecuteRegistration(script, args, eventInfo, hwnd, false, out result);

			if (executionResult == ScriptEventExecutionResult.Executed)
				script.ExitIfNotPersistent();

			return executionResult;
		}

		internal static bool TryExecuteEmergency(this MsgMonitor monitor, Script script, object[] args, object eventInfo, long hwnd, out long result)
		{
			result = 0L;

			if (monitor == null)
				return false;

			var executedAny = false;

			foreach (var registration in monitor.GetRegistrationsSnapshot())
			{
				if (!registration.IsActive || registration.InstanceCount >= registration.MaxInstances)
					continue;

				var executionResult = registration.ExecuteRegistration(script, args, eventInfo, hwnd, true, out result);

				if (executionResult != ScriptEventExecutionResult.Executed)
					continue;

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
