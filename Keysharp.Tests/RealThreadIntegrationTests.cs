using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Reflection;
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
		private static int hookWinCriterionCalls;
		private static bool hookWinCriterionResult;

		private sealed class NamedCriterion(string name, Func<object> callback) : IFuncObj
		{
			public object Inst { get; set; }
			public bool IsBuiltIn => false;
			public bool IsValid => true;
			public string Name => name;
			public IFuncObj Bind(params object[] obj) => this;
			public object Call(params object[] obj) => callback();
			public object CallInst(object inst, params object[] obj) => callback();
			public bool IsByRef(object obj = null) => false;
			public bool IsOptional(object obj = null) => false;
		}

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
			internal HotkeyBinding HotkeyBinding;
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

		private static void EnsureUiScheduler()
		{
			_ = Script.TheScript.EventScheduler;
#if !WINDOWS
			_ = Eto.Forms.Application.Instance ?? new Eto.Forms.Application();
#endif
		}

		private void WithMatchingWorkerHotkey(HotkeyDefinition hotkey, IFuncObj workerCallback, Action<Ks.RealThread> assertions, string options = null)
		{
			var registered = new ManualResetEventSlim(false);
			Exception workerSetupException = null;
			Ks.RealThread worker = null;

			try
			{
				worker = StartWorker(() =>
				{
					try
					{
						_ = options == null ? Keyboard.Hotkey(hotkey.Name, workerCallback) : Keyboard.Hotkey(hotkey.Name, workerCallback, options);
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
				Assert.IsTrue(WaitWithUiPump(() => registered.IsSet), "Worker did not register the matching hotkey variant.");
				Assert.That(workerSetupException, Is.Null, workerSetupException?.ToString());
				assertions(worker);
			}
			finally
			{
				ShutdownWorker(s, worker);
			}
		}

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

		private static Error AssertScriptError(TestDelegate action) => Assert.Throws<KeysharpException>(action).UserError;

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
			EnsureUiScheduler();

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
						registrations.HotkeyBinding = registrations.HotkeyVariant.FindBinding(s.EventScheduler);

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
			AssertEventually(() => registrations.HotkeyBinding.OwnerScheduler == null, "Worker-owned hotkey scheduler affinity was not cleared.");
			AssertEventually(() => !registrations.HotkeyBinding.IsActive, "Worker-owned hotkey binding was not disabled.");
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
		public void ExactHotkeyVariantDispatchesAcrossSchedulers()
		{
			EnsureUiScheduler();

			var probe = new CallbackProbe();
			var hk = new HotkeyDefinition((uint)s.HotkeyData.shk.Count, new FuncObj((Func<object, object>)(_ => probe.Record("main"))), 0, "$a", 0);
			s.HotkeyData.shk.Add(hk);

			WithMatchingWorkerHotkey(hk, new FuncObj((Func<object, object>)(_ => probe.Record("worker"))), worker =>
			{
				Assert.AreEqual(1, s.HotkeyData.shk.Count, "Exact match should reuse the same hotkey definition.");
				Assert.AreEqual(2, hk.firstVariant.BindingCount, "Exact match should attach one callback per scheduler.");

				hk.PerformInNewThreadMadeByCallerAsync(hk.firstVariant, 0, 0);

				Assert.IsTrue(probe.WaitFor("main"));
				Assert.IsTrue(probe.WaitFor("worker"));
				Assert.AreEqual(worker.Id, probe.ThreadIds["worker"]);
				Assert.AreNotEqual(probe.ThreadIds["main"], probe.ThreadIds["worker"]);
			});
		}

		[Test, Category("Threading")]
		public void HookHotkeyDispatchSkipsCriterionReevaluation()
		{
			EnsureUiScheduler();

			var criterionCalls = 0;
			var callbackRan = new ManualResetEventSlim(false);
			var previousCriterion = s.Threads.CurrentThread.hotCriterion;

			try
			{
				s.Threads.CurrentThread.hotCriterion = new FuncObj((Func<object, object>)(_ =>
				{
					_ = Interlocked.Increment(ref criterionCalls);
					return 1L;
				}));

				var hk = new HotkeyDefinition((uint)s.HotkeyData.shk.Count, new FuncObj((Func<object, object>)(_ =>
				{
					callbackRan.Set();
					return 0L;
				})), 0, "$a", 0);
				s.HotkeyData.shk.Add(hk);

				Assert.IsTrue(s.HookThread.PostMessage(new KeysharpMsg
				{
					message = (uint)UserMessages.AHK_HOOK_HOTKEY,
					wParam = new nint(hk.id),
					lParam = 0,
					obj = new HookHotkeyMsg
					{
						variant = hk.firstVariant,
						criterionFoundHwnd = 1
					}
				}));

				Assert.IsTrue(WaitWithUiPump(() => callbackRan.IsSet), "Prequalified hook hotkey did not dispatch.");
				Assert.AreEqual(0, Volatile.Read(ref criterionCalls), "Hook hotkey dispatch should not re-evaluate its criterion on receipt.");
			}
			finally
			{
				s.Threads.CurrentThread.hotCriterion = previousCriterion;
			}
		}

		[Test, Category("Threading")]
		public void HookHotkeyWindowCriterionReevaluatesOnReceipt()
		{
			EnsureUiScheduler();

			var callbackRan = new ManualResetEventSlim(false);
			var previousCriterion = s.Threads.CurrentThread.hotCriterion;
			var previousCalls = Interlocked.Exchange(ref hookWinCriterionCalls, 0);
			hookWinCriterionResult = true;

			try
			{
				s.Threads.CurrentThread.hotCriterion = new NamedCriterion("HotIfWinActivePrivate", () =>
				{
					_ = Interlocked.Increment(ref hookWinCriterionCalls);
					return hookWinCriterionResult;
				});

				var hk = new HotkeyDefinition((uint)s.HotkeyData.shk.Count, new FuncObj((Func<object, object>)(_ =>
				{
					callbackRan.Set();
					return 0L;
				})), 0, "$c", 0);
				s.HotkeyData.shk.Add(hk);

				var buildMethod = s.HookThread.GetType().GetMethod("TryBuildHookHotkeyMessage", BindingFlags.Instance | BindingFlags.NonPublic);
				Assert.IsNotNull(buildMethod, "Hook hotkey message builder should exist.");
				var args = new object[] { hk.id, 0UL, null, null };
				Assert.IsTrue((bool)buildMethod.Invoke(s.HookThread, args), "Hook hotkey should qualify successfully.");
				Assert.AreEqual(1, Volatile.Read(ref hookWinCriterionCalls), "Window-style criterion should be evaluated once on the hook side.");

				var hookMsg = (HookHotkeyMsg)args[3];
				Assert.IsNull(hookMsg.variant, "Window-style criteria should be re-evaluated on receipt instead of dispatching the prequalified variant directly.");

				Assert.IsTrue(s.HookThread.PostMessage(new KeysharpMsg
				{
					message = (uint)UserMessages.AHK_HOOK_HOTKEY,
					wParam = new nint(hk.id),
					lParam = 0,
					obj = hookMsg
				}));

				Assert.IsTrue(WaitWithUiPump(() => Volatile.Read(ref hookWinCriterionCalls) == 2), "Window-style criterion was not re-evaluated on receipt.");
				Assert.IsTrue(WaitWithUiPump(() => callbackRan.IsSet, 5000), "Re-evaluated hook hotkey did not dispatch.");
				Assert.AreEqual(2, Volatile.Read(ref hookWinCriterionCalls), "Window-style criterion should be evaluated again when the message is received.");
			}
			finally
			{
				s.Threads.CurrentThread.hotCriterion = previousCriterion;
				_ = Interlocked.Exchange(ref hookWinCriterionCalls, previousCalls);
			}
		}

		[Test, Category("Threading")]
		public void HotIfHotkeyWithEnabledGlobalVariantCanStayRegistered()
		{
			EnsureUiScheduler();
			var previousCriterion = s.Threads.CurrentThread.hotCriterion;

			try
			{
				var hk = new HotkeyDefinition((uint)s.HotkeyData.shk.Count, new FuncObj((Func<object, object>)(_ => 0L)), 0, "b", 0);
				s.HotkeyData.shk.Add(hk);
				s.Threads.CurrentThread.hotCriterion = new FuncObj((Func<object, object>)(_ => 1L));
				_ = hk.AddVariant(new FuncObj((Func<object, object>)(_ => 0L)), 0);
				s.Threads.CurrentThread.hotCriterion = previousCriterion;

				_ = HotkeyDefinition.ManifestAllHotkeysHotstringsHooks();

				Assert.AreEqual(HotkeyTypeEnum.Normal, hk.type, "A hotkey with an enabled global variant should be allowed to stay on the non-hook WM_HOTKEY path.");
			}
			finally
			{
				s.Threads.CurrentThread.hotCriterion = previousCriterion;
			}
		}

		[Test, Category("Threading")]
		public void WorkerHotkeyBindingRunsAfterMainThreadEnds()
		{
			EnsureUiScheduler();

			var mainStarted = new ManualResetEventSlim(false);
			var releaseMain = new ManualResetEventSlim(false);
			var workerRan = new ManualResetEventSlim(false);
			var hk = new HotkeyDefinition((uint)s.HotkeyData.shk.Count, new FuncObj((Func<object, object>)(_ =>
			{
				mainStarted.Set();
				_ = releaseMain.Wait(2000);
				return 0L;
			})), 0, "$a", 0);
			s.HotkeyData.shk.Add(hk);

			WithMatchingWorkerHotkey(hk, new FuncObj((Func<object, object>)(_ =>
			{
				workerRan.Set();
				return 0L;
			})), _ =>
			{
				hk.PerformInNewThreadMadeByCallerAsync(hk.firstVariant, 0, 0);

				Assert.IsTrue(WaitWithUiPump(() => mainStarted.IsSet, 1000), "Main-thread hotkey callback never started.");
				Thread.Sleep(100);
				releaseMain.Set();

				Assert.IsTrue(WaitWithUiPump(() => workerRan.IsSet, 1000),
					"Worker-bound hotkey callback did not run after the main-thread callback completed.");
			});
		}

		[Test, Category("Threading")]
		public void WorkerHotkeyBindingProgressesWhileMainBindingBlocked()
		{
			EnsureUiScheduler();

			var mainStarted = new ManualResetEventSlim(false);
			var releaseMain = new ManualResetEventSlim(false);
			var workerCalls = 0;
			var hk = new HotkeyDefinition((uint)s.HotkeyData.shk.Count, new FuncObj((Func<object, object>)(_ =>
			{
				mainStarted.Set();
				_ = releaseMain.Wait(2000);
				return 0L;
			})), 0, "$a", 0);
			s.HotkeyData.shk.Add(hk);

			WithMatchingWorkerHotkey(hk, new FuncObj((Func<object, object>)(_ =>
			{
				_ = Interlocked.Increment(ref workerCalls);
				Thread.Sleep(50);
				return 0L;
			})), _ =>
			{
				hk.firstVariant.maxThreads = 2;
				hk.PerformInNewThreadMadeByCallerAsync(hk.firstVariant, 0, 0);
				Assert.IsTrue(WaitWithUiPump(() => mainStarted.IsSet, 1000), "Main-thread hotkey callback never started.");

				for (var i = 0; i < 3; i++)
					hk.PerformInNewThreadMadeByCallerAsync(hk.firstVariant, 0, 0);

				Assert.IsTrue(WaitWithUiPump(() => Volatile.Read(ref workerCalls) >= 2, 1000),
					"Worker-bound hotkey callback did not make forward progress while the main binding was still blocked.");

				releaseMain.Set();
			}, "T2");
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

		[Test, Category("Threading")]
		public void StopUnhooksKeyboardHotkeys()
		{
			EnsureUiScheduler();

			var hk = new HotkeyDefinition((uint)s.HotkeyData.shk.Count, new FuncObj((Func<object, object>)(_ => 0L)), 0, "$a", 0);
			s.HotkeyData.shk.Add(hk);
			_ = HotkeyDefinition.ManifestAllHotkeysHotstringsHooks();
			Assert.IsTrue(s.HookThread.HasKbdHook(), "Hook-backed hotkey should install the keyboard hook.");

			s.Dispose();

			AssertEventually(() => !s.HookThread.HasKbdHook(), "Script.Dispose() did not uninstall the keyboard hook.");
		}
	}
}
