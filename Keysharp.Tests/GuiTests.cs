using System.Collections.Concurrent;
using Assert = NUnit.Framework.Legacy.ClassicAssert;
using Keysharp.Internals;
using Keysharp.Internals.Images;
#if OSX
using AppKit = MonoMac.AppKit;
#endif

namespace Keysharp.Tests
{
	public class GuiTests : TestRunner
	{
		private const string MsgBoxTitle = "this is a sample title";

		[Test, Category("Gui")]
		public void DisplayTopologySelectsLargestIntersectionThenNearestDisplay()
		{
			DisplayInfo[] displays =
			[
				new("primary", new ScreenRect(0, 0, 1920, 1080), new ScreenRect(0, 0, 1920, 1040), 1.0, true),
				new("left", new ScreenRect(-2560, 0, 2560, 1440), new ScreenRect(-2560, 0, 2560, 1400), 1.5, false),
				new("above", new ScreenRect(0, -1200, 1600, 1200), new ScreenRect(0, -1200, 1600, 1160), 2.0, false)
			];

			Assert.IsTrue(DisplayTopology.TryFind(displays, new ScreenRect(-300, 100, 400, 300), out var spanning));
			Assert.AreEqual("left", spanning.Name, "largest visible intersection should own a spanning overlay");

			Assert.IsTrue(DisplayTopology.TryFind(displays, new ScreenRect(100, -100, 0, 0), out var point));
			Assert.AreEqual("above", point.Name, "zero-size point lookup should honor negative monitor origins");

			Assert.IsTrue(DisplayTopology.TryFind(displays, new ScreenRect(4000, 100, 20, 20), out var nearest));
			Assert.AreEqual("primary", nearest.Name, "off-desktop placement should use the nearest display deterministically");

			DisplayInfo[] unequalDisplays =
			[
				new("large", new ScreenRect(0, 0, 4000, 1000), new ScreenRect(0, 0, 4000, 1000), 1, true),
				new("small", new ScreenRect(4200, 0, 100, 1000), new ScreenRect(4200, 0, 100, 1000), 1, false)
			];
			Assert.IsTrue(DisplayTopology.TryFind(unequalDisplays, new ScreenRect(4050, 500, 0, 0), out var edgeNearest));
			Assert.AreEqual("large", edgeNearest.Name, "nearest lookup must compare display edges, not centres");
		}

		[Test, Category("Gui")]
		public void DisplayTopologyHandlesExtremeInputWithoutOverflow()
		{
			DisplayInfo[] displays =
			[
				new("left", new ScreenRect(int.MinValue, -10, 100, 20), new ScreenRect(int.MinValue, -10, 100, 20), 1, false),
				new("right", new ScreenRect(int.MaxValue - 99, -10, 100, 20), new ScreenRect(int.MaxValue - 99, -10, 100, 20), 1, true)
			];

			Assert.IsTrue(DisplayTopology.TryFind(displays, new ScreenRect(int.MinValue, 0, int.MaxValue, 1), out var selected));
			Assert.AreEqual("left", selected.Name);
			Assert.AreEqual((long)int.MaxValue + 1, new ScreenRect(int.MaxValue, 0, 1, 1).Right);
		}

		[Test, Category("Gui")]
		public void ScreenCaptureMapsWindowsMixedUiScalingOneToOne()
		{
			// PMv2 Windows exposes both monitors in physical desktop pixels. A 1920-wide 100% display followed
			// by a 2560-wide 150% display is therefore one 4480-pixel-wide capture; UI scale is not applied here.
			var desktop = new ScreenRect(0, 0, 4480, 1440);
			var pixels = new PixelSize(4480, 1440);

			Assert.AreEqual(new Point(2920, 700), desktop.PixelToScreen(new Point(2920, 700), pixels));
			Assert.AreEqual(new Point(2920, 700), desktop.ScreenToPixel(2920, 700, pixels));
		}

		[Test, Category("Gui")]
		public void ScreenCaptureMapsDenseRasterPixelsToNativeScreenUnits()
		{
			var twice = new ScreenRect(10, 20, 2, 1);
			var fractional = new ScreenRect(10, 20, 2, 2);
			var fiveForFour = new ScreenRect(10, 20, 4, 1);
			var fractionalSeam = new ScreenRect(0, 0, 6, 1);
			var roundedCanvasSeam = new ScreenRect(0, 0, 5, 1);
			// A 1920-point non-Retina display beside a 1440-point Retina display is flattened to a
			// 6720-pixel 2x canvas. X=4840 is 500 logical points into the second display.
			var mixedMacDesktop = new ScreenRect(0, 0, 3360, 1080);

			Assert.AreEqual(new Point(11, 20), twice.PixelToScreen(new Point(3, 1), new PixelSize(4, 2)));
			Assert.AreEqual(new Point(11, 21), fractional.PixelToScreen(new Point(1, 1), new PixelSize(3, 3)));
			Assert.AreEqual(new Point(11, 21), fractional.PixelToScreen(new Point(2, 2), new PixelSize(3, 3)));
			Assert.AreEqual(new Point(11, 21), fractional.PixelToScreen(
				fractional.ScreenToPixel(11, 21, new PixelSize(3, 3)), new PixelSize(3, 3)));
			Assert.AreEqual(new Point(13, 20), fiveForFour.PixelToScreen(
				fiveForFour.ScreenToPixel(13, 20, new PixelSize(5, 1)), new PixelSize(5, 1)));
			Assert.AreEqual(new Point(3, 0), fractionalSeam.PixelToScreen(new Point(4, 0), new PixelSize(9, 1)));
			Assert.AreEqual(new Point(2, 0), roundedCanvasSeam.PixelToScreen(new Point(4, 0), new PixelSize(8, 1)));
			Assert.AreEqual(new Point(3, 0), roundedCanvasSeam.PixelToScreen(new Point(5, 0), new PixelSize(8, 1)));
			Assert.AreEqual(new Point(2420, 300), mixedMacDesktop.PixelToScreen(
				new Point(4840, 600), new PixelSize(6720, 2160)));
		}

		[Test, Category("Gui")]
		public void OverlayUpdateStagesNativeScreenGeometryWhileHidden()
		{
			using var image = KeysharpImage.Create(null, 20, 12, "0xFF204060") as KeysharpImage;
			var overlay = new Ks.KeysharpOverlay();
			_ = overlay.__New(1L, 2L, 20L, 12L);

			try
			{
				_ = overlay.Update(image, 31L, 42L, 30L, 18L);

				Assert.AreEqual(31L, overlay.X);
				Assert.AreEqual(42L, overlay.Y);
				Assert.AreEqual(30L, overlay.W);
				Assert.AreEqual(18L, overlay.H);
				Assert.AreEqual(false, overlay.Visible);
			}
			finally
			{
				_ = overlay.Destroy();
			}
		}

		[Test, Category("Gui")]
		public void OverlayRedrawStagesTargetSizedCanvasAndGeometryTogether()
		{
			var overlay = new Ks.KeysharpOverlay();
			_ = overlay.__New(1L, 2L, 10L, 10L);

			try
			{
				var callback = new FuncObj((Func<object, object>)(target =>
				{
					_ = ((Ks.KeysharpOverlay)target).Clear("0xFF204060");
					return 0L;
				}));
				_ = overlay.Redraw(callback, 31L, 42L, 30L, 18L);

				Assert.AreEqual(31L, overlay.X);
				Assert.AreEqual(42L, overlay.Y);
				Assert.AreEqual(30L, overlay.W);
				Assert.AreEqual(18L, overlay.H);
				Assert.AreEqual(false, overlay.Visible);
			}
			finally
			{
				_ = overlay.Destroy();
			}
		}

		[Test, Category("Gui")]
		public void OverlayDerivesUnspecifiedDisplaySizeFromImagePixels()
		{
			using var image = KeysharpImage.Create(null, 20, 12, "0xFF204060", 2.0) as KeysharpImage;
			var overlay = new Ks.KeysharpOverlay();
			_ = overlay.__New(1L, 2L);

			try
			{
				_ = overlay.SetImage(image);
				Assert.AreEqual(40L, overlay.W);
				Assert.AreEqual(24L, overlay.H);
			}
			finally
			{
				_ = overlay.Destroy();
			}
		}

#if WINDOWS
		[Test, Category("Gui")]
		[Apartment(ApartmentState.STA)]
		public void WinSetStyleDoesNotAlterExStyle()
		{
			using var form = new Form { Text = nameof(WinSetStyleDoesNotAlterExStyle) };
			var handle = form.Handle;
			var oldDetectHiddenWindows = A_DetectHiddenWindows;
			var originalStyle = WindowsAPI.GetWindowLongPtr(handle, WindowsAPI.GWL_STYLE).ToInt64();
			var originalExStyle = WindowsAPI.GetWindowLongPtr(handle, WindowsAPI.GWL_EXSTYLE).ToInt64();
			var newStyle = originalStyle ^ WindowsAPI.WS_DISABLED;

			try
			{
				A_DetectHiddenWindows = true;
				WindowX.WinSetStyle(newStyle, $"ahk_id {handle.ToInt64()}");

				Assert.AreEqual(newStyle, WindowsAPI.GetWindowLongPtr(handle, WindowsAPI.GWL_STYLE).ToInt64());
				Assert.AreEqual(originalExStyle, WindowsAPI.GetWindowLongPtr(handle, WindowsAPI.GWL_EXSTYLE).ToInt64());
			}
			finally
			{
				_ = WindowsAPI.SetWindowLongPtr(handle, WindowsAPI.GWL_STYLE, new nint(originalStyle));
				_ = WindowsAPI.SetWindowLongPtr(handle, WindowsAPI.GWL_EXSTYLE, new nint(originalExStyle));
				A_DetectHiddenWindows = oldDetectHiddenWindows;
			}
		}

		[Test, Category("Gui")]
		[Apartment(ApartmentState.STA)]
		public void WinSetExStyleDoesNotAlterStyle()
		{
			using var form = new Form { Text = nameof(WinSetExStyleDoesNotAlterStyle) };
			var handle = form.Handle;
			var oldDetectHiddenWindows = A_DetectHiddenWindows;
			var originalStyle = WindowsAPI.GetWindowLongPtr(handle, WindowsAPI.GWL_STYLE).ToInt64();
			var originalExStyle = WindowsAPI.GetWindowLongPtr(handle, WindowsAPI.GWL_EXSTYLE).ToInt64();
			var newExStyle = originalExStyle ^ WindowsAPI.WS_EX_TOOLWINDOW;

			try
			{
				A_DetectHiddenWindows = true;
				WindowX.WinSetExStyle(newExStyle, $"ahk_id {handle.ToInt64()}");

				Assert.AreEqual(originalStyle, WindowsAPI.GetWindowLongPtr(handle, WindowsAPI.GWL_STYLE).ToInt64());
				Assert.AreEqual(newExStyle, WindowsAPI.GetWindowLongPtr(handle, WindowsAPI.GWL_EXSTYLE).ToInt64());
			}
			finally
			{
				_ = WindowsAPI.SetWindowLongPtr(handle, WindowsAPI.GWL_STYLE, new nint(originalStyle));
				_ = WindowsAPI.SetWindowLongPtr(handle, WindowsAPI.GWL_EXSTYLE, new nint(originalExStyle));
				A_DetectHiddenWindows = oldDetectHiddenWindows;
			}
		}
#endif

#if !WINDOWS
		[Test, Category("Gui")]
		public void MainWindowInitializesHidden()
		{
			SkipIfUiInitializationBlocked("Test requires a live Eto Application (macOS testhost cannot drive AppKit).");
			var shown = false;
			using var mainWindow = new Keysharp.Internals.UI.Unix.MainWindow();
			mainWindow.Shown += (_, _) => shown = true;

			mainWindow.InitializeHidden();

			Assert.AreNotEqual(0, mainWindow.NativeHandle);
			Assert.IsFalse(mainWindow.Visible);
			Assert.IsFalse(shown);
		}
#endif

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
#if WINDOWS
		// Same STA apartment as Theme so the two share a per-test STA thread that is torn down afterward. Otherwise this
		// test's message loop strands a SystemEvents (dark-mode/theming) subscription on the persistent runner thread,
		// and Theme's Application.SetColorMode later deadlocks marshaling a synchronous notification back to it.
		[Apartment(ApartmentState.STA)]
#endif
		public void MsgBox()
		{
			if (Script.IsHeadless)
				Assert.Ignore("MsgBox requires an interactive desktop session.");
#if LINUX
			if (!Keysharp.Internals.Platform.Desktop.IsX11Available)
				Assert.Ignore("Linux MsgBox automation currently requires X11-backed Eto windows.");
#endif

			var (cts, task) = StartMsgBoxAutoAccept();
			KeysharpForm form = null;
			try
			{
				form = CreateMsgBoxHostForm();
				form.Shown += Form_Shown;
				RunMsgBoxHost(form);
			}
			finally
			{
				form?.Close();
				cts.Cancel();
				task.Wait();
			}

			var timeoutForm = CreateMsgBoxHostForm();
			timeoutForm.Shown += TimeoutForm_Shown;
			RunMsgBoxHost(timeoutForm);
		}

		[Test, Category("Gui")]
#if WINDOWS
		[Apartment(ApartmentState.STA)]
#endif
		public void Theme()
		{
#if WINDOWS
			var originalTheme = Ks.A_GuiTheme.ToString();

			try
			{
				Ks.A_GuiTheme = "Dark";
				Assert.AreEqual("Dark", Ks.A_GuiTheme);

				Ks.A_GuiTheme = "System";
				Assert.AreEqual("System", Ks.A_GuiTheme);

				Ks.A_GuiTheme = "Classic";
				Assert.AreEqual("Classic", Ks.A_GuiTheme);
			}
			finally
			{
				Ks.A_GuiTheme = originalTheme;
			}
#else
			var originalTheme = Ks.A_GuiTheme.ToString();

			try
			{
				Ks.A_GuiTheme = "Dark";
				Assert.AreEqual("Dark", Ks.A_GuiTheme);
			}
			finally
			{
				Ks.A_GuiTheme = originalTheme;
			}
#endif
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
			ret = Dialogs.MsgBox("cancel-try-continue, exclamation, def: 1", MsgBoxTitle, "0x36");
			Assert.AreEqual(ret, "Cancel");
#endif
			(sender as Form)?.Close();
		}

		private static void TimeoutForm_Shown(object sender, EventArgs e)
		{
			var ret = Dialogs.MsgBox("ok, hand, def: 1, timeout: 0.2", MsgBoxTitle, "0 16 t0.2");
			Assert.AreEqual("Timeout", ret);
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
