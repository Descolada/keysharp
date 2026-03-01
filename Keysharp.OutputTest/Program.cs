using static Keysharp.Core.Accessors;
//using static Keysharp.Core.COM.Com;
using static Keysharp.Core.Common.Keyboard.HotkeyDefinition;
using static Keysharp.Core.Common.Keyboard.HotstringDefinition;
using static Keysharp.Core.Common.Keyboard.HotstringManager;
using static Keysharp.Core.ControlX;
using static Keysharp.Core.Debug;
using static Keysharp.Core.Dialogs;
using static Keysharp.Core.Dir;
using static Keysharp.Core.Dll;
using static Keysharp.Core.Drive;
using static Keysharp.Core.EditX;
using static Keysharp.Core.Env;
using static Keysharp.Core.Errors;
using static Keysharp.Core.External;
using static Keysharp.Core.Files;
using static Keysharp.Core.Flow;
using static Keysharp.Core.Functions;
using static Keysharp.Core.GuiHelper;
using static Keysharp.Core.ImageLists;
using static Keysharp.Core.Images;
using static Keysharp.Core.Ini;
using static Keysharp.Core.Input;
using static Keysharp.Core.Keyboard;
using static Keysharp.Core.Loops;
using static Keysharp.Core.Maths;
using static Keysharp.Core.Menu;
using static Keysharp.Core.Misc;
using static Keysharp.Core.Monitor;
using static Keysharp.Core.Mouse;
using static Keysharp.Core.Network;
using static Keysharp.Core.Processes;
using static Keysharp.Core.RegEx;
//using static Keysharp.Core.Registrys;
using static Keysharp.Core.Screen;
using static Keysharp.Core.Sound;
using static Keysharp.Core.Strings;
using static Keysharp.Core.ToolTips;
using static Keysharp.Core.Types;
using static Keysharp.Core.WindowX;
//using static Keysharp.Core.Windows.WindowsAPI;
using static Keysharp.Scripting.Script.Operator;
using static Keysharp.Scripting.Script;

[assembly: Keysharp.Scripting.AssemblyBuildVersionAttribute("0.0.0.13")]
namespace Keysharp.CompiledMain
{
	using System;
	using System.Runtime.InteropServices;
	using Keysharp.Core;
	using Keysharp.Core.Common;
	using Keysharp.Core.Common.File;
	using Keysharp.Core.Common.Invoke;
	using Keysharp.Core.Common.ObjectBase;
	using Keysharp.Core.Common.Strings;
	using Keysharp.Core.Common.Threading;
	using Keysharp.Scripting;
	using Array = Keysharp.Core.Array;
	using Buffer = Keysharp.Core.Buffer;
	using String = Keysharp.Core.String;

	public class Program
	{
		[System.STAThreadAttribute()]
		public static int Main(string[] args)
		{
			try
			{
				MainScript.SetName("*");
				if (Keysharp.Scripting.Script.HandleSingleInstance(Keysharp.Core.Accessors.A_ScriptName, Keysharp.Scripting.eScriptInstance.Prompt))
					return 0;
				Keysharp.Core.Env.HandleCommandLineParams(args);
				MainScript.RunMainWindow(Keysharp.Core.Accessors.A_ScriptName, AutoExecSection, false);
				MainScript.WaitThreads();
			}
			catch (Keysharp.Core.Flow.UserRequestedExitException)
			{
			}
			catch (Keysharp.Core.KeysharpException kserr)
			{
				if (!kserr.UserError.Processed)
					Keysharp.Core.Errors.ErrorOccurred(kserr.UserError, kserr.UserError.ExcType);
				if (!kserr.UserError.Handled && !MainScript.SuppressErrorOccurredDialog)
					Keysharp.Core.Dialogs.MsgBox("Uncaught Keysharp exception:\r\n" + kserr, Keysharp.Core.Accessors.A_ScriptName + ": Unhandled exception", "iconx");
				Keysharp.Core.Flow.ExitApp(1);
			}
			catch (System.Exception mainex)
			{
				var ex = mainex.InnerException ?? mainex;
				if (ex is Keysharp.Core.KeysharpException kserr)
				{
					if (!kserr.UserError.Processed)
						Keysharp.Core.Errors.ErrorOccurred(kserr.UserError, kserr.UserError.ExcType);
					if (!kserr.UserError.Handled && !MainScript.SuppressErrorOccurredDialog)
						Keysharp.Core.Dialogs.MsgBox("Uncaught Keysharp exception:\r\n" + kserr, Keysharp.Core.Accessors.A_ScriptName + ": Unhandled exception", "iconx");
				}
				else if (!MainScript.SuppressErrorOccurredDialog)
					Keysharp.Core.Dialogs.MsgBox("Uncaught exception:\r\n" + "Message: " + ex.Message + "\r\nStack: " + ex.StackTrace, Keysharp.Core.Accessors.A_ScriptName + ": Unhandled exception", "iconx");
				Keysharp.Core.Flow.ExitApp(1);
			}

			return System.Environment.ExitCode;
		}

		private static Keysharp.Scripting.Script MainScript = new Keysharp.Scripting.Script(typeof(Program));
		private static Keysharp.Core.Common.Keyboard.HotstringManager MainHotstringManager = MainScript.HotstringManager;
		public class __Main : Module
		{
			public static object msgbox = Keysharp.Core.Functions.Func((System.Delegate)Keysharp.Core.Dialogs.MsgBox);
			public static object AutoExecSection()
			{
				Keysharp.Core.Dialogs.MsgBox("Hello from Keysharp!");
				return "";
			}
		}

		public static object AutoExecSection()
		{
			Keysharp.Core.Common.Keyboard.HotkeyDefinition.ManifestAllHotkeysHotstringsHooks();
			MainScript.CurrentModuleType = typeof(Program.__Main);
			__Main.AutoExecSection();
			MainScript.CurrentModuleType = null;
			return "";
		}
	}
}
