using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Keysharp.Tests
{
	[TestFixture, NonParallelizable]
	public class TimerSchedulerTests : TestRunner
	{
		[Test, Category("Threading")]
		public void BlockedTimerFiresOnce()
		{
			var output = RunScript(string.Concat(path, "timer-blocked-fires-once.ahk"), "timer-blocked-fires-once", true, false);
			Assert.AreEqual("F|done", output.Trim(), output);
		}

		[Test, Category("Threading")]
		public void ShortTimerCallbackKeepsPeriod() => Assert.IsTrue(TestScript("timer-short-callback-keeps-period", false));

		[Test, Category("Threading")]
		public void LongTimerCallbackCoalesces() => Assert.IsTrue(TestScript("timer-long-callback-coalesces", false));
	}
}
