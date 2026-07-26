namespace Keysharp.Tests
{
	[TestFixture, NonParallelizable]
	public class HotCriterionExecutorTests : TestRunner
	{
		private sealed class TestCriterion(Func<object> callback) : IFuncObj
		{
			public object Inst { get; set; }
			public bool IsBuiltIn => false;
			public bool IsValid => true;
			public string Name => nameof(TestCriterion);
			public IFuncObj Bind(params object[] obj) => this;
			public object Call(params object[] obj) => callback();
			public object CallInst(object inst, params object[] obj) => callback();
			public bool IsByRef(object obj = null) => false;
			public bool IsOptional(object obj = null) => false;
		}

		[Test, Category("Input")]
		public void TimedOutWorkersGrowIncrementallyToTheConfiguredLimit()
		{
			using var executor = new HotCriterionExecutor(3);
			using var release = new ManualResetEventSlim(false);
			using var entered = new CountdownEvent(3);
			var blocked = new TestCriterion(() =>
			{
				entered.Signal();
				release.Wait();
				return 1L;
			});

			try
			{
				for (var expectedWorkers = 1; expectedWorkers <= 3; expectedWorkers++)
				{
					var status = executor.Execute(blocked, HotCriterionEnum.IfCallback, "test", null,
						DeadlineAfter(250), out _, out _);
					Assert.That(status, Is.EqualTo(CriterionExecutionStatus.TimedOut));
					Assert.That(executor.WorkerCount, Is.EqualTo(expectedWorkers));
					Assert.That(entered.CurrentCount, Is.EqualTo(3 - expectedWorkers));
				}

				var quick = new TestCriterion(() => 42L);
				var rejected = executor.Execute(quick, HotCriterionEnum.IfCallback, "test", null,
					DeadlineAfter(1000), out _, out _);
				Assert.That(rejected, Is.EqualTo(CriterionExecutionStatus.Rejected));

				release.Set();
				var value = 0L;
				var recovered = SpinWait.SpinUntil(() =>
					executor.Execute(quick, HotCriterionEnum.IfCallback, "test", null,
						DeadlineAfter(1000), out value, out _) == CriterionExecutionStatus.Completed,
					2000);
				Assert.That(recovered, Is.True);
				Assert.That(value, Is.EqualTo(42L));
				Assert.That(executor.WorkerCount, Is.EqualTo(3));
			}
			finally
			{
				release.Set();
			}
		}

		[Test, Category("Input")]
		public void OnlyHookOriginatedCriteriaUseTheExecutor()
		{
			var callerThread = Environment.CurrentManagedThreadId;
			var evaluatedThread = 0;
			var criterion = new TestCriterion(() =>
			{
				evaluatedThread = Environment.CurrentManagedThreadId;
				return 1L;
			});
			var executor = Script.TheScript.HookThread.HotCriterionExecutor;

			Assert.That(executor.WorkerCount, Is.Zero);
			Assert.That(HotkeyDefinition.HotCriterionAllowsFiring(criterion, "test"), Is.EqualTo(1L));
			Assert.That(evaluatedThread, Is.EqualTo(callerThread));
			Assert.That(executor.WorkerCount, Is.Zero);

			evaluatedThread = 0;
			using (HookThread.BeginHotIfCallback(HookThread.HotIfCallbackBudgetMilliseconds))
				Assert.That(HotkeyDefinition.HotCriterionAllowsFiring(criterion, "test"), Is.EqualTo(1L));

			Assert.That(evaluatedThread, Is.Not.EqualTo(callerThread));
			Assert.That(executor.WorkerCount, Is.EqualTo(1));
		}

		private static long DeadlineAfter(int milliseconds)
			=> Stopwatch.GetTimestamp() + Stopwatch.Frequency * milliseconds / 1000;
	}
}
