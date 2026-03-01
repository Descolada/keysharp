using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Keysharp.Tests
{
	public partial class MonitorTests : TestRunner
	{
		private static void SkipIfGuiHeadless()
		{
			if (Script.IsHeadless)
				Assert.Ignore("Monitor tests require a non-headless GUI session.");
		}

		[Test, Category("Monitor")]
		public void MonitorGet()
		{
			SkipIfGuiHeadless();
			VarRef l = new(null), t = new(null), r = new(null), b = new(null);
			var monget = Core.Monitor.MonitorGet(null, l, t, r, b);
			Assert.IsTrue(l.__Value.Ai() >= 0);
			Assert.IsTrue(r.__Value.Ai() >= 0);
			Assert.IsTrue(t.__Value.Ai() >= 0);
			Assert.IsTrue(b.__Value.Ai() >= 0);
			Assert.IsTrue(monget.Ai() > 0);
			Assert.IsTrue(TestScript("monitor-monitorget", true));
		}

		[Test, Category("Monitor")]
		public void MonitorGetCount()
		{
			SkipIfGuiHeadless();
			var ct = Core.Monitor.MonitorGetCount();
			Assert.IsTrue(ct > 0);
			Assert.IsTrue(TestScript("monitor-monitorgetcount", true));
		}

		[Test, Category("Monitor")]
		public void MonitorGetName()
		{
			SkipIfGuiHeadless();
			var names = "";
			var ct = Core.Monitor.MonitorGetCount();

			for (var i = 1; i <= ct; i++)
				names += Core.Monitor.MonitorGetName(i) + Environment.NewLine;

			Assert.IsTrue(names != "");
			Assert.IsTrue(TestScript("monitor-monitorgetname", true));
		}

		[Test, Category("Monitor")]
		public void MonitorGetPrimary()
		{
			SkipIfGuiHeadless();
			var ct = Core.Monitor.MonitorGetPrimary();
			Assert.IsTrue(ct > 0);
			Assert.IsTrue(TestScript("monitor-monitorgetprimary", true));
		}

		[Test, Category("Monitor")]
		public void MonitorGetWorkArea()
		{
			SkipIfGuiHeadless();
			VarRef l = new(null), t = new(null), r = new(null), b = new(null);
			var monget = Core.Monitor.MonitorGetWorkArea(null, l, t, r, b);
			Assert.IsTrue(l.__Value.Ai() >= 0);
			Assert.IsTrue(r.__Value.Ai() >= 0);
			Assert.IsTrue(t.__Value.Ai() >= 0);
			Assert.IsTrue(b.__Value.Ai() >= 0);
			Assert.IsTrue(monget.Ai() > 0);
			Assert.IsTrue(TestScript("monitor-monitorgetworkarea", true));
		}
	}
}
