using Keysharp.Builtins;
using Timer1 = System.Timers.Timer;

namespace Keysharp.Internals
{
	internal static class Flow
	{
		internal const int IntervalUnspecified = int.MinValue + 303;

		private sealed class DialogInterruptibilityScope : IDisposable
		{
			private readonly ThreadVariables threadVariables;
			private readonly bool previousAllowThreadToBeInterrupted;
			private bool disposed;

			internal DialogInterruptibilityScope(ThreadVariables threadVariables)
			{
				this.threadVariables = threadVariables;
				previousAllowThreadToBeInterrupted = threadVariables.allowThreadToBeInterrupted;
			}

			public void Dispose()
			{
				if (disposed)
					return;

				threadVariables.allowThreadToBeInterrupted = previousAllowThreadToBeInterrupted;
				disposed = true;
			}
		}

		private sealed class NoOpScope : IDisposable
		{
			internal static readonly NoOpScope Instance = new();

			public void Dispose()
			{
			}
		}

		internal static bool TryGetException<TException>(Exception ex, out TException found)
			where TException : Exception
		{
			found = null;

			if (ex == null)
				return false;

			if (ex is TException matched)
			{
				found = matched;
				return true;
			}

			if (ex is AggregateException agg)
			{
				foreach (var inner in agg.InnerExceptions)
				{
					if (TryGetException(inner, out found))
						return true;
				}
			}

			return ex.InnerException != null && TryGetException(ex.InnerException, out found);
		}

		internal static IDisposable BeginDialogInterruptibilityScope()
		{
			var script = Script.TheScript;

			if (script == null || Volatile.Read(ref script.totalExistingThreads) == 0)
				return NoOpScope.Instance;

			var tv = script.Threads.CurrentThread;

			if (tv == null || tv.allowThreadToBeInterrupted)
				return NoOpScope.Instance;

			var scope = new DialogInterruptibilityScope(tv);
			tv.allowThreadToBeInterrupted = true;
			return scope;
		}

		internal static bool ExitAppInternal(Keysharp.Builtins.Flow.ExitReasons exitReason, object exitCode = null, bool useThrow = true)
		{
			var script = Script.TheScript;
			var fd = script.FlowData;

			if (script.hasExited)
				return false;

			Dialogs.CloseMessageBoxes();
			Dialogs.CloseToolTips();
			var ec = exitCode.Ai();
			Accessors.A_ExitReason = exitReason.ToString();
			var allowInterruptionPrev = fd.allowInterruption;
			fd.allowInterruption = false;
			var result = script.onExitHandlers.InvokeEventHandlers(Accessors.A_ExitReason, exitCode);

			if (exitReason >= Keysharp.Builtins.Flow.ExitReasons.None && Script.ForceLong(result) != 0L)
			{
				Accessors.A_ExitReason = "";
				fd.allowInterruption = allowInterruptionPrev;
				return true;
			}

			script.onExitHandlers.Clear();
			script.SuppressErrorOccurredDialog = true;

			GC.Collect();
			GC.WaitForPendingFinalizers();
			DestructorPump.RunPendingDestructors();

			foreach (var t in Reflections.GetNestedTypes([script.ProgramType]).OrderBy(Reflections.GetInheritanceDepth))
			{
				var fields = t.GetFields(BindingFlags.Static | BindingFlags.Public);

				foreach (var val in fields.Select(f => f.GetValue(null)))
				{
					if (val is Any kso)
						CallDeleteSilent(kso);
				}

				if (script.Vars.Statics.IsInitialized(t)
					&& script.Vars.Statics.TryGetValue(t, out Class kso2)
					&& kso2.HasOwnPropInternal("__Delete"))
					CallDeleteSilent(kso2);
			}

			script.hasExited = true;
			script.ScheduleAllEventSchedulers();
			fd.allowInterruption = allowInterruptionPrev;
			HotkeyDefinition.AllDestruct();
			StopMainTimer();

			if (script.KeyboardData.blockInput)
				_ = Keysharp.Builtins.Keyboard.ScriptBlockInput(ToggleValueType.Off);
			else if (script.KeyboardData.blockMouseMove)
				_ = Keysharp.Builtins.Keyboard.ScriptBlockInput(ToggleValueType.MouseMoveOff);

			script.FlowData.timers.Clear();

			Gui.DestroyAll();
			Environment.ExitCode = ec;
			script.Dispose();

#if !WINDOWS
			Script.InvokeOnUIThread(() => Eto.Forms.Application.Instance?.Quit());
#endif

			if (useThrow)
				throw new Keysharp.Builtins.Flow.UserRequestedExitException();

			return false;

			void CallDeleteSilent(Any kso)
			{
				try
				{
					kso.HasFinalizer = false;
					Script.InvokeMeta(kso, "__Delete");
					if (kso is IDisposable dis)
						dis.Dispose();
				}
				catch
				{
				}
			}
		}

		internal static void SetMainTimer()
		{
			var script = Script.TheScript;
			var mainTimer = script.FlowData.mainTimer;

			if (mainTimer == null)
			{
				mainTimer = new Timer1(10);
				mainTimer.Elapsed += (o, e) => { };
				script.FlowData.mainTimer = mainTimer;
				mainTimer.Start();
			}
		}

		internal static void Sleep(int delay = -1)
		{
			var script = Script.TheScript;

			if (delay == 0)
			{
				var tc = Environment.TickCount;
				TryDoEvents(true, false);
				if (Environment.TickCount - tc == 0)
					System.Threading.Thread.Sleep(0);
			}
			else if (delay == -1)
			{
				TryDoEvents(true, false);
			}
			else if (delay == -2)
			{
				WaitWithMessagePump(() => !script.hasExited && script.input != null && script.input.InProgress());
			}
			else
			{
				var stopTick = Environment.TickCount64 + delay;
				WaitWithMessagePump(() => !script.hasExited && Environment.TickCount64 < stopTick);
			}
		}

		internal static void SleepWithoutInterruption(int duration = IntervalUnspecified)
		{
			var fd = Script.TheScript.FlowData;
			fd.allowInterruption = false;
			Sleep(duration);
			fd.allowInterruption = true;
		}

		internal static void StopMainTimer()
		{
			var script = Script.TheScript;
			var mainTimer = script.FlowData.mainTimer;

			if (mainTimer != null)
			{
				mainTimer.Stop();
				script.FlowData.mainTimer = null;
			}
		}

		internal static bool TryCatch(Action action)
		{
			static void ShowHandledErrorDialog(Exception ex, bool keysharpDialog)
			{
				static void ShowDialog(Exception innerEx, bool useKeysharpDialog)
				{
					if (useKeysharpDialog)
						_ = ErrorDialog.Show((KeysharpException)innerEx, false);
					else
						_ = ErrorDialog.Show(innerEx);
				}

				var script = Script.TheScript;
				var scheduler = script.CurrentSchedulerIfCreated ?? script.EventScheduler;
				var executionResult = scheduler.TryExecuteThreadLaunch(0, false, false, _ =>
				{
					ShowDialog(ex, keysharpDialog);
				});

				if (executionResult != ScriptEventExecutionResult.Executed)
					ShowDialog(ex, keysharpDialog);
			}

			try
			{
				action();
				return true;
			}
			catch (KeysharpException kserr)
			{
				var userErr = kserr.UserError;
				if (userErr != null && !userErr.Processed)
					_ = Errors.ErrorOccurred(userErr, Keywords.Keyword_Exit);

				if (userErr != null && !userErr.Handled && !Script.TheScript.SuppressErrorOccurredDialog)
					ShowHandledErrorDialog(kserr, true);
				else if (userErr == null && !Script.TheScript.SuppressErrorOccurredDialog)
					ShowHandledErrorDialog(kserr, true);

				return false;
			}
			catch (Exception mainex)
			{
				var ex = mainex.InnerException ?? mainex;

				if (TryGetException<Keysharp.Builtins.Flow.UserRequestedExitException>(mainex, out _))
					return true;

				if (ex is KeysharpException kserr)
				{
					var userErr = kserr.UserError;
					if (userErr != null && !userErr.Processed)
						_ = Errors.ErrorOccurred(userErr, Keywords.Keyword_Exit);

					if (userErr != null && !userErr.Handled && !Script.TheScript.SuppressErrorOccurredDialog)
						ShowHandledErrorDialog(kserr, true);
					else if (userErr == null && !Script.TheScript.SuppressErrorOccurredDialog)
						ShowHandledErrorDialog(kserr, true);
				}
				else if (!Script.TheScript.SuppressErrorOccurredDialog)
				{
					var dummy = new Error(mainex);
					_ = Errors.ErrorOccurred(dummy, Keywords.Keyword_Exit);
					if (!dummy.Handled)
						ShowHandledErrorDialog(ex, false);
				}

				return false;
			}
		}

		internal static void TryDoEvents(bool allowExit = true, bool yieldTick = true)
		{
			var start = yieldTick ? Environment.TickCount : default;
			var script = Script.TheScript;

			try
			{
				PumpUiAndScheduler();
			}
			catch (Exception ex) when (!allowExit || !TryGetException<Keysharp.Builtins.Flow.UserRequestedExitException>(ex, out _))
			{
			}
			finally
			{
				script.RecordMessageCheck();
			}

			if (yieldTick && start.Equals(Environment.TickCount))
				System.Threading.Thread.Sleep(1);

			if (script.hasExited)
				throw new Keysharp.Builtins.Flow.UserRequestedExitException();
		}

		internal static void WaitWithMessagePump(Func<bool> keepWaiting, bool allowExit = true)
		{
			while (keepWaiting())
				TryDoEvents(allowExit);
		}

		private static void PumpUiAndScheduler()
		{
			var script = Script.TheScript;
			var scheduler = script.EventScheduler;

			if (script.IsOnMainThread)
			{
#if WINDOWS
				Application.DoEvents();
#else
				Application.Instance?.RunIteration();
#endif
			}

			scheduler.PumpPendingEvents();
		}
	}
}
