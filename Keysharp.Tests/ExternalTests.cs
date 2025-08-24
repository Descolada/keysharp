using static Keysharp.Core.External;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Keysharp.Tests
{
	public partial class ExternalTests : TestRunner
	{
#if WINDOWS
		[Test, Category("External")]
		public void CallbackCreate() => Assert.IsTrue(TestScript("external-callbackcreate", false));

		[Test, Category("External")]
		public void DllCall_()
		{
			LongPrimitive desktop = WindowsAPI.GetDesktopWindow();
			var rect = new RECT();
			var buf = Collections.Buffer(16, 0);
			_ = Dll.DllCall((StringPrimitive)"user32.dll\\GetWindowRect", (StringPrimitive)"ptr", desktop, (StringPrimitive)"ptr", buf);
			_ = WindowsAPI.GetWindowRect((nint)desktop, out rect);
			var l = (long)NumGet(buf, 0, "UInt");
			var t = (long)NumGet(buf, 4, "UInt");
			var r = (long)NumGet(buf, 8, "UInt");
			var b = (long)NumGet(buf, 12, "UInt");
			Assert.IsTrue(r > 0 && b > 0);
			Assert.AreEqual(rect.Left, l);
			Assert.AreEqual(rect.Right, r);
			Assert.AreEqual(rect.Top, t);
			Assert.AreEqual(rect.Bottom, b);
			string str = "lower";
			LongPrimitive len = str.Length;
			var strbuf = Strings.StringBuffer((StringPrimitive)str);
			_ = Dll.DllCall((StringPrimitive)"user32.dll\\CharUpperBuff", (StringPrimitive)"ptr", strbuf.Ptr, (StringPrimitive)"UInt", len);
			Assert.AreEqual(strbuf.ToString(), str.ToUpper());
			Assert.IsTrue(TestScript("external-dllcall", false));
		}

		[Test, Category("External")]
		public void NumPutNumGet() => Assert.IsTrue(TestScript("external-numput-numget", true));

		[Test, Category("External")]
		public void COM() => Assert.IsTrue(TestScript("external-com", false));

		[Test, Category("External")]
		public void OnMessage() => Assert.IsTrue(TestScript("external-onmessage", false));
#endif
	}
}