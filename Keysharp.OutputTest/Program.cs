using static Keysharp.Builtins.Accessors;
using static Keysharp.Builtins.ControlX;
using static Keysharp.Builtins.Debug;
using static Keysharp.Builtins.Dialogs;
using static Keysharp.Builtins.Dir;
using static Keysharp.Builtins.Dll;
using static Keysharp.Builtins.Drive;
using static Keysharp.Builtins.EditX;
using static Keysharp.Builtins.Env;
using static Keysharp.Builtins.Errors;
using static Keysharp.Builtins.External;
using static Keysharp.Builtins.Files;
using static Keysharp.Builtins.Flow;
using static Keysharp.Builtins.Functions;
using static Keysharp.Builtins.GuiHelper;
using static Keysharp.Builtins.ImageLists;
using static Keysharp.Builtins.Images;
using static Keysharp.Builtins.Ini;
using static Keysharp.Builtins.Input;
using static Keysharp.Builtins.Keyboard;
using static Keysharp.Builtins.Maths;
using static Keysharp.Builtins.Menu;
using static Keysharp.Builtins.Misc;
using static Keysharp.Builtins.Monitor;
using static Keysharp.Builtins.Mouse;
using static Keysharp.Builtins.Network;
using static Keysharp.Builtins.Processes;
using static Keysharp.Builtins.RegEx;
using static Keysharp.Builtins.Screen;
using static Keysharp.Builtins.Sound;
using static Keysharp.Builtins.Strings;
using static Keysharp.Builtins.ToolTips;
using static Keysharp.Builtins.Types;
using static Keysharp.Builtins.WindowX;
using static Keysharp.Runtime.Keyboard.HotkeyDefinition;
using static Keysharp.Runtime.Keyboard.HotstringManager;
using static Keysharp.Runtime.Script.Operator;
using static Keysharp.Runtime.Script;

[assembly: Keysharp.Runtime.AssemblyBuildVersionAttribute("2.1-alpha.18")]
namespace Keysharp.CompiledMain
{
	using System;
	using System.Runtime.InteropServices;
	using Keysharp.Builtins;
	using Keysharp.Runtime;
	using Array = Keysharp.Builtins.Array;
	using Buffer = Keysharp.Builtins.Buffer;
	using String = Keysharp.Builtins.String;

	public class Program
	{
		private static Eto.Forms.TextArea remapDebugText;
		private static Eto.Forms.Label remapDebugStatus;

		[System.STAThreadAttribute()]
		public static int Main(string[] args)
		{
			try
			{
				MainScript.SetName("*");
				if (Keysharp.Runtime.Script.HandleSingleInstance(Keysharp.Builtins.Accessors.A_ScriptName, Keysharp.Runtime.eScriptInstance.Prompt))
					return 0;
				Keysharp.Builtins.Env.HandleCommandLineParams(args);
				MainScript.RunMainWindow(Keysharp.Builtins.Accessors.A_ScriptName, AutoExecSection, false);
			}
			catch (System.Exception mainex)
			{
				var ex = Keysharp.Runtime.Flow.UnwrapException(mainex);
				if (ex is Keysharp.Builtins.Flow.UserRequestedExitException)
					return System.Environment.ExitCode;
				if (ex is Keysharp.Builtins.KeysharpException kserr)
				{
					Keysharp.Runtime.Script.TryProcessKeysharpException(MainScript, kserr);
				}
				else
				{
					Keysharp.Runtime.Script.TryProcessUnhandledException(MainScript, ex);
				}

				Keysharp.Runtime.Script.SafeExit(1);
			}

			return System.Environment.ExitCode;
		}

		private static Keysharp.Runtime.Script MainScript = new Keysharp.Runtime.Script(typeof(Program));

		private static void ShowRemapDebugGui()
		{
			remapDebugStatus = new Eto.Forms.Label
			{
				Text = "Focus here, then synthesize the configured key with inputd."
			};
			remapDebugText = new Eto.Forms.TextArea
			{
				Size = new Eto.Drawing.Size(380, 80),
				Wrap = false
			};

			var form = new Eto.Forms.Form
			{
				Title = "Keysharp remap debug: g::s",
				ClientSize = new Eto.Drawing.Size(420, 140),
				Content = new Eto.Forms.StackLayout
				{
					Padding = 12,
					Spacing = 8,
					Items =
					{
						remapDebugStatus,
						remapDebugText
					}
				}
			};

			form.Shown += (_, __) =>
			{
				form.Focus();
				remapDebugText.Focus();
				StartRemapDebugAutoTest();
			};
			form.Show();
		}

		private static void StartRemapDebugAutoTest()
		{
			var auto = System.Environment.GetEnvironmentVariable("KEYSHARP_REMAP_DEBUG_AUTO");

			if (string.IsNullOrEmpty(auto) || !(auto == "1" || auto.Equals("true", System.StringComparison.OrdinalIgnoreCase)))
				return;

			var hooktest = System.IO.Path.Combine(
				System.IO.Directory.GetCurrentDirectory(),
				"native",
				"keysharp-inputd",
				"build",
				"keysharp-inputd-hooktest");
			var socketPath = System.Environment.GetEnvironmentVariable("KEYSHARP_INPUTD_SOCKET");
			var sendVk = System.Environment.GetEnvironmentVariable("KEYSHARP_REMAP_DEBUG_VK");
			var expected = System.Environment.GetEnvironmentVariable("KEYSHARP_REMAP_DEBUG_EXPECT");

			if (string.IsNullOrWhiteSpace(sendVk))
				sendVk = "0x47";

			if (string.IsNullOrWhiteSpace(expected))
				expected = "s";

			if (string.IsNullOrWhiteSpace(socketPath))
			{
				var runtimeDir = System.Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
				socketPath = string.IsNullOrWhiteSpace(runtimeDir)
					? "/tmp/keysharp-inputd.sock"
					: System.IO.Path.Combine(runtimeDir, "keysharp", "keysharp-inputd.sock");
			}

			if (!System.IO.File.Exists(hooktest))
			{
				remapDebugStatus.Text = "FAIL: hooktest not found";
				return;
			}

			var timer = new Eto.Forms.UITimer { Interval = 0.5 };
			timer.Elapsed += (_, __) =>
			{
				timer.Stop();
				remapDebugText.Focus();

				try
				{
					System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
					{
						FileName = hooktest,
						ArgumentList = { "--socket", socketPath, "--send-vk", sendVk },
						UseShellExecute = false
					});
				}
				catch (System.Exception ex)
				{
					remapDebugStatus.Text = "FAIL: " + ex.Message;
					return;
				}

				var check = new Eto.Forms.UITimer { Interval = 1.0 };
				check.Elapsed += (_, __) =>
				{
					check.Stop();
					var text = remapDebugText.Text;
					var passed = text == expected;
					var status = passed ? $"PASS: received {expected}" : $"FAIL: received '{text}', expected '{expected}'";
					remapDebugStatus.Text = status;
					System.Console.WriteLine(status);
					System.Environment.ExitCode = passed ? 0 : 1;
					Eto.Forms.Application.Instance.Quit();
				};
				check.Start();
			};
			timer.Start();
		}

		[CompatibilityMode("2.0.0")]
		public class __Main : Module
		{
			public static object tooltip = Keysharp.Builtins.Functions.Func((System.Delegate)Keysharp.Builtins.ToolTips.ToolTip);
			public static object KS_Hotkey_1(params object[] args)
			{
				Keysharp.Builtins.Keyboard.SetKeyDelay(-1L);
				Keysharp.Builtins.Keyboard.SendEvent("{Blind}{a DownR}");
				return "";
			}

			public static object KS_Hotkey_2(params object[] args)
			{
				Keysharp.Builtins.Keyboard.SetKeyDelay(-1L);
				Keysharp.Builtins.Keyboard.SendEvent("{Blind}{a Up}");
				return "";
			}

			public static object KS_Hotkey_3(params object[] args)
			{
				Keysharp.Builtins.Keyboard.SetKeyDelay(-1L);
				Keysharp.Builtins.Keyboard.SendEvent("{Blind}{s DownR}");
				return "";
			}

			public static object KS_Hotkey_4(params object[] args)
			{
				Keysharp.Builtins.Keyboard.SetKeyDelay(-1L);
				Keysharp.Builtins.Keyboard.SendEvent("{Blind}{s Up}");
				return "";
			}

			public static object KS_Hotkey_5(params object[] args)
			{
				Keysharp.Builtins.Keyboard.SetKeyDelay(-1L);
				Keysharp.Builtins.Keyboard.Send("{Blind<^>^}{b DownR}");
				return "";
			}

			public static object KS_Hotkey_6(params object[] args)
			{
				Keysharp.Builtins.Keyboard.SetKeyDelay(-1L);
				Keysharp.Builtins.Keyboard.Send("{Blind}{b Up}");
				return "";
			}

			public static object KS_Hotkey_7(params object[] args)
			{
				Keysharp.Builtins.Mouse.SetMouseDelay(-1L);
				Keysharp.Builtins.Keyboard.Send("{Blind}{LButton DownR}");
				return "";
			}

			public static object KS_Hotkey_8(params object[] args)
			{
				Keysharp.Builtins.Mouse.SetMouseDelay(-1L);
				Keysharp.Builtins.Keyboard.Send("{Blind}{LButton Up}");
				return "";
			}

			public static object KS_Hotkey_9(params object[] args)
			{
				Keysharp.Builtins.Keyboard.SetKeyDelay(-1L);
				Keysharp.Builtins.Keyboard.Send("{Blind}{y DownR}");
				return "";
			}

			public static object KS_Hotkey_10(params object[] args)
			{
				Keysharp.Builtins.Keyboard.SetKeyDelay(-1L);
				Keysharp.Builtins.Keyboard.Send("{Blind}{y Up}");
				return "";
			}

			public static object KS_Hotkey_11(object thishotkey)
			{
				Keysharp.Runtime.Script.InvokeOrNull(tooltip, "Call", "Hello");
				return "";
			}

			public static object AutoExecSection()
			{
				return "";
			}

			public __Main(params object[] args) : base(null)
			{
			}
		}

		public static object AutoExecSection()
		{
			Keysharp.Runtime.Keyboard.HotkeyDefinition.AddHotkey(Keysharp.Builtins.Functions.Func((System.Delegate)__Main.KS_Hotkey_1), 0U, "*s");
			Keysharp.Runtime.Keyboard.HotkeyDefinition.AddHotkey(Keysharp.Builtins.Functions.Func((System.Delegate)__Main.KS_Hotkey_2), 0U, "*s up");
			Keysharp.Runtime.Keyboard.HotkeyDefinition.AddHotkey(Keysharp.Builtins.Functions.Func((System.Delegate)__Main.KS_Hotkey_3), 0U, "*g");
			Keysharp.Runtime.Keyboard.HotkeyDefinition.AddHotkey(Keysharp.Builtins.Functions.Func((System.Delegate)__Main.KS_Hotkey_4), 0U, "*g up");
			Keysharp.Runtime.Keyboard.HotkeyDefinition.AddHotkey(Keysharp.Builtins.Functions.Func((System.Delegate)__Main.KS_Hotkey_5), 0U, "*^a");
			Keysharp.Runtime.Keyboard.HotkeyDefinition.AddHotkey(Keysharp.Builtins.Functions.Func((System.Delegate)__Main.KS_Hotkey_6), 0U, "*^a up");
			Keysharp.Runtime.Keyboard.HotkeyDefinition.AddHotkey(Keysharp.Builtins.Functions.Func((System.Delegate)__Main.KS_Hotkey_7), 0U, "*RButton");
			Keysharp.Runtime.Keyboard.HotkeyDefinition.AddHotkey(Keysharp.Builtins.Functions.Func((System.Delegate)__Main.KS_Hotkey_8), 0U, "*RButton up");
			Keysharp.Runtime.Keyboard.HotstringManager.AddHotstring("::btw", null, ":btw", "btw", "by the way", false);
			Keysharp.Runtime.Keyboard.HotkeyDefinition.AddHotkey(Keysharp.Builtins.Functions.Func((System.Delegate)__Main.KS_Hotkey_9), 0U, "*x");
			Keysharp.Runtime.Keyboard.HotkeyDefinition.AddHotkey(Keysharp.Builtins.Functions.Func((System.Delegate)__Main.KS_Hotkey_10), 0U, "*x up");
			Keysharp.Runtime.Keyboard.HotkeyDefinition.AddHotkey(Keysharp.Builtins.Functions.Func((System.Delegate)__Main.KS_Hotkey_11), 0U, "CapsLock & a");
			Keysharp.Builtins.Flow.Persistent();
			Keysharp.Runtime.Keyboard.HotkeyDefinition.ManifestAllHotkeysHotstringsHooks();
			ShowRemapDebugGui();
			MainScript.CurrentModuleType = typeof(Program.__Main);
			__Main.AutoExecSection();
			MainScript.CurrentModuleType = null;
			return "";
		}
	}
}
