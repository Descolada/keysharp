using Keysharp.Builtins;
namespace Keysharp.Internals.Threading
{
	internal class ThreadVariableManager
	{
		/// <summary>
		/// Since AHK doesn't actually use threads, and instead behaves more as a stack-based system, using the built-in [ThreadStatic] attribute for members
		/// will not work.
		/// So we implement our own, but take a few steps for efficiency. We don't want to have to allocate an object every time a pseudo-thread is launched
		/// in response to a button click, hotkey, timer event, etc...
		/// So we create a SlimStack to be used as an object pool which we push and pop each time a thread starts and finishes.
		/// </summary>
		internal SlimStack<ThreadVariables> threadVars;

		internal ThreadVariableManager(int size)
		{
			threadVars = new (size, () => new ThreadVariables());
		}

		internal void PopThreadVariables(ThreadVariables threadVarsToPop, bool checkThread = true)
		{
			if (threadVarsToPop == null)
				return;

			var ctid = Thread.CurrentThread.ManagedThreadId;

			//Do not check threadVars for null, because it should have always been created with a call to PushThreadVariables() before this.
			if (ctid != Script.TheScript.ManagedMainThreadID
					|| threadVars.Index > 0)//Never pop the last object on the main thread.
			{
				//Ks.OutputDebugLine($"About to pop with {threadVars.Index} existing threads");
				if (threadVars.TryPop(out var tv))
				{
					if (!ReferenceEquals(tv, threadVarsToPop))
					{
						_ = Errors.ErrorOccurred("Severe threading error: Tried to pop a ThreadVariables object that was not at the top of the stack. This should never happen.", null, Keyword_ExitApp);
						return;
					}

					if (checkThread && ctid != tv.threadId)
					{
						_ = Errors.ErrorOccurred($"Severe threading error: ThreadVariables.threadId {tv.threadId} did not match the current thread id {ctid}. This should never happen.", null, Keyword_ExitApp);
						return;
					}
				}
			}
		}

		internal ThreadVariables PushThreadVariables(long priority, bool skipUninterruptible,
				bool isCritical = false)
		{
			var script = Script.TheScript;
			var pushed = threadVars.TryPush(out var tv);

			if (pushed)
			{
				tv.Init();
				tv.threadId = Thread.CurrentThread.ManagedThreadId;
				tv.priority = priority;

				if (!skipUninterruptible)
				{
					if (!tv.isCritical)
						tv.isCritical = isCritical;

					tv.ApplyUninterruptibleStartupWindow();
				}
			}

#if DEBUG
			else
				_ = Ks.OutputDebugLine($"Thread stack limit exceeded");

#endif
			return tv;
		}
	}
}
