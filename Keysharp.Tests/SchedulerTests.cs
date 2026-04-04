using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Keysharp.Tests
{
	[TestFixture, NonParallelizable]
	public class SchedulerTests : TestRunner
	{
		[Test, Category("Threading")]
		public void InteractiveNested()
		{
			var context = UseQueuedMainContext();
			var scheduler = s.EventScheduler;
			var order = new List<string>();

			scheduler.EnqueueCallback(() =>
			{
				order.Add("H1");
				scheduler.EnqueueCallback(() => order.Add("H3"), ScriptEventQueue.Interactive, false);
				scheduler.EnqueueCallback(() => order.Add("H4"), ScriptEventQueue.Interactive, false);
				scheduler.EnqueueCallback(() => order.Add("N3"), ScriptEventQueue.Normal, false);
				scheduler.EnqueueCallback(() => order.Add("N4"), ScriptEventQueue.Normal, false);
			}, ScriptEventQueue.Interactive, false);
			scheduler.EnqueueCallback(() => order.Add("H2"), ScriptEventQueue.Interactive, false);
			scheduler.EnqueueCallback(() => order.Add("N1"), ScriptEventQueue.Normal, false);
			scheduler.EnqueueCallback(() => order.Add("N2"), ScriptEventQueue.Normal, false);

			Assert.AreEqual(1, context.PendingCount);

			context.DrainAll();

			Assert.That(order, Is.EqualTo(new[]
			{
				"H1", "H2", "H3", "H4",
				"N1", "N2", "N3", "N4"
			}));
		}

		[Test, Category("Threading")]
		public void BlockedInteractive()
		{
			var context = UseQueuedMainContext();
			var scheduler = s.EventScheduler;
			var order = new List<string>();
			var interactiveBlocked = true;

			scheduler.Enqueue(ScriptEventQueue.Interactive, () =>
			{
				if (interactiveBlocked)
					return ScriptEventExecutionResult.GlobalBlocked;

				order.Add("H1");
				return ScriptEventExecutionResult.Executed;
			});
			scheduler.EnqueueCallback(() => order.Add("N1"), ScriptEventQueue.Normal, false);

			context.DrainAll();

			Assert.IsEmpty(order);
			Assert.AreEqual(0, context.PendingCount);

			interactiveBlocked = false;
			scheduler.SchedulePump();
			context.DrainAll();

			Assert.That(order, Is.EqualTo(new[] { "H1", "N1" }));
		}

		[Test, Category("Threading")]
		public void BlockedNormalRetry()
		{
			var context = UseQueuedMainContext();
			var scheduler = s.EventScheduler;
			var order = new List<string>();
			var normalBlocked = true;

			scheduler.Enqueue(ScriptEventQueue.Normal, () =>
			{
				if (normalBlocked)
					return ScriptEventExecutionResult.GlobalBlocked;

				order.Add("N1");
				return ScriptEventExecutionResult.Executed;
			});

			context.DrainAll();

			Assert.IsEmpty(order);

			scheduler.EnqueueCallback(() => order.Add("H1"), ScriptEventQueue.Interactive, false);
			context.DrainAll();

			Assert.That(order, Is.EqualTo(new[] { "H1" }));

			normalBlocked = false;
			scheduler.SchedulePump();
			context.DrainAll();

			Assert.That(order, Is.EqualTo(new[] { "H1", "N1" }));
		}
	}
}
