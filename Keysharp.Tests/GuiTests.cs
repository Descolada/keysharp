using System.Collections.Concurrent;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Keysharp.Tests
{
	public class GuiTests : TestRunner
	{
		private const string MsgBoxTitle = "this is a sample title";

		[Test, Category("Gui")]
#if WINDOWS
		[Apartment(ApartmentState.STA)]
#endif
		public void FileSelect()
		{
			if (Script.IsHeadless)
				Assert.Ignore("FileSelect requires an interactive desktop session.");

			var fullpath = Path.GetFullPath(string.Concat(path, "DirCopy/file1.txt"));
			var files = Dialogs.FileSelect();
			//MsgBox(files);
			files = Dialogs.FileSelect("", fullpath, "Filename - Path and file", "");
			fullpath = Path.GetFullPath(string.Concat(path, "DirCopy/"));
			files = Dialogs.FileSelect("M", fullpath, "Filename - Path only - Multiselect", "");
			//MsgBox(files);
			fullpath = Path.GetFullPath(string.Concat(path, "DirCopy/file1.txt"));
			files = Dialogs.FileSelect("S16", fullpath, "Filename - Path and file - Text files filter - Save & prompt for overwrite", "Text files |*.txt;*.wri;*.ini");
			fullpath = Path.GetFullPath(string.Concat(path, "DirCopy/"));
			files = Dialogs.FileSelect("S16", fullpath, "Filename - Path only - Text files filter - Save & prompt for overwrite", "Text files |*.txt;*.wri;*.ini");
			//MsgBox(files);
			files = Dialogs.FileSelect("D", "D:\\", "", "");
		}

		[Test, Category("Gui")]
		public void MsgBox()
		{
			if (Script.IsHeadless)
				Assert.Ignore("MsgBox requires an interactive desktop session.");
#if LINUX
			if (!Keysharp.Core.Unix.PlatformManager.IsX11Available)
				Assert.Ignore("Linux MsgBox automation currently requires X11-backed Eto windows.");
#endif

			var (cts, task) = StartMsgBoxAutoAccept();

			try
			{
				var form = CreateMsgBoxHostForm();
				form.Shown += Form_Shown;
				RunMsgBoxHost(form);

				var ret = Dialogs.MsgBox("ok, hand, def: 1, timeout: 0.2", MsgBoxTitle, "0 16 t0.2");
				Assert.AreEqual("Timeout", ret);
			}
			finally
			{
				cts.Cancel();
				task.Wait();
			}
		}

		private void Form_Shown(object sender, EventArgs e)
		{
			var ret = Dialogs.MsgBox("ok, hand, def: 1", MsgBoxTitle, "0 16");
			Assert.AreEqual(ret.ToUpper(), "OK");
			ret = Dialogs.MsgBox("ok, hand, def: 1", MsgBoxTitle, 16);
			Assert.AreEqual(ret.ToUpper(), "OK");
			ret = Dialogs.MsgBox("ok-cancel, question, def: 2", MsgBoxTitle, "1 32 256");
			Assert.AreEqual(ret, "Cancel");
			ret = Dialogs.MsgBox("ok-cancel, question, def: 2", MsgBoxTitle, 1 | 32 | 256);
			Assert.AreEqual(ret, "Cancel");
			ret = Dialogs.MsgBox("yes-no-cancel, asterisk/info, def: 1", MsgBoxTitle, "3 64");
			Assert.AreEqual(ret, "Yes");
			ret = Dialogs.MsgBox("yes-no-cancel, asterisk/info, def: 1", MsgBoxTitle, 3 | 64);
			Assert.AreEqual(ret, "Yes");
			ret = Dialogs.MsgBox("yes-no, asterisk/info, def: 2", MsgBoxTitle, "4 64 256");
			Assert.AreEqual(ret, "No");
			ret = Dialogs.MsgBox("yes-no, asterisk/info, def: 2", MsgBoxTitle, 4 | 64 | 256);
			Assert.AreEqual(ret, "No");
#if WINDOWS
			ret = Dialogs.MsgBox("abort-retry-ignore, exclamation, def: 3, just: right", MsgBoxTitle, "2 48 512 524288");
			Assert.AreEqual(ret, "Ignore");
			ret = Dialogs.MsgBox("abort-retry-ignore, exclamation, def: 3, just: right", MsgBoxTitle, 2 | 48 | 512 | 524288);
			Assert.AreEqual(ret, "Ignore");
			ret = Dialogs.MsgBox("retry-cancel, asterisk/info, def: 1", MsgBoxTitle, "5 64");
			Assert.AreEqual(ret, "Retry");
			ret = Dialogs.MsgBox("retry-cancel, asterisk/info, def: 1", MsgBoxTitle, 5 | 64);
			Assert.AreEqual(ret, "Retry");
#endif
			(sender as Form)?.Close();
		}

		private static KeysharpForm CreateMsgBoxHostForm()
		{
#if WINDOWS
			return new KeysharpForm
			{
				Size = new System.Drawing.Size(500, 500),
				StartPosition = FormStartPosition.CenterScreen,
				Text = "MessageBox holder",
			};
#else
			return new KeysharpForm
			{
				Title = "MessageBox holder",
				Size = new Size(500, 500),
			};
#endif
		}

		private static void RunMsgBoxHost(KeysharpForm form)
		{
#if WINDOWS
			Application.Run(form);
#else
			var app = Application.Instance ?? new Application();
			app.MainForm = form;
			form.Show();

			while (form.Visible)
				app.RunIteration();

			if (ReferenceEquals(app.MainForm, form))
				app.MainForm = null;
#endif
		}

		private static (CancellationTokenSource cts, Task task) StartMsgBoxAutoAccept()
		{
			var cts = new CancellationTokenSource();

#if WINDOWS
			var task = Task.Run(() =>
			{
				while (!cts.IsCancellationRequested)
				{
					var wnd = WindowsAPI.FindWindow(null, MsgBoxTitle);

					if (wnd != 0)
					{
						_ = WindowsAPI.SetForegroundWindow(wnd);
						SendKeys.SendWait(" ");
						Thread.Sleep(100);
					}
					else
						Thread.Sleep(50);
				}
			});
#else
			var responses = new ConcurrentQueue<string>(new[] { "OK", "OK", "Cancel", "Cancel", "Yes", "Yes", "No", "No" });
			var task = Task.Run(() =>
			{
				while (!cts.IsCancellationRequested)
				{
					if (responses.TryPeek(out var response) && TryAcceptActiveMessageBox(response))
						_ = responses.TryDequeue(out _);

					Thread.Sleep(50);
				}
			});
#endif

			return (cts, task);
		}

#if !WINDOWS
		private static bool TryAcceptActiveMessageBox(string expectedResult)
		{
#if LINUX
			var accepted = false;
			Application.Instance.Invoke(() =>
			{
				foreach (var window in Gtk.Window.ListToplevels())
				{
					if (window is not Gtk.MessageDialog dialog || !dialog.Visible || dialog.Title != MsgBoxTitle)
						continue;

					dialog.Respond(expectedResult switch
					{
						"OK" => (int)Gtk.ResponseType.Ok,
						"Cancel" => (int)Gtk.ResponseType.Cancel,
						"Yes" => (int)Gtk.ResponseType.Yes,
						"No" => (int)Gtk.ResponseType.No,
						_ => (int)Gtk.ResponseType.None
					});
					accepted = true;
					break;
				}
			});
			return accepted;
#elif OSX
			var accepted = false;
			Application.Instance.Invoke(() =>
			{
				foreach (var window in AppKit.NSApplication.SharedApplication.Windows)
				{
					if (window?.IsVisible != true || window.Title != MsgBoxTitle)
						continue;

					if (TryClickMacButton(window, expectedResult))
					{
						accepted = true;
						break;
					}
				}
			});
			return accepted;
#else
			return false;
#endif
		}

#if OSX
		private static bool TryClickMacButton(AppKit.NSWindow window, string expectedResult)
		{
			return window.ContentView != null && TryClickMacButton(window.ContentView, expectedResult);
		}

		private static bool TryClickMacButton(AppKit.NSView view, string expectedResult)
		{
			if (view is AppKit.NSButton button && string.Equals(button.Title, expectedResult, StringComparison.OrdinalIgnoreCase))
			{
				button.PerformClick(null);
				return true;
			}

			foreach (var subview in view.Subviews)
			{
				if (TryClickMacButton(subview, expectedResult))
					return true;
			}

			return false;
		}
#endif
#endif
	}
}
