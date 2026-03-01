using System.Runtime.InteropServices;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Keysharp.Tests
{
	public class EnvTests : TestRunner
	{
		[Test, Category("Env"), NonParallelizable]
#if WINDOWS
		[Apartment(ApartmentState.STA)]
#endif
		public void ClipboardAll()
		{
			//Flow.ResetState();
			Accessors.A_Clipboard = "Asdf";
			var baseline = Accessors.A_Clipboard as string;
#if !WINDOWS
			if (baseline != "Asdf")
				Assert.Ignore("Clipboard text is unavailable in this headless Linux environment.");
#endif
			var arr = new ClipboardAll();
			var clip = Accessors.A_Clipboard as string;
			Assert.AreEqual("Asdf", clip);
			Accessors.A_Clipboard = "";
			clip = Accessors.A_Clipboard as string;
			Assert.AreEqual("", clip);
			Accessors.A_Clipboard = arr;
			clip = Accessors.A_Clipboard as string;
			Assert.AreEqual("Asdf", clip);

			// ClipboardAll(Data, Size) should only construct an object, not write to clipboard.
			Accessors.A_Clipboard = "";
			var clone = new ClipboardAll(arr);
			clip = Accessors.A_Clipboard as string;
			Assert.AreEqual("", clip);

			Accessors.A_Clipboard = clone;
			clip = Accessors.A_Clipboard as string;
			Assert.AreEqual("Asdf", clip);
		}

		[Test, Category("Env"), NonParallelizable]
		public void ClipboardAllDataSize()
		{
			var source = new byte[] { 1, 2, 3, 4, 5 };
			var buf = new Keysharp.Core.Buffer(source);
			var clipFromBuffer = new ClipboardAll(buf, 3L);
			var roundtrip = clipFromBuffer.ToByteArray();
			Assert.AreEqual(3, roundtrip.Length);
			Assert.AreEqual(1, roundtrip[0]);
			Assert.AreEqual(2, roundtrip[1]);
			Assert.AreEqual(3, roundtrip[2]);

			var ptr = Marshal.AllocHGlobal(source.Length);

			try
			{
				Marshal.Copy(source, 0, ptr, source.Length);
				var clipFromPtr = new ClipboardAll((long)ptr, 4L);
				var ptrRoundtrip = clipFromPtr.ToByteArray();
				Assert.AreEqual(4, ptrRoundtrip.Length);
				Assert.AreEqual(1, ptrRoundtrip[0]);
				Assert.AreEqual(2, ptrRoundtrip[1]);
				Assert.AreEqual(3, ptrRoundtrip[2]);
				Assert.AreEqual(4, ptrRoundtrip[3]);
			}
			finally
			{
				Marshal.FreeHGlobal(ptr);
			}
		}

		/// <summary>
		/// This can fail periodically, but mostly works.
		/// If it fails in a batch, try running on its own.
		/// </summary>
		[Test, Category("Env"), NonParallelizable]
#if WINDOWS
		[Apartment(ApartmentState.STA)]
		public void ClipWait()
		{
			//Flow.ResetState();
			Clipboard.Clear();
			var dt = DateTime.UtcNow;
			var b = Env.ClipWait(0.5);
			var dt2 = DateTime.UtcNow;
			var ms = (dt2 - dt).TotalMilliseconds;
			Assert.AreEqual(false, b);//Will have timed out, so ErrorLevel will be 1.
			Assert.IsTrue(ms >= 500 && ms <= 3000);
			var tcs = new TaskCompletionSource<bool>();
			var thread = new Thread(() =>
			{
				Thread.Sleep(100);
				Clipboard.SetText("test text");
				tcs.SetResult(true);
			});
#if WINDOWS
			thread.SetApartmentState(ApartmentState.STA);
#endif
			thread.Start();
			dt = DateTime.UtcNow;
			b = Env.ClipWait(null, true);//Will wait indefinitely for any type.
			dt2 = DateTime.UtcNow;
			ms = (dt2 - dt).TotalMilliseconds;
			//Seems to take much longer than 100ms, but it's not too important.
			//Assert.IsTrue(ms >= 500 && ms <= 1100);
			tcs.Task.Wait();
			Assert.AreEqual(true, b);//Will have detected clipboard data, so ErrorLevel will be 0.
			//Now test with file paths.
			Clipboard.Clear();
			tcs = new TaskCompletionSource<bool>();
			thread = new Thread(() =>
			{
				Thread.Sleep(100);
				Clipboard.SetFileDropList(
					[
						"./testfile1.txt",
						"./testfile2.txt",
						"./testfile3.txt",
					]);
				tcs.SetResult(true);
			});
#if WINDOWS
			thread.SetApartmentState(ApartmentState.STA);
#endif
			thread.Start();
			dt = DateTime.UtcNow;
			b = Env.ClipWait();//Will wait indefinitely for only text or file paths.
			dt2 = DateTime.UtcNow;
			ms = (dt2 - dt).TotalMilliseconds;
			tcs.Task.Wait();
			Assert.AreEqual(true, b);//Will have detected clipboard data, so ErrorLevel will be 0.
			//Wait specifically for text/files, and copy an image. This should time out.
			Clipboard.Clear();
			var bitmap = new Bitmap(640, 480);
			tcs = new TaskCompletionSource<bool>();
			thread = new Thread(() =>
			{
				Thread.Sleep(100);
				Clipboard.SetImage(bitmap);
				tcs.SetResult(true);
			});
#if WINDOWS
			thread.SetApartmentState(ApartmentState.STA);
#endif
			thread.Start();
			dt = DateTime.UtcNow;
			b = Env.ClipWait(1);//Will wait for one second for only text or file paths.
			dt2 = DateTime.UtcNow;
			ms = (dt2 - dt).TotalMilliseconds;
			tcs.Task.Wait();
			Assert.AreEqual(false, b);//Will have timed out, so ErrorLevel will be 1.
			Accessors.A_Clipboard = "Asdf";
			Assert.IsTrue(TestScript("env-clipwait", true));//For this to work, the bitmap from above must be on the clipboard.
		}
#else
		public void ClipWait()
		{
			Assert.Ignore("ClipWait test currently requires Windows clipboard APIs.");
		}
#endif

		[Test, Category("Env"), NonParallelizable]
		public void EnvGet()
		{
			var path = Env.EnvGet("PATH");
			Assert.AreNotEqual(path, string.Empty);
			path = Env.EnvGet("dummynothing123");
			Assert.AreEqual(path, string.Empty);
			Assert.IsTrue(TestScript("env-envget", true));
		}

		[Test, Category("Env"), NonParallelizable]
		public void EnvSet()
		{
			var key = "dummynothing123";
			var s = "a test value";
			_ = Env.EnvSet(key, s); //Add the variable.
			var val = Env.EnvGet(key);
			Assert.AreEqual(val, s);
			_ = Env.EnvSet(key, null); //Delete the variable.
			val = Env.EnvGet(key);//Ensure it's deleted.
			Assert.AreEqual(val, string.Empty);
			Assert.IsTrue(TestScript("env-envset", true));
		}

		[Test, Category("Env"), NonParallelizable]
		public void EnvUpdate()
		{
			_ = Env.EnvUpdate();
			Assert.IsTrue(TestScript("env-envupdate", true));
		}

		[Test, Category("Env"), NonParallelizable]
		public void SysGet()
		{
			//Monitors
			var val = Env.SysGet(80);
			Assert.IsTrue(val.Ai(int.MinValue) >= 0);
			//SM_CXSCREEN
			val = Env.SysGet(0);
			Assert.IsTrue(val.Ai() == A_ScreenWidth);
			//Mouse buttons
			val = Env.SysGet(43);
			Assert.IsTrue(val.Ai() > 0);
			//Mouse present
			val = Env.SysGet(19);
			Assert.IsTrue(val.Ai() > 0);
			Assert.IsTrue(TestScript("env-sysget", true));
		}
	}
}


