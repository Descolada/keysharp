using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Assert = NUnit.Framework.Legacy.ClassicAssert;
using Keysharp.Core.Common.Invoke;
using Keysharp.Core.Common.Keyboard;
using Keysharp.Core.Common.Threading;
using Keysharp.Core.Common.Window;

namespace Keysharp.Tests
{
	[TestFixture, NonParallelizable]
	public class RealThreadIntegrationTests : TestRunner
	{
		private sealed class CallbackProbe
		{
			private readonly ConcurrentDictionary<string, ManualResetEventSlim> events = new();
			internal readonly ConcurrentDictionary<string, bool> HasSchedulerContext = new();
			internal readonly ConcurrentDictionary<string, int> ThreadIds = new();

			internal object Record(string name)
			{
				ThreadIds[name] = Environment.CurrentManagedThreadId;
				HasSchedulerContext[name] = SynchronizationContext.Current is ScriptEventSynchronizationContext;
				events.GetOrAdd(name, _ => new ManualResetEventSlim()).Set();
				return 0L;
			}

			internal bool WaitFor(string name, int timeout = 2000)
				=> WaitWithUiPump(() => events.GetOrAdd(name, _ => new ManualResetEventSlim()).IsSet, timeout);
		}

		private sealed class WorkerRegistrations
		{
			internal DelegateHolder CallbackHolder;
			internal KeysharpForm Form;
			internal Gui Gui;
			internal HotkeyDefinition Hotkey;
			internal HotkeyVariant HotkeyVariant;
			internal HotstringDefinition Hotstring;
			internal int MessageId;
			internal int WorkerThreadId;
			internal bool WorkerHasSchedulerContext;
			internal IFuncObj TimerFunc;
			internal TimerWithTag Timer;
		}

		private static Ks.RealThread StartWorker(Action body)
			=> (Ks.RealThread)Ks.RealThread.Call(null, new FuncObj((Func<object>)(() =>
			{
				body();
				return 0L;
			})));

		private static Message CreateMessage(int msgId)
		{
#if WINDOWS
			return Message.Create(IntPtr.Zero, msgId, IntPtr.Zero, IntPtr.Zero);
#else
			return new Message
			{
				HWnd = 0,
				Msg = msgId,
				WParam = 0,
				LParam = 0,
				Result = 0
			};
#endif
		}

		private static Error AssertScriptError(TestDelegate action)
		{
			var ex = Assert.Throws<KeysharpException>(action);
			return ex.UserError;
		}

		private static bool WaitWithUiPump(Func<bool> predicate, int timeout = 2000)
		{
			var deadline = Environment.TickCount64 + timeout;
			var script = Script.TheScript;

			while (!predicate())
			{
				if (Environment.TickCount64 >= deadline)
					return false;

				try
				{
#if WINDOWS
					Application.DoEvents();
#else
					Eto.Forms.Application.Instance?.RunIteration();
#endif
					script.FlowData.QueueOverdueTimersIfNeeded(Environment.TickCount64);
					script.EventScheduler.PumpPendingEvents();
				}
				catch
				{
				}

				Thread.Sleep(1);
			}

			return true;
		}

		private static void AssertEventually(Func<bool> predicate, string message, int timeout = 2000)
			=> Assert.IsTrue(WaitWithUiPump(predicate, timeout), message);

		private static void ShutdownWorker(Script script, Ks.RealThread worker)
		{
			if (script != null)
				script.hasExited = true;

			if (worker == null)
				return;

			_ = SpinWait.SpinUntil(() => !worker.IsAlive, 2000);
		}

		[Test, Category("Threading")]
		public void WorkerOwnedRegistrations()
		{
			_ = s.EventScheduler;
#if !WINDOWS
			_ = Eto.Forms.Application.Instance ?? new Eto.Forms.Application();
#endif

			var probe = new CallbackProbe();
			var registrations = new WorkerRegistrations();
			var registered = new ManualResetEventSlim(false);
			Exception workerSetupException = null;
			var form = (KeysharpForm)RuntimeHelpers.GetUninitializedObject(typeof(KeysharpForm));
			var gui = (Gui)RuntimeHelpers.GetUninitializedObject(typeof(Gui));
			Ks.RealThread worker = null;

				try
				{
					form.closedHandlers = new();
				gui.form = form;
				s.GuiData.allGuiHwnds[1] = gui;
				registrations.Form = form;
				registrations.Gui = gui;

				worker = StartWorker(() =>
				{
					try
					{
						registrations.WorkerThreadId = Environment.CurrentManagedThreadId;
						registrations.WorkerHasSchedulerContext = SynchronizationContext.Current is ScriptEventSynchronizationContext;

						registrations.TimerFunc = new FuncObj((Func<object>)(() => probe.Record("timer")));
						_ = Flow.SetTimer(registrations.TimerFunc, 250L);
						registrations.Timer = s.FlowData.GetTimerRegistration(registrations.TimerFunc, s.EventScheduler)?.Timer;

						registrations.Hotkey = new HotkeyDefinition(1, new FuncObj((Func<object, object>)(_ => probe.Record("hotkey"))), 0, "F24", 0);
						s.HotkeyData.shk.Add(registrations.Hotkey);
						registrations.HotkeyVariant = registrations.Hotkey.firstVariant;

						registrations.Hotstring = new HotstringDefinition("::abc", "")
						{
							Name = "abc",
							funcObj = new FuncObj((Func<object, object>)(_ => probe.Record("hotstring"))),
							maxThreads = 1,
							priority = 0
						};
						s.HotstringManager.shs.Add(registrations.Hotstring);

						registrations.MessageId = 0x8017;
						_ = Flow.OnMessage(registrations.MessageId, new FuncObj((Func<object, object, object, object, object>)((wParam, lParam, msg, hwnd) =>
						{
							_ = probe.Record("message");
							return 1L;
						})));

							form.closedHandlers ??= new();
						form.closedHandlers.ModifyEventHandlers(new FuncObj((Func<object, object>)(_ => probe.Record("gui"))), 1);

						_ = Env.OnClipboardChange(new FuncObj((Func<object, object>)(_ => probe.Record("clipboard"))));
						registrations.CallbackHolder = (DelegateHolder)Dll.CallbackCreate(new FuncObj((Func<object>)(() => probe.Record("callbackcreate"))));
					}
					catch (Exception ex)
					{
						workerSetupException = ex;
						throw;
					}
					finally
					{
						registered.Set();
					}
				});

				Assert.IsTrue(WaitWithUiPump(() => registered.IsSet), "Worker did not finish registration setup.");
				if (workerSetupException != null)
					Assert.Fail(workerSetupException.ToString());
				Assert.IsTrue(worker.IsAlive, "Worker should stay alive while it owns persistent registrations.");
				Assert.AreEqual(registrations.WorkerThreadId, worker.Id);
				Assert.IsTrue(registrations.WorkerHasSchedulerContext);

				registrations.Timer.PushToMessageQueue();
				Assert.IsTrue(probe.WaitFor("timer"));

				registrations.Hotkey.PerformInNewThreadMadeByCallerAsync(registrations.HotkeyVariant, 0, 0);
				Assert.IsTrue(probe.WaitFor("hotkey"));

				_ = registrations.Hotstring.PerformInNewThreadMadeByCaller(0, " ");
				Assert.IsTrue(probe.WaitFor("hotstring"));

				var filter = new MessageFilter(s);
				var msg = CreateMessage(registrations.MessageId);
				Assert.IsTrue(filter.CallEventHandlers(ref msg));
				Assert.IsTrue(probe.WaitFor("message"));

				_ = form.closedHandlers.InvokeEventHandlers("close");
				Assert.IsTrue(probe.WaitFor("gui"));

				_ = s.ClipFunctions.InvokeEventHandlers(1L);
				Assert.IsTrue(probe.WaitFor("clipboard"));

				Assert.AreEqual(0L, Dll.DllCall((long)registrations.CallbackHolder.Ptr));
				Assert.IsTrue(probe.WaitFor("callbackcreate"));

				_ = worker.Post(new FuncObj((Func<object>)(() => probe.Record("post"))));
				Assert.IsTrue(probe.WaitFor("post"));

				foreach (var name in new[] { "timer", "hotkey", "hotstring", "message", "gui", "clipboard", "callbackcreate", "post" })
				{
					Assert.AreEqual(registrations.WorkerThreadId, probe.ThreadIds[name], $"{name} callback did not run on the owning worker.");
					Assert.IsTrue(probe.HasSchedulerContext[name], $"{name} callback did not observe the worker synchronization context.");
				}
			}
			finally
			{
				ShutdownWorker(s, worker);
			}

			AssertEventually(() => !worker.IsAlive, "Worker should be fully stopped after shutdown.");
			AssertEventually(() => s.FlowData.GetTimerRegistration(registrations.Timer) == null, "Worker-owned timer was not removed.");
			AssertEventually(() => registrations.HotkeyVariant.ownerScheduler == null, "Worker-owned hotkey scheduler affinity was not cleared.");
			AssertEventually(() => !registrations.HotkeyVariant.enabled, "Worker-owned hotkey variant was not disabled.");
			AssertEventually(() => registrations.Hotstring.ownerScheduler == null, "Worker-owned hotstring scheduler affinity was not cleared.");
			AssertEventually(() => (registrations.Hotstring.suspended & HotstringDefinition.HS_TURNED_OFF) != 0, "Worker-owned hotstring was not turned off.");
			AssertEventually(() => !s.GuiData.onMessageHandlers.ContainsKey(registrations.MessageId), "Worker-owned OnMessage registration was not removed.");
			AssertEventually(() => form.closedHandlers.Count == 0, "Worker-owned GUI handlers were not removed.");
			AssertEventually(() => s.ClipFunctions.Count == 0, "Worker-owned clipboard handlers were not removed.");
			AssertEventually(() => registrations.CallbackHolder.Ptr == 0L, "Worker-owned callback pointer was not freed.");
		}

		[Test, Category("Threading")]
		public void SendPumpsMainScheduler()
		{
			_ = s.EventScheduler;

			var ready = new ManualResetEventSlim(false);
			var uiRan = new ManualResetEventSlim(false);
			Ks.RealThread worker = null;

			try
			{
				worker = StartWorker(() =>
				{
					_ = Env.OnClipboardChange(new FuncObj((Func<object, object>)(_ => 0L)));
					ready.Set();
				});

				Assert.IsTrue(WaitWithUiPump(() => ready.IsSet), "Worker did not become ready.");
				Assert.IsTrue(worker.IsAlive, "Worker must still be alive before Send.");

				var result = worker.Send(new FuncObj((Func<object>)(() =>
				{
					s.UIEventScheduler.EnqueueCallback(() => uiRan.Set(), ScriptEventQueue.Normal, false);
					Assert.IsTrue(uiRan.Wait(1000), "Worker never observed the callback queued back to the main scheduler.");
					return "ok";
				})));

				Assert.AreEqual("ok", result);
				Assert.IsTrue(uiRan.IsSet, "Main scheduler did not pump while waiting in Send.");
			}
			finally
			{
				ShutdownWorker(s, worker);
			}
		}

		[Test, Category("Threading")]
		public void PostSendAfterWorkerExit()
		{
			_ = s.EventScheduler;
			var worker = StartWorker(() => { });

			AssertEventually(() => !worker.IsAlive, "Worker with no persistent registrations should exit on its own.");

			var postError = AssertScriptError(() => worker.Post(new FuncObj((Func<object>)(() => 0L))));
			var sendError = AssertScriptError(() => worker.Send(new FuncObj((Func<object>)(() => 0L))));

			Assert.That(postError.Message, Does.Contain("Real thread is no longer alive."));
			Assert.That(sendError.Message, Does.Contain("Real thread is no longer alive."));
		}
	}
}
