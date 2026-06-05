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
		[CompatibilityMode("2.0.0")]
		public class __Main : Module
		{
			public static readonly object ks = new Keysharp.Builtins.Ks();
			public static object clr => MainScript.Vars.Statics[typeof(Keysharp.Builtins.Ks.Clr)];

			public static object encodedecodeuri = Keysharp.Builtins.Functions.Func((System.Delegate)FN_Encodedecodeuri);
			public static object purgeclipboardurl = Keysharp.Builtins.Functions.Func((System.Delegate)FN_Purgeclipboardurl);
			public static object clipwait = Keysharp.Builtins.Functions.Func((System.Delegate)Keysharp.Builtins.Env.ClipWait);
			public static object tooltip = Keysharp.Builtins.Functions.Func((System.Delegate)Keysharp.Builtins.ToolTips.ToolTip);
			public static object traytip = Keysharp.Builtins.Functions.Func((System.Delegate)Keysharp.Builtins.ToolTips.TrayTip);
			public static object send = Keysharp.Builtins.Functions.Func((System.Delegate)Keysharp.Builtins.Keyboard.Send);
			public static object regexmatch = Keysharp.Builtins.Functions.Func((System.Delegate)Keysharp.Builtins.RegEx.RegExMatch);
			public static object regexreplace = Keysharp.Builtins.Functions.Func((System.Delegate)Keysharp.Builtins.RegEx.RegExReplace);
			public static object instr = Keysharp.Builtins.Functions.Func((System.Delegate)Keysharp.Builtins.Strings.InStr);
			public static object strreplace = Keysharp.Builtins.Functions.Func((System.Delegate)Keysharp.Builtins.Strings.StrReplace);
			public static object strsplit = Keysharp.Builtins.Functions.Func((System.Delegate)Keysharp.Builtins.Strings.StrSplit);
			public static object SL_18_fn_encodedecodeuri_web = null;
			[UserDeclaredName("EncodeDecodeURI")]
			public static object FN_Encodedecodeuri(object str, [Optional, DefaultParameterValue(true)] object encode, [Optional, DefaultParameterValue(true)] object component)
			{
				Keysharp.Runtime.Variables.Dereference KS_Derefs_FN_Encodedecodeuri = new Keysharp.Runtime.Variables.Dereference(Keysharp.Runtime.eScope.Local, "FN_Encodedecodeuri", typeof(__Main), null);
				Keysharp.Runtime.Script.InitStaticVariable(ref SL_18_fn_encodedecodeuri_web, "__Main_SL_18_fn_encodedecodeuri_web", () => Keysharp.Runtime.Script.Invoke(clr, "Load", "System.Web.HttpUtility"));
				return Keysharp.Runtime.Script.Invoke(Keysharp.Runtime.Script.GetPropertyValue(SL_18_fn_encodedecodeuri_web, "HttpUtility"), System.String.Concat(new object[] { "Url", (Keysharp.Runtime.Script.IfTest(encode) ? "en" : "de"), "code" }), str);
			}

			[UserDeclaredName("PurgeClipboardURL")]
			public static object FN_Purgeclipboardurl()
			{
				object keep_question_marks = null;
				object prefixes = null;
				object whitelist = null;
				object global_exception = null;
				object each = null;
				object prefix = null;
				object exception = null;
				keep_question_marks = "";
				A_Clipboard = A_Clipboard;
				if (Keysharp.Runtime.Script.IfTest(Keysharp.Runtime.Script.Invoke(instr, "Call", A_Clipboard, " › ")))
				{
					A_Clipboard = Keysharp.Runtime.Script.Invoke(strreplace, "Call", A_Clipboard, " › ", "/");
				}

				if (Keysharp.Runtime.Script.IfTest(Keysharp.Runtime.Script.Invoke(instr, "Call", A_Clipboard, "%")))
				{
					A_Clipboard = Keysharp.Runtime.Script.Invoke(encodedecodeuri, "Call", A_Clipboard, false);
				}

				if (Keysharp.Runtime.Script.IfTest(Keysharp.Runtime.Script.Invoke(instr, "Call", A_Clipboard, "http://")))
				{
					A_Clipboard = Keysharp.Runtime.Script.Invoke(strreplace, "Call", A_Clipboard, "p", "ps");
				}

				if (Keysharp.Runtime.Script.IfTest(Keysharp.Runtime.Script.Invoke(instr, "Call", A_Clipboard, "://www.reddit.com")))
				{
					A_Clipboard = Keysharp.Runtime.Script.Invoke(strreplace, "Call", A_Clipboard, "://www", "://old");
				}
				else
				{
					if (Keysharp.Runtime.Script.IfTest(Keysharp.Runtime.Script.Invoke(instr, "Call", A_Clipboard, "://reddit.com")))
					{
						A_Clipboard = Keysharp.Runtime.Script.Invoke(strreplace, "Call", A_Clipboard, "://", "://old.");
					}
				}

				prefixes = new Keysharp.Builtins.Array("//click.alert.ally.com/CL0/", "https://www.ojrq.net/p/?return=", "ref=notif&");
				{
					Keysharp.Runtime.Loops.Push();
					object[] KS_e1_backup = new object[2]
					{
						each,
						prefix
					};
					var KS_e1 = Keysharp.Runtime.Loops.MakeEnumerable(prefixes, Misc.MakeVarRef(() => each, (Val) => each = Val), Misc.MakeVarRef(() => prefix, (Val) => prefix = Val)).GetEnumerator();
					try
					{
						while (Keysharp.Runtime.Flow.IsTrueAndRunning(KS_e1.MoveNext()))
						{
							Keysharp.Runtime.Loops.Inc();
							if (Keysharp.Runtime.Script.IfTest(Keysharp.Runtime.Script.Invoke(instr, "Call", A_Clipboard, prefix)))
							{
								A_Clipboard = Keysharp.Runtime.Script.Invoke(strreplace, "Call", A_Clipboard, prefix);
								goto KS_e1_end;
							}

							KS_e1_next:
								;
						}
					}
					finally
					{
						(each, prefix) = (KS_e1_backup[0], KS_e1_backup[1]);
						Keysharp.Runtime.Loops.Pop();
					}

					KS_e1_end:
						;
				}

				if (Keysharp.Runtime.Script.IfTest(Keysharp.Runtime.Script.Invoke(instr, "Call", A_Clipboard, "instagram.com/_u/")))
				{
					A_Clipboard = Keysharp.Runtime.Script.Invoke(strreplace, "Call", A_Clipboard, "/_u");
				}

				if (Keysharp.Runtime.Script.IfTest(Keysharp.Runtime.Script.Invoke(instr, "Call", A_Clipboard, "bing.com")))
				{
					A_Clipboard = Keysharp.Runtime.Script.Invoke(regexreplace, "Call", A_Clipboard, "(form=).*(&q=)", "q=");
				}
				else
				{
					if (Keysharp.Runtime.Script.IfTest(Keysharp.Runtime.Script.LogicalNot(((Keysharp.Runtime.Script.IfTest((Keysharp.Runtime.Script.IfTest(Keysharp.Runtime.Script.Invoke(instr, "Call", A_Clipboard, "indeed.com")) || Keysharp.Runtime.Script.IfTest(Keysharp.Runtime.Script.Invoke(instr, "Call", A_Clipboard, "ebay"))).ParseObject()) || Keysharp.Runtime.Script.IfTest(Keysharp.Runtime.Script.Invoke(instr, "Call", A_Clipboard, "&uu="))).ParseObject()))))
					{
						A_Clipboard = Keysharp.Runtime.Script.Invoke(regexreplace, "Call", A_Clipboard, ".*?(u=|q=|url=)");
					}
				}

				if (Keysharp.Runtime.Script.IfTest(Keysharp.Runtime.Script.Invoke(instr, "Call", A_Clipboard, "%")))
				{
					A_Clipboard = Keysharp.Runtime.Script.Invoke(encodedecodeuri, "Call", A_Clipboard, false);
				}

				if (Keysharp.Runtime.Script.IfTest(Keysharp.Runtime.Script.Invoke(instr, "Call", A_Clipboard, "?")))
				{
					whitelist = new Keysharp.Builtins.Array("?post_id=", "musicnotes.com", "humblebundle.com/?key=", "soundgym.co", "walgreens.com", "boardgamearena.com", "indeed", "youtube", "abcnews.go", "piped.video", "play.google.com", "linkedin.com/jobs/search/?currentJobId", "bing.com", "?profile_id=", "profile.php?id=", "?fbid=", " ?obId=", "cloudskillsboost.google", "?notif_id=", "multi_permalinks", "?_nkw=", "node=", "rlkey=", "schafffuneralhome.com", "churchandchapel.com", "bvfh", "google.com/calendar/embed", "beckerritter.com", "?story_fbid=", "productName=CONSUMER_DEBIT_CARD", "finviz.com", ".amazon.com/your-orders/order-details?orderID=", "/referral?referralCode=", ".facebook.com/watch/live/", "amazon.com/gp/css/summary/print.html?orderID=", ".facebook.com/watch/?v=", ".redcrossblood.org/give.html/drive-results?");
					{
						Keysharp.Runtime.Loops.Push();
						object[] KS_e1_backup = new object[1]
						{
							exception
						};
						var KS_e1 = Keysharp.Runtime.Loops.MakeEnumerable(whitelist, Misc.MakeVarRef(() => exception, (Val) => exception = Val)).GetEnumerator();
						try
						{
							while (Keysharp.Runtime.Flow.IsTrueAndRunning(KS_e1.MoveNext()))
							{
								Keysharp.Runtime.Loops.Inc();
								if (Keysharp.Runtime.Script.IfTest(Keysharp.Runtime.Script.Invoke(instr, "Call", A_Clipboard, exception)))
								{
									if (Keysharp.Runtime.Script.IfTest(((Keysharp.Runtime.Script.IfTest((Keysharp.Runtime.Script.IfTest(Keysharp.Runtime.Script.Invoke(instr, "Call", A_Clipboard, "similar-jobs?currentJobId=")) || Keysharp.Runtime.Script.IfTest(Keysharp.Runtime.Script.LogicalNot(Keysharp.Runtime.Script.Invoke(instr, "Call", A_Clipboard, "?si=")))).ParseObject()) || Keysharp.Runtime.Script.IfTest(Keysharp.Runtime.Script.LogicalNot(Keysharp.Runtime.Script.Invoke(instr, "Call", A_Clipboard, ".youtube.com/channel/")))).ParseObject())))
									{
										keep_question_marks = 1L;
									}

									global_exception = exception;
									goto KS_e1_end;
								}

								KS_e1_next:
									;
							}
						}
						finally
						{
							exception = KS_e1_backup[0];
							Keysharp.Runtime.Loops.Pop();
						}

						KS_e1_end:
							;
					}

					if (Keysharp.Runtime.Script.IfTest((Keysharp.Runtime.Script.IfTest(Keysharp.Runtime.Script.Invoke(instr, "Call", A_Clipboard, "node=")) || Keysharp.Runtime.Script.IfTest(Keysharp.Runtime.Script.Invoke(instr, "Call", A_Clipboard, "Namecheap"))).ParseObject()))
					{
						Keysharp.Runtime.Script.InvokeOrNull(traytip, "Call", "If it's always followed by \"&ref_\", adjust the URL purger to delete that and everything else", "Check for possible Amazon node or Namecheap package link!");
						A_Clipboard = Keysharp.Runtime.Script.GetIndex(Keysharp.Runtime.Script.Invoke(strsplit, "Call", A_Clipboard, "&utm"), 1L);
					}
					else
					{
						if (Keysharp.Runtime.Script.IfTest((Keysharp.Runtime.Script.ValueEquality(keep_question_marks, ""))))
						{
							if (Keysharp.Runtime.Script.IfTest(Keysharp.Runtime.Script.Invoke(regexmatch, "Call", A_Clipboard, "story_fbid=.+&id=")))
							{
								Keysharp.Runtime.Script.InvokeOrNull(regexreplace, "Call", A_Clipboard, "^([^&]*&[^&]*)&.*");
							}
							else
							{
								A_Clipboard = Keysharp.Runtime.Script.GetIndex(Keysharp.Runtime.Script.Invoke(strsplit, "Call", A_Clipboard, "?"), 1L);
							}

							Keysharp.Runtime.Script.InvokeOrNull(clipwait, "Call", 0L);
						}
						else
						{
							if (Keysharp.Runtime.Script.IfTest((Keysharp.Runtime.Script.ValueInequality(global_exception, ""))))
							{
								Keysharp.Runtime.Script.InvokeOrNull(traytip, "Call", "Keeping \"?\" and clearing everything else.", Keysharp.Runtime.Script.Concat("Whitelisted domain detected: ", global_exception));
							}
						}
					}

					keep_question_marks = "";
				}

				if (Keysharp.Runtime.Script.IfTest(Keysharp.Runtime.Script.Invoke(instr, "Call", A_Clipboard, "http")))
				{
					A_Clipboard = Keysharp.Runtime.Script.Invoke(regexreplace, "Call", A_Clipboard, "(from=).*(&jk=)", "jk=");
					A_Clipboard = Keysharp.Runtime.Script.Invoke(regexreplace, "Call", A_Clipboard, "&feature=shared");
					A_Clipboard = Keysharp.Runtime.Script.Invoke(regexreplace, "Call", A_Clipboard, "('?si='|&|/ref=)(.*)");
					if (Keysharp.Runtime.Script.IfTest(Keysharp.Runtime.Script.LogicalNot(Keysharp.Runtime.Script.Invoke(instr, "Call", A_Clipboard, "send.canine.tools"))))
					{
						A_Clipboard = Keysharp.Runtime.Script.Invoke(regexreplace, "Call", A_Clipboard, "(#)(.*)");
					}
				}

				return "";
			}

			public static object KS_Hotkey_1(object thishotkey)
			{
				Keysharp.Runtime.Script.InvokeOrNull(tooltip, "Call", "active");
				Keysharp.Runtime.Script.InvokeOrNull(purgeclipboardurl, "Call");
				Keysharp.Runtime.Script.InvokeOrNull(send, "Call", "^v");
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
			Keysharp.Runtime.Keyboard.HotkeyDefinition.AddHotkey(Keysharp.Builtins.Functions.Func((System.Delegate)__Main.KS_Hotkey_1), 0U, "^+v");
			Keysharp.Builtins.Flow.Persistent();
			Keysharp.Runtime.Keyboard.HotkeyDefinition.ManifestAllHotkeysHotstringsHooks();
			MainScript.CurrentModuleType = typeof(Program.__Main);
			__Main.AutoExecSection();
			MainScript.CurrentModuleType = null;
			return "";
		}
	}
}