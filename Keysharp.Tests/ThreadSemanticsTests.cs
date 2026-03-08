using Assert = NUnit.Framework.Legacy.ClassicAssert;
using Keysharp.Core.Common.Threading;

namespace Keysharp.Tests
{
	[TestFixture, NonParallelizable]
	public class ThreadSemanticsTests : TestRunner
	{
		[Test, Category("Threading")]
		public void ThreadNoTimersIsThreadLocal()
		{
			Assert.AreEqual(true, Ks.A_AllowTimers);
			Assert.IsTrue(s.AccessorData.threadConfigDataPrototype.allowTimers);

			_ = Flow.Thread("NoTimers", true);

			Assert.AreEqual(false, Ks.A_AllowTimers);
			Assert.IsTrue(s.AccessorData.threadConfigDataPrototype.allowTimers);
		}

		[Test, Category("Threading")]
		public void ThreadNoTimersIsInheritedFromPrototype()
		{
			s.AccessorData.threadConfigDataPrototype.allowTimers = false;
			var btv = s.Threads.BeginThread();

			try
			{
				Assert.IsFalse(btv.Item2.configData.allowTimers);
			}
			finally
			{
				_ = s.Threads.EndThread(btv);
			}
		}

		[Test, Category("Threading")]
		public void ThreadInterruptUsesDurationAndIgnoresLineCount()
		{
			_ = Flow.Thread("Interrupt", 42, 1);
			Assert.AreEqual(42, s.uninterruptibleTime);

			var btv = s.Threads.BeginThread();

			try
			{
				Assert.AreEqual(42, btv.Item2.UninterruptibleDuration);
			}
			finally
			{
				_ = s.Threads.EndThread(btv);
			}
		}

		[Test, Category("Threading")]
		public void CriticalDefaultIsInheritedFromPrototype()
		{
			s.AccessorData.threadConfigDataPrototype.defaultIsCritical = true;
			s.AccessorData.threadConfigDataPrototype.peekFrequency = ThreadVariables.DefaultUninterruptiblePeekFrequency;
			var btv = s.Threads.BeginThread();

			try
			{
				Assert.IsTrue(btv.Item2.isCritical);
				Assert.IsFalse(btv.Item2.allowThreadToBeInterrupted);
			}
			finally
			{
				_ = s.Threads.EndThread(btv);
			}
		}

		[Test, Category("Threading")]
		public void LowerPriorityEventsAreDroppedInsteadOfBuffered()
		{
			var context = UseQueuedMainContext();
			var calls = 0;
			s.Threads.CurrentThread.priority = 1;

			s.EventScheduler.EnqueueThreadLaunch(0, false, false, () => calls++, false);
			context.DrainAll();

			Assert.AreEqual(0, calls);

			s.Threads.CurrentThread.priority = 0;
			s.EventScheduler.SchedulePump();
			context.DrainAll();

			Assert.AreEqual(0, calls);
		}

		[Test, Category("Threading")]
		public void CriticalThreadBecomesInterruptibleWithinDialogScope()
		{
			var btv = s.Threads.BeginThread();

			try
			{
				_ = Flow.Critical();
				Assert.IsFalse(s.Threads.IsInterruptible());

				using (Flow.BeginDialogInterruptibilityScope())
					Assert.IsTrue(s.Threads.IsInterruptible());

				Assert.IsFalse(s.Threads.IsInterruptible());
			}
			finally
			{
				_ = s.Threads.EndThread(btv);
			}
		}

		[Test, Category("Threading")]
		public void DialogScopeDoesNotOverrideGlobalInterruptionBlock()
		{
			var btv = s.Threads.BeginThread();

			try
			{
				_ = Flow.Critical();
				s.FlowData.allowInterruption = false;

				try
				{
					using (Flow.BeginDialogInterruptibilityScope())
						Assert.IsFalse(s.Threads.IsInterruptible());
				}
				finally
				{
					s.FlowData.allowInterruption = true;
				}
			}
			finally
			{
				_ = s.Threads.EndThread(btv);
			}
		}

		[Test, Category("Threading")]
		public void PreemptiveChecksRespectPeekFrequency()
		{
			var btv = s.Threads.BeginThread();

			try
			{
				_ = Flow.Critical(50);
				s.RecordMessageCheck(Environment.TickCount64);
				s.preemptiveMessageCheckPending = true;

				Assert.IsTrue(Flow.IsTrueAndRunning(true));
				Assert.IsTrue(s.preemptiveMessageCheckPending);

				s.RecordMessageCheck(Environment.TickCount64 - 60);
				s.preemptiveMessageCheckPending = true;

				Assert.IsTrue(Flow.IsTrueAndRunning(true));
				Assert.IsFalse(s.preemptiveMessageCheckPending);
			}
			finally
			{
				_ = s.Threads.EndThread(btv);
			}
		}

		[Test, Category("Threading")]
		public void CriticalMinusOneDisablesPreemptiveChecks()
		{
			var btv = s.Threads.BeginThread();

			try
			{
				_ = Flow.Critical(-1);
				s.RecordMessageCheck(0);
				s.preemptiveMessageCheckPending = true;

				Assert.IsTrue(Flow.IsTrueAndRunning(true));
				Assert.IsTrue(s.preemptiveMessageCheckPending);
				Assert.AreEqual(-1, s.GetPeekFrequency());
			}
			finally
			{
				_ = s.Threads.EndThread(btv);
			}
		}
	}
}
