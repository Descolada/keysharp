namespace Keysharp.Builtins
{
	/// <summary>
	/// Public interface for real thread-related functions.
	/// These differ than the pseudo-threads used throughout the rest of the library.
	/// </summary>
	public partial class Ks
	{
		/// <summary>
		/// Runs a function object inside of a lock statement.
		/// This is useful for calling a function inside of a real thread.
		/// </summary>
		/// <param name="lockObj">The object to lock on when calling the function.</param>
		/// <param name="obj1">The name of the function or a function object.</param>
		/// <param name="args">The arguments to pass to the function.</param>
		/// <returns>The object the function object returned.</returns>
		public static object LockRun(object lockObj, object obj1, params object[] args)
		{
			lock (lockObj)
			{
				var funcObj = Functions.GetFuncObj(obj1, null, true);
				return funcObj.Call(funcObj.Inst == null ? args : new[] { funcObj.Inst }.Concat(args));
			}
		}

		/// <summary>
		/// An object that encapsulates a C# Task.
		/// </summary>
		public sealed class RealThread : KeysharpObject
		{
			internal Task<object> task;
			private readonly Task<ScriptEventScheduler> schedulerTask;

			private static Task<object> StartWorkerTask(Func<object> body)
				=> Task.Factory.StartNew(body, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

			private static object RunWorkerLoop(Func<object> body, TaskCompletionSource<ScriptEventScheduler> schedulerSource)
			{
				var script = Script.TheScript;
				object result = DefaultObject;
				ScriptEventScheduler scheduler = null;
				var previousContext = SynchronizationContext.Current;

				try
				{
					scheduler = script.EventScheduler;
					_ = schedulerSource.TrySetResult(scheduler);
					SynchronizationContext.SetSynchronizationContext(new ScriptEventSynchronizationContext(scheduler));
					var launchResult = scheduler.TryExecuteThreadLaunch(0, false, false, _ => result = body());

					if (launchResult != ScriptEventExecutionResult.Executed)
						_ = Errors.ErrorOccurred($"Unable to start RealThread worker body ({launchResult}).");

					scheduler.RunWorkerEventLoop();
					return result;
				}
				catch (Exception ex)
				{
					_ = schedulerSource.TrySetException(ex);
					throw;
				}
				finally
				{
					scheduler?.DisposeWorker();
					script.ExitIfNotPersistent();
					SynchronizationContext.SetSynchronizationContext(previousContext);
				}
			}

			private static RealThread StartRealThread(Func<object> body)
			{
				var schedulerSource = new TaskCompletionSource<ScriptEventScheduler>(TaskCreationOptions.RunContinuationsAsynchronously);
				var tsk = StartWorkerTask(() => RunWorkerLoop(body, schedulerSource));
				return new RealThread(tsk, schedulerSource.Task);
			}

			private ScriptEventScheduler GetScheduler() => schedulerTask.GetAwaiter().GetResult();

			private ScriptEventScheduler GetAliveScheduler()
			{
				var scheduler = GetScheduler();

				return scheduler == null || scheduler.IsDisposed ? null : scheduler;
			}

			private static object ReportThreadNotAlive(object ret = null) => Errors.ErrorOccurred("Real thread is no longer alive.", ret);

			/// <summary>
			/// Constructor that takes a task to keep a reference to.
			/// </summary>
			/// <param name="t">The task to hold.</param>
			internal RealThread(Task<object> t, Task<ScriptEventScheduler> scheduler)
			{
				task = t;
				schedulerTask = scheduler;
			}

			/// <summary>
			/// Gets the managed thread id of the worker backing this real thread.
			/// </summary>
			public long Id => GetAliveScheduler()?.OwnerManagedThreadId ?? (long)ReportThreadNotAlive(0L);

			/// <summary>
			/// True while the worker scheduler is still available to accept work.
			/// </summary>
			public bool IsAlive
			{
				get
				{
					if (task.IsCompleted)
						return false;

					if (!schedulerTask.IsCompleted)
						return true;

					return schedulerTask.Status == TaskStatus.RanToCompletion && !schedulerTask.Result.IsDisposed;
				}
			}

			/// <summary>
			/// Runs a function object inside of a C# task.
			/// </summary>
			/// <param name="obj">The name of the function or a function object.</param>
			/// <param name="args">The arguments to pass to the function.</param>
			/// <returns>The <see cref="RealThread"/> object.</returns>
			public static object staticCall(object @this, object obj, params object[] args)
			{
				var funcObj = Functions.GetFuncObj(obj, null, true);
				return StartRealThread(() => funcObj.Call(args));
			}

			/// <summary>
			/// Encapsulates a call to <see cref="Task.ContinueWith()"/>.
			/// </summary>
			/// <param name="obj">The name of the function or a function object.</param>
			/// <param name="args">The arguments to pass to the function.</param>
			/// <returns>The new <see cref="RealThread"/> object</returns>
			public object ContinueWith(object obj, params object[] args)
			{
				var fo = Functions.GetFuncObj(obj, null, true);
				var schedulerSource = new TaskCompletionSource<ScriptEventScheduler>(TaskCreationOptions.RunContinuationsAsynchronously);
				var rt = task.ContinueWith((to) => StartWorkerTask(() => RunWorkerLoop(() => fo.Call(args), schedulerSource)), TaskScheduler.Default).Unwrap();
				return new RealThread(rt, schedulerSource.Task);
			}

			/// <summary>
			/// Queues work onto this worker thread and returns immediately.
			/// </summary>
			public object Post(object obj, params object[] args)
			{
				var fo = Functions.GetFuncObj(obj, null, true);
				var scheduler = GetAliveScheduler();

				if (scheduler == null)
					return ReportThreadNotAlive();

				if (!scheduler.EnqueueThreadLaunch(0, false, false, () => _ = Keysharp.Internals.Flow.TryCatch(() => _ = fo.Call(args))))
					return ReportThreadNotAlive();

				return DefaultObject;
			}

			/// <summary>
			/// Executes work on this worker thread synchronously and returns the callback result.
			/// </summary>
			public object Send(object obj, params object[] args)
			{
				var fo = Functions.GetFuncObj(obj, null, true);
				var scheduler = GetAliveScheduler();

				if (scheduler == null)
					return ReportThreadNotAlive();

				try
				{
					return scheduler.InvokeSynchronous(() =>
					{
						object result = DefaultObject;
						var executionResult = scheduler.TryExecuteThreadLaunch(0, false, false, _ => result = fo.Call(args));
						return executionResult == ScriptEventExecutionResult.Executed
							? result
							: Errors.ErrorOccurred("Unable to execute callback on RealThread.");
					});
				}
				catch (ObjectDisposedException)
				{
					return ReportThreadNotAlive();
				}
			}

			/// <summary>
			/// Wait for the existing task either indefinitely or for a timeout period.
			/// </summary>
			/// <param name="obj">The timeout duration to wait. Default: wait indefinitely.</param>
			/// <returns>The result of the task.</returns>
			public object Wait(object obj = null)
			{
				var timeout = obj.Ai(-1);
				var start = DateTime.UtcNow;

				while (!task.IsCompleted && (timeout < 0 || (DateTime.UtcNow - start).TotalMilliseconds < timeout))
					Keysharp.Internals.Flow.TryDoEvents();

				try
				{
					return task.Result;
				}
				catch (AggregateException ae)
				{
					// Mostly looking for UserRequestedExitException
					throw ae.InnerException?.InnerException ?? ae.InnerException ?? ae;
				}
			}
		}
	}
}
