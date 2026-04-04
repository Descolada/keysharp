

using Keysharp.Builtins;
#if WINDOWS
using Keysharp.Builtins.COM;
#endif

[assembly: InternalsVisibleTo("Keysharp.Tests")]
[assembly: InternalsVisibleTo("Keysharp.Benchmark")]
[assembly: InternalsVisibleTo("Keyview")]

namespace Keysharp.Runtime
{
	/// <summary>
	/// This is the main script object which contains all instance data needed for a script to run.
	/// A Script object is created twice: once for parsing, and another for running.
	/// The design is unusual because all instance data is contained here, then the object itself
	/// is assigned to a global static member of itself, script.
	/// The reason for this is that most of the user facing functions in Keysharp are static.
	/// However, just having them access static data presents a major problem:
	///     Static data is left around after multiple instances are created during parsing, running
	///     and between unit tests. As long as they all exist in the same process, each instance does
	///     not start clean and instead starts with unpredictable remnants of the previous instance.
	/// To remedy this problem, all data is instance data, and there is only one static member that all
	/// instance data is accessed through. This ensures a clean start every time we create a Script object.
	/// </summary>
	public partial class Script : IDisposable
	{
		internal static bool dpimodeset;//This should be done once per process, so it can be static.
#if WINDOWS
		private static int screenSystemEventsInitialized;
#endif
#if !WINDOWS
			private static Encoding enc1252 = Encoding.Default;
			private static bool etoAppConfigured;
#endif

		public SynchronizationContext MainContext;

		internal readonly uint NativeMainThreadID;
		internal readonly int ManagedMainThreadID;
		internal uint NativeMainThreadId => NativeMainThreadID;

		internal bool IsOnMainThread
		{
            get {
#if !WINDOWS
                var app = Application.Instance;
                if (app != null)
                    return app.IsUIThread;
#endif
			    return Environment.CurrentManagedThreadId == ManagedMainThreadID;
            }
		}

		public const string dotNetMajorVersion = "10";

		/// <summary>
		/// True when running under the NUnit/VSTest host process (<c>testhost</c>).
		/// This indicates a test-host runtime, not necessarily a user-requested headless mode.
		/// </summary>
		public static readonly bool IsTestHost = AppDomain.CurrentDomain.FriendlyName == "testhost";

		/// <summary>
		/// True when scripts should run without normal UI affordances.
		/// This is true when headless mode is forced, when no displays are available, or when both
		/// <see cref="NoMainWindow"/> and <see cref="NoTrayIcon"/> are enabled.
		/// </summary>
		public static bool IsHeadless => IsHeadlessForced()
			|| !HasAvailableDisplay()
			|| (TheScript?.NoMainWindow == true && TheScript?.NoTrayIcon == true);

		/// <summary>
		/// True when this host must not attempt Eto UI initialization.
		/// </summary>
		internal static bool IsUiInitializationBlocked =>
#if OSX
			IsTestHost;
#else
			false;
#endif

		private static bool HasAvailableDisplay()
		{

			try
			{
#if WINDOWS
				return System.Windows.Forms.Screen.AllScreens?.Length > 0;
#else
				// Before Eto is initialized, Screen queries may throw on some hosts.
				// Avoid incorrectly classifying normal desktop runs as headless.
					if (Eto.Forms.Application.Instance == null)
					return DefaultWithoutUILoop();

				return Eto.Forms.Screen.Screens?.Any() == true;
#endif
			}
			catch
			{
				return DefaultWithoutUILoop();
			}

			static bool DefaultWithoutUILoop()
			{
				#if WINDOWS
				return false;
#elif LINUX
				return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
					|| !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
#else
				return !IsTestHost;
#endif
			}
		}

		private static bool IsHeadlessForced()
		{
			var value = Environment.GetEnvironmentVariable("KEYSHARP_FORCE_HEADLESS");
			return Options.OnOff(value) ?? false;
		}
		internal MessageFilter msgFilter;
		internal volatile bool hasExited = false;
		public bool ForceKeybdHook;
		public string[] ScriptArgs = [];
		public string[] KeysharpArgs = [];
		public uint MaxThreadsTotal = 12u;
		public bool NoMainWindow = false;
		public bool NoTrayIcon = false;
		public bool ValidateThenExit;
		public bool WinActivateForce = false;
		//Some unit tests use try..catch in non-script code, which causes ErrorOccurred to display the error dialog.
		//This allows to suppress it, but only inside ErrorOccurred (not in TryCatch etc).
		public bool SuppressErrorOccurredDialog = IsTestHost;
		//This allows to suppress all error processing
		public uint SuppressErrorOccurred = 0;
		internal const double DefaultErrorDouble = double.NaN;
		internal const int DefaultErrorInt = int.MinValue;
		internal const long DefaultErrorLong = long.MinValue;
		internal const string DefaultNewLine = "\n";
		internal const string DefaultObject = "";
		internal const string DefaultErrorObject = "";
		internal const string DefaultErrorString = "";
		internal const int INTERVAL_UNSPECIFIED = int.MinValue + 303;
		internal const int maxEmergencyThreads = 10;
		internal const int maxThreadsLimit = 0xFF;
		internal const int SLEEP_INTERVAL = 10;
		internal const int SLEEP_INTERVAL_HALF = SLEEP_INTERVAL / 2;
		internal CallbackRegistry<CallbackRegistration> ClipFunctions = new();
		internal List<IFuncObj> hotCriterions = [];
		internal List<IFuncObj> hotExprs = [];
		internal InputType input;
		internal int inputBeforeHotkeysCount;
		internal DateTime inputTimeoutAt = DateTime.UtcNow;
		internal bool inputTimerExists;
		internal MainWindow mainWindow;
		internal Gui mainWindowGui;
		internal MenuType menuIsVisible = MenuType.None;
		internal PlatformManagerBase mgr;
		internal int nMessageBoxes;
		internal CallbackRegistry<CallbackRegistration> onErrorHandlers = new();
		internal CallbackRegistry<CallbackRegistration> onExitHandlers = new();
		private Icon _normalIcon = null;
		public Icon normalIcon
		{
			get
			{
#if WINDOWS
				if (_normalIcon == null)
					_normalIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
#endif
				return _normalIcon ??= ImageHelper.IconFromByteArray(Keysharp.Internals.Properties.Resources.Keysharp_ico);
			}
		}
		private Icon _pausedIcon;
		internal Icon pausedIcon => _pausedIcon ??= ImageHelper.IconFromByteArray(Keysharp.Internals.Properties.Resources.Keysharp_p_ico);
		private Icon _suspendedIcon;
		internal Icon suspendedIcon => _suspendedIcon ??= ImageHelper.IconFromByteArray(Keysharp.Internals.Properties.Resources.Keysharp_s_ico);
		internal bool persistent;
		internal nint playbackHook = 0;
		internal DateTime priorHotkeyStartTime = DateTime.UtcNow;
		public string scriptName = "";
		internal string thisHotkeyName, priorHotkeyName;
		internal DateTime thisHotkeyStartTime;
		internal ThreadLocal<Threads> threads;
		internal readonly ThreadLocal<ModuleData> moduleData = new ();
		internal DateTime timeLastInputKeyboard;
		internal DateTime timeLastInputMouse;
		internal DateTime timeLastInputPhysical = DateTime.UtcNow;
		internal int totalExistingThreads;//Even though the thread stacks are on a per-real-thread basis, we keep a global count of threads. This may need to change in the future.
		internal int uninterruptibleTime = 17;
		private static int instanceCount;
		private AccessorData accessorData;
#if WINDOWS
		private ComMethodData comMethodData;
#endif
		private ControlProvider controlProvider;
		private DllData dllData;
		private DriveTypeMapper driveTypeMapper;
		private ExecutableMemoryPoolManager exeMemoryPoolManager;
		private FlowData flowData;
		private FunctionData functionData;
		private GuiData guiData;
		private HotkeyData hotkeyData;
		private HotstringManager hotstringManager;
		private ImageListData imageListData;
		private InputData inputData;
		private bool isReadyToExecute;
		private JoystickData joystickData;
		private KeyboardData keyboardData;
		private KeyboardUtilsData keyboardUtilsData;
		private LoopData loopData;
		private nint mainWindowHandle;
		private PlatformProvider platformProvider;
		private PermissionProvider permissionProvider;
		private ProcessesData processesData;
		private RegExData regExData;
		private StringsData stringsData;
		private ToolTipData toolTipData;
		private WindowProvider windowProvider;
		private int disposeStarted;

		public static Keysharp.Runtime.Script TheScript { get; private set; }
		public Type ProgramType;
		public string ProgramNamespace = Keywords.MainNamespaceName;
		internal HotstringManager HotstringManager => hotstringManager ?? (hotstringManager = new ());
		public Threads Threads => threads.Value;
		public Variables Vars { get; private set; }
		internal AccessorData AccessorData => accessorData ?? (accessorData = new ());
#if WINDOWS
		internal ComMethodData ComMethodData => comMethodData ?? (comMethodData = new ());
#endif
		internal ControlProvider ControlProvider => controlProvider ?? (controlProvider = new ());
		internal DllData DllData => dllData ?? (dllData = new ());
		internal DriveTypeMapper DriveTypeMapper => driveTypeMapper ?? (driveTypeMapper = new ());
		internal ExecutableMemoryPoolManager ExecutableMemoryPoolManager => exeMemoryPoolManager ?? (exeMemoryPoolManager = new ());
		internal FlowData FlowData => flowData ?? (flowData = new ());
		internal FunctionData FunctionData => functionData ?? (functionData = new ());
		internal GuiData GuiData => guiData ?? (guiData = new ());
		internal HookThread HookThread { get; private set; }
		internal HotkeyData HotkeyData => hotkeyData ?? (hotkeyData = new ());

		internal long HwndLastUsed
		{
			get => Threads.CurrentThread.hwndLastUsed;
			set => Threads.CurrentThread.hwndLastUsed = value;
		}

		internal ImageListData ImageListData => imageListData ?? (imageListData = new ());
		internal InputData InputData => inputData ?? (inputData = new ());
		internal bool IsMainWindowClosing => mainWindow == null || mainWindow.IsClosing;
		internal bool IsReadyToExecute => isReadyToExecute;
		internal JoystickData JoystickData => joystickData ?? (joystickData = new ());
		internal KeyboardData KeyboardData => keyboardData ?? (keyboardData = new ());
		internal KeyboardUtilsData KeyboardUtilsData => keyboardUtilsData ?? (keyboardUtilsData = new ());
		internal LoopData LoopData => loopData ?? (loopData = new ());

		internal nint MainWindowHandle
		{
			get
			{
#if WINDOWS
				if (mainWindow == null)
					InitializeMainWindow(string.Empty, persistent, false, initializeUiChrome: false);
#else
				if (mainWindow == null)
					return 0;
#endif

				if (mainWindowHandle == 0)
				{
					if (mainWindow.InvokeRequired)
						_ = mainWindow.Invoke(() => mainWindowHandle = mainWindow.Handle);
					else
						mainWindowHandle = mainWindow.Handle;
				}

				return mainWindowHandle;
			}
		}

		internal PlatformProvider PlatformProvider => platformProvider ?? (platformProvider = new ());
		internal PermissionProvider PermissionProvider => permissionProvider ?? (permissionProvider = new ());
		internal IPermissionManager Permissions => PermissionProvider.Manager;
		internal ProcessesData ProcessesData => processesData ?? (processesData = new ());
		internal Reflections Reflections { get; private set; }
		internal ReflectionsData ReflectionsData { get; } = new ();//Don't lazy initialize, it's always needed in every Script.TheScript.
		internal RegExData RegExData => regExData ?? (regExData = new ());
		internal StringsData StringsData => stringsData ?? (stringsData = new ());
		internal ToolTipData ToolTipData => toolTipData ?? (toolTipData = new ());
		internal WindowProvider WindowProvider => windowProvider ?? (windowProvider = new ());

#if OSX
		internal string ldLibraryPath = Environment.GetEnvironmentVariable("DYLD_LIBRARY_PATH") ?? "";
#elif LINUX
		internal string ldLibraryPath = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") ?? "";
#endif

		static Script()
		{
			// Needed for string and file encodings such as Windows-1252
			Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
#if WINDOWS
			Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

			Application.ThreadException += (s, e) =>
			{
				if (e.Exception is Keysharp.Builtins.Flow.UserRequestedExitException) return; // silence during shutdown
				System.Diagnostics.Debug.Write("ThreadException caught: " + e.Exception);
			};
#endif

			AppDomain.CurrentDomain.UnhandledException += (s, e) =>
			{
				var ex = e.ExceptionObject as Exception;
				if (Script.TheScript?.hasExited == true || ex is Keysharp.Builtins.Flow.UserRequestedExitException) return; // silence during shutdown
				System.Diagnostics.Debug.WriteLine("Exception caught in current domain: " + ex);
			};

			WindowX.SetProcessDPIAware();
			CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
			CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
#if !WINDOWS
			Eto.Platform.AllowReinitialize = true;

			Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);//For some reason, linux needs this for rich text to work.
			enc1252 = Encoding.GetEncoding(1252);
#endif
			SetInitialFloatFormat();//This must be done intially and not just when A_FormatFloat is referenced for the first time.

#if WINDOWS
			// Temporary patch to override the GdiPlus initialization settings used by WinForms
			// (or more specifically System.Drawing). If StartupParameters is not 4 then external
			// codecs such as webp cannot be used.
			EnsureGdiPlus();
#endif

#if LINUX
			// Keysharp uses X11 from multiple threads (GTK UI + hook/window code), so Xlib
			// must be put into threaded mode before any display connection is opened.
			_ = Keysharp.Internals.Window.Linux.X11.Xlib.XInitThreads();

			// Keysharp's Linux automation path is still X11-based, so force Gtk onto X11/Xwayland
			// when an X display is available before Eto initializes its platform backend.
			if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
				Environment.SetEnvironmentVariable("GDK_BACKEND", "x11");
#endif
		}

#if WINDOWS
		static nint _gdiToken;
		public static void EnsureGdiPlus()
		{
			if (_gdiToken != 0) return;
			var si = new WindowsAPI.GdiplusStartupInputEx
			{
				GdiplusVersion = 2,
				SuppressBackgroundThread = false,
				SuppressExternalCodecs = false,
				StartupParameters = 4
			};
			int s = WindowsAPI.GdiplusStartup(out _gdiToken, ref si, IntPtr.Zero);
			if (s != 0) throw new ExternalException($"GdiplusStartup failed: {s}");
		}
#endif

		public Script(Type program = null, string hookMutexName = null)
		{
			Script.TheScript = this;//Everywhere in the script will reference this.

			NativeMainThreadID = CurrentThreadId();
			ManagedMainThreadID = Environment.CurrentManagedThreadId;

			ProgramType = program ?? GetCallingType();
			ProgramNamespace = ProgramType.Namespace;

			timeLastInputPhysical = DateTime.UtcNow;
			timeLastInputKeyboard = timeLastInputPhysical;
			timeLastInputMouse = timeLastInputPhysical;

			//Init the API classes, passing in this which will be used to access their respective data objects.
			Reflections = new Reflections();
			Vars = new Variables();
			Vars.InitClasses();

			threads = new(() => new());

			Script.TheScript.Threads.EnsureCurrentThreadVariables();

			_ = EventScheduler;

			// Request the required privacy permissions at startup so failures are explicit and early.
			var pm = Permissions;
			_ = pm.RequestAccessibilityAutomation(operation: "accessibility automation");
			_ = pm.RequestInputMonitoring(operation: "keyboard/mouse monitoring");
			_ = pm.RequestInputInjection(operation: "keyboard/mouse sending");

#if WINDOWS
			msgFilter = new MessageFilter(this);
			Application.AddMessageFilter(msgFilter);
			InitializeScreenSystemEventsOnNeutralContext();
#else
			msgFilter = new MessageFilter(this);

			if (!IsOnMainThread)
				PostToUIThread(msgFilter.Attach);
			else
				msgFilter.Attach();
#endif
			HookThread = CreateHookThread();
			if (hookMutexName != null && hookMutexName != "") Keysharp.Internals.Input.Hooks.HookThread.MutexName = hookMutexName;
			//Init the data objects that the API classes will use.
			SetInitialFloatFormat();//This must be done intially and not just when A_FormatFloat is referenced for the first time.
		}

#if WINDOWS
		private static void InitializeScreenSystemEventsOnNeutralContext()
		{
			if (Interlocked.Exchange(ref screenSystemEventsInitialized, 1) != 0)
				return;

			var oldContext = System.ComponentModel.AsyncOperationManager.SynchronizationContext;

			try
			{
				// Screen.WorkingArea lazily subscribes to SystemEvents.UserPreferenceChanged and SystemEvents
				// captures the current SynchronizationContext for future callbacks. Use a neutral context once
				// per process so that callback is not bound to a per-script WinForms UI thread which may be
				// torn down between tests/scripts, causing Application.SetColorMode() to block forever.
				System.ComponentModel.AsyncOperationManager.SynchronizationContext = new SynchronizationContext();
				_ = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea;
			}
			catch
			{
			}
			finally
			{
				System.ComponentModel.AsyncOperationManager.SynchronizationContext = oldContext;
			}
		}
#endif

		~Script()
		{
			Dispose(false);
		}

		[MethodImpl(MethodImplOptions.NoInlining)]  // prevent inlining from collapsing frames
		public static Type GetCallingType(int skipFrames = 2)
		{
			var st = new StackTrace();
			// skip the requested frames (defaults: 0=this, 1=GetCallingType, 2=your caller)
			var frame = st.GetFrame(skipFrames);
			if (frame == null) return null;

			var method = frame.GetMethod();
			return method.DeclaringType;
		}

		internal long GetPeekFrequency()
		{
			var tv = threads != null && threads.IsValueCreated ? threads.Value.CurrentThread : null;
			return tv != null ? tv.configData.peekFrequency : ThreadVariables.DefaultPeekFrequency;
		}

		internal bool IsCurrentThreadPreemptiveCheckDue()
		{
			var tv = Threads.CurrentThread;
			var freq = tv.configData.peekFrequency;

			if (freq < 0L)
				return false;

			var nowTick = Environment.TickCount;
			var threshold = freq > int.MaxValue ? int.MaxValue : (int)freq;
			return unchecked((uint)(nowTick - tv.lastPeekTick)) > unchecked((uint)threshold);
		}

		internal void RecordMessageCheck() => Threads.CurrentThread.lastPeekTick = Environment.TickCount;

		/// <summary>
		/// Will be a generated call within Main which calls into this class to add DLLs.
		/// </summary>
		/// <param name="p"></param>
		/// <param name="s"></param>
		public void LoadDll(string library, bool throwOnFailure = true)
		{
			if (library.Length == 0)
			{
				if (!SetDllDirectory(null))//An empty #DllLoad restores the default search order.
					if (throwOnFailure)
					{
						_ = Errors.ErrorOccurred("PlatformManager.SetDllDirectory(null) failed.", null, Keyword_ExitApp);
						return;
					}
			}
			else if (Directory.Exists(library))
			{
				if (!SetDllDirectory(library))
					if (throwOnFailure)
					{
						_ = Errors.ErrorOccurred($"PlatformManager.SetDllDirectory({library}) failed.", null, Keyword_ExitApp);
						return;
					}
			}
			else
			{
				var libraryName = library;
				if (libraryName.Length != 0 && !Path.HasExtension(libraryName)
#if !WINDOWS
					&& !File.Exists(libraryName)
#endif
				)
					libraryName += Keywords.LibraryExtension;


				var hmodule = LoadLibrary(libraryName);

				if (hmodule != 0)
				{
#if WINDOWS
					// "Pin" the dll so that the script cannot unload it with FreeLibrary.
					// This is done to avoid undefined behavior when DllCall optimizations
					// resolves a proc address in a dll loaded by this directive.
					_ = WindowsAPI.GetModuleHandleEx(WindowsAPI.GET_MODULE_HANDLE_EX_FLAG_PIN, libraryName, out hmodule);  // MSDN regarding hmodule: "If the function fails, this parameter is NULL."
#endif
				}
				else if (throwOnFailure)
				{
					_ = Errors.ErrorOccurred($"Failed to load DLL {libraryName}.", null, Keyword_ExitApp);
					return;
				}
			}
		}

		public static bool HandleSingleInstance(string title, eScriptInstance inst)
		{
			if (title.Length == 0 || title == "*")//Happens when running in Keyview.
				return false;

			if (Env.FindCommandLineArg("force") != null || Env.FindCommandLineArg("f") != null)
				inst = eScriptInstance.Off;

			if (Env.FindCommandLineArg("restart") != null || Env.FindCommandLineArg("r") != null)
				inst = eScriptInstance.Force;

			var exit = false;
			var oldDetect = WindowX.DetectHiddenWindows(true);
			var oldMatchMode = WindowX.SetTitleMatchMode(3);//Require exact match.

			switch (inst)
			{
				case eScriptInstance.Force:
				{
					_ = WindowX.WinClose(title, "", 2);
				}
				break;

				case eScriptInstance.Ignore:
					if (WindowX.WinExist(title) != 0)
						exit = true;

					break;

				case eScriptInstance.Off:
					break;

				case eScriptInstance.Prompt:
				default:
					var hwnd = WindowX.WinExist(title);

					if (hwnd != 0)
					{
						if (Dialogs.MsgBox("Do you want to close the existing instance before running this one?\nYes to exit that instance, No to exit this instance.", "", "YesNo") == "Yes")
							_ = WindowX.WinClose(hwnd, "", 2);
						else
							exit = true;
					}

					break;
			}

			_ = WindowX.SetTitleMatchMode(oldMatchMode);
			_ = WindowX.DetectHiddenWindows(oldDetect);
			return exit;
		}

		public void ExitIfNotPersistent(Keysharp.Builtins.Flow.ExitReasons exitReason = Keysharp.Builtins.Flow.ExitReasons.Exit)
		{
			//Must use BeginInvoke() because this might be called from _ks_UserMainCode(),
			//so it needs to run after that thread has exited.
			if (!IsMainWindowClosing && totalExistingThreads == 0)
			{
				PostToUIThread(() =>
				{
					if (!IsMainWindowClosing && !AnyPersistent())
						_ = Keysharp.Internals.Flow.ExitAppInternal(exitReason, Environment.ExitCode, false);
				});
			}
		}

		public string GetPublicStaticPropertyNames()
		{
			var l1 = ReflectionsData.flatPublicStaticMethods.Keys.ToList();
			l1.AddRange(ReflectionsData.flatPublicStaticProperties.Keys);
			var hs = new HashSet<string>(l1);
			return string.Join(' ', hs);
		}

		public ModuleData ModuleData
		{
			get
			{
				if (moduleData.Value == null)
				{
					var defaultType = Vars?.DefaultModuleType;
					if (defaultType != null)
						moduleData.Value = ModuleData.GetOrCreate(defaultType);
				}

				return moduleData.Value;
			}
		}

		public Type CurrentModuleType
		{
			get => ModuleData?.ModuleType;
			set
			{
				if (value == null)
				{
					moduleData.Value = null;
					return;
				}
				else if (ModuleData?.ModuleType == value)
					return;

				if (!typeof(Keysharp.Runtime.Module).IsAssignableFrom(value))
					return;

				moduleData.Value = ModuleData.GetOrCreate(value);
			}
		}

		private void InitializeMainContext()
		{
			if (MainContext != null)
				return;
#if WINDOWS
			InitializeScreenSystemEventsOnNeutralContext();
			var current = SynchronizationContext.Current;

			if (current == null || current.GetType() == typeof(SynchronizationContext))
			{
				current = new WindowsFormsSynchronizationContext();
				SynchronizationContext.SetSynchronizationContext(current);
			}
			MainContext = current;
#else
			var app = EnsureEtoApplication();

			app.AsyncInvoke(() => {
				var current = SynchronizationContext.Current;

				if (current == null || current.GetType() == typeof(SynchronizationContext))
				{
					current = new EtoSynchronizationContext(Application.Instance);
					SynchronizationContext.SetSynchronizationContext(current);
				}

				MainContext = current;
			});
#endif
		}

		internal static void InvokeOnUIThread(Action action)
		{
			if (action == null)
				return;

			var script = TheScript;

			if (script == null || script.IsOnMainThread)
			{
				action();
				return;
			}

			if (script.MainContext != null)
				script.MainContext.Send(_ => action(), null);
#if !WINDOWS
			else if (Application.Instance != null)
				Application.Instance.Invoke(action);
#endif
			else
				action();
		}

		internal static T InvokeOnUIThread<T>(Func<T> action)
		{
			if (action == null)
				return default;

			var script = TheScript;

			if (script == null || script.IsOnMainThread || script.MainContext == null)
			{
#if !WINDOWS
				if (script != null && !script.IsOnMainThread && Application.Instance != null)
				{
					T appResult = default;
					Application.Instance.Invoke(() => appResult = action());
					return appResult;
				}
#endif
				return action();
			}

			T uiResult = default;
			script.MainContext.Send(_ => uiResult = action(), null);
			return uiResult;
		}

		internal static void PostToUIThread(Action action)
		{
			if (action == null)
				return;

			var script = TheScript;

			if (script?.MainContext != null)
				script.MainContext.Post(_ => action(), null);
#if !WINDOWS
			else if (Application.Instance != null)
				Application.Instance.AsyncInvoke(action);
#endif
			else
				action();
		}

#if !WINDOWS
		private static Application EnsureEtoApplication()
		{
			var app = Application.Instance ?? new Application();

			if (app == null)
				throw new Exception("Unable to start Eto Application");

			if (etoAppConfigured)
				return app;

			app.UnhandledException += (s, e) =>
			{
				if (e.ExceptionObject is Keysharp.Builtins.Flow.UserRequestedExitException) return;
				System.Diagnostics.Debug.Write("ThreadException caught: " + e.ExceptionObject);
			};

#if OSX
			if (app.Handler is Eto.Mac.Forms.ApplicationHandler macHandler)
				macHandler.AllowClosingMainForm = true;
#endif

#if LINUX
			try
			{
				var settings = Gtk.Settings.Default;
				if (settings != null)
				{
					settings.SetProperty("gtk-menu-images", new GLib.Value(true));
					settings.SetProperty("gtk-button-images", new GLib.Value(true));
				}
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine("Failed to enable GTK menu images: " + ex);
			}
#endif

			etoAppConfigured = true;
			return app;
		}

#endif

		private void RunAutoExecSection(Func<object> userInit)
		{
			var autoExecResult = false;
			var executionResult = EventScheduler.TryExecuteThreadLaunch(0, false, false, tv =>
			{
				var prevConfigData = tv.configData;
				tv.configData = AccessorData.threadConfigDataPrototype;

				try
				{
					autoExecResult = Keysharp.Internals.Flow.TryCatch(() =>
					{
						_ = userInit();
						isReadyToExecute = true;
					});
				}
				finally
				{
					tv.configData = prevConfigData;
				}
			});

			if (executionResult != ScriptEventExecutionResult.Executed)
				return;

			if (!autoExecResult && !persistent)
				_ = Keysharp.Builtins.Flow.ExitApp(1);

			ExitIfNotPersistent();
		}

		private void InitializeMainWindow(string title, bool _persistent, bool showInTaskbar, bool initializeUiChrome = true)
		{
			mainWindow = new MainWindow();

			if (!string.IsNullOrEmpty(title))
				mainWindow.Text = title;

			if (initializeUiChrome && normalIcon != null)
				mainWindow.Icon = normalIcon;

			persistent = _persistent;
			mainWindowGui = new Gui(null, null, null, mainWindow);

			if (initializeUiChrome && Tray == null)
				CreateTrayMenu();

			mainWindow.AllowShowDisplay = false;
			mainWindow.ShowInTaskbar = showInTaskbar;
		}

		public void RunMainWindow(string title, Func<object> userInit, bool _persistent)
		{
			InitializeMainContext();

			if (IsUiInitializationBlocked)
			{
				// Eto.Mac cannot initialize under dotnet testhost because it is not an app bundle.
				// Run tests headlessly on macOS testhost.
				SuppressErrorOccurredDialog = true;
				RunAutoExecSection(userInit);
				return;
			}

			if (IsHeadless)
				SuppressErrorOccurredDialog = true;

#if WINDOWS
			InitializeMainWindow(title, _persistent, !NoMainWindow); // Can't use !suppressTestHostUi here or OnMessage test fails
			_ = mainWindow.BeginInvoke(() => RunAutoExecSection(userInit));
			Application.Run(mainWindow);
#else
			var suppressTestHostUi = IsTestHost;

			var app = EnsureEtoApplication();
#if LINUX
			Keysharp.Internals.Window.Linux.WindowManager.InstallTestLoopXErrorHandler();
#endif

			app.AsyncInvoke(() => InitializeUnixMainWindow(app, title, userInit, _persistent, suppressTestHostUi));

			app.Run();
#endif
		}

#if !WINDOWS
		private void InitializeUnixMainWindow(Eto.Forms.Application app, string title, Func<object> userInit, bool persistentState, bool suppressTestHostUi)
		{
			var showUiChrome = !suppressTestHostUi;
			InitializeMainWindow(title, persistentState, !NoMainWindow && showUiChrome, showUiChrome);
			mainWindow.Closed += (_, __) =>
			{
				if (hasExited)
					app.Quit();
			};
			if (suppressTestHostUi)
			{
				mainWindow.Show();
				mainWindow.Hide();
			}
			else if (!NoMainWindow)
				mainWindow.Show();

				app.AsyncInvoke(() => RunAutoExecSection(userInit));
			}
#endif

		public void SetName(string s)
        {
			scriptName = s;

			//If we're running via passing in a script and are not in a unit test, then set the working directory to that of the script file.
			var path = Path.GetFileName(
#if WINDOWS
				Application.ExecutablePath
#else
				Environment.ProcessPath ?? string.Empty
#endif
				).ToLowerInvariant();

			if (!IsTestHost && path != "testhost.exe" && path != "testhost.dll" && path != "testhost" && !A_IsCompiled)
				_ = Dir.SetWorkingDir(A_ScriptDir);
        }

		public void SetReady() => isReadyToExecute = true;

		public static void ProcessUnhandledException(Script script, Exception ex)
		{
			if (ex == null)
				return;

			var unwrapped = Keysharp.Runtime.Flow.UnwrapException(ex);

			if (unwrapped is Keysharp.Builtins.Flow.UserRequestedExitException)
				return;

			if (unwrapped is KeysharpException kserr)
			{
				var msg = "Uncaught Keysharp exception:\r\n" + kserr;
				WriteUncaughtErrorToStdErr(msg);

				if (script == null || !script.SuppressErrorOccurredDialog)
				{
					var title = script != null ? Accessors.A_ScriptName + ": Unhandled exception" : "Keysharp: Unhandled exception";
					_ = Dialogs.MsgBox(msg, title, "iconx");
				}

				return;
			}

			var genericMsg = "Uncaught exception:\r\n" + "Message: " + unwrapped.Message + "\r\nStack: " + unwrapped.StackTrace;
			WriteUncaughtErrorToStdErr("Uncaught exception:\r\n" + unwrapped);

			if (script == null || !script.SuppressErrorOccurredDialog)
			{
				var title = script != null ? Accessors.A_ScriptName + ": Unhandled exception" : "Keysharp: Unhandled exception";
				_ = Dialogs.MsgBox(genericMsg, title, "iconx");
			}
		}

		public static void TryProcessUnhandledException(Script script, Exception ex)
		{
			try
			{
				ProcessUnhandledException(script, ex);
			}
			catch (Exception)
			{
			}
		}

		public static void TryProcessKeysharpException(Script script, KeysharpException kserr)
		{
			if (kserr == null)
				return;

			if (!kserr.UserError.Processed)
			{
				try
				{
					_ = Errors.ErrorOccurred(kserr.UserError, kserr.UserError.ExcType);
				}
				catch (Exception)
				{
				}
			}

			if (!kserr.UserError.Handled)
				TryProcessUnhandledException(script, kserr);
		}

		public static void SafeExit(int code)
		{
			Environment.ExitCode = code;

			try
			{
				_ = Keysharp.Builtins.Flow.ExitApp(code);
			}
			catch (Exception)
			{
			}
		}

		public void HandleUncaughtException(Exception ex) => ProcessUnhandledException(this, ex);

		/// <summary>
		/// Writes uncaught script errors to stderr in debug builds to improve test diagnostics.
		/// </summary>
		/// <param name="text">The text to write.</param>
		[Conditional("DEBUG")]
		[PublicHiddenFromUser]
		internal static void WriteUncaughtErrorToStdErr(string text)
		{
			if (!string.IsNullOrEmpty(text))
				Console.Error.WriteLine(text);
		}

		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		protected virtual void Dispose(bool disposing)
		{
			if (Interlocked.Exchange(ref disposeStarted, 1) != 0)
				return;

			HookThread?.Stop();
			stringsData?.Free();
			flowData?.Dispose();

			if (!disposing)
				return;

#if WINDOWS
			Application.RemoveMessageFilter(msgFilter);
#elif !WINDOWS
			msgFilter?.Detach();
#endif

			if (Tray != null)
			{
				InvokeOnUIThread(DisposeTrayIcon);
			}

			if (!IsMainWindowClosing)
			{
				var window = mainWindow;
				window?.CheckedInvoke(() =>
				{
					window.Close();
					mainWindow = null;
				}, false);
			}

#if WINDOWS
			Dialogs.DisposeWindowsMsgBoxThread();
#endif
		}

		private void DisposeTrayIcon()
		{
			var tray = Tray;

			if (tray == null)
				return;

			try
			{
				tray.MouseClick -= TrayIcon_MouseClick;
				tray.MouseDoubleClick -= TrayIcon_MouseDoubleClick;
				tray.Tag = null;
				tray.Dispose();
			}
			catch
			{
			}
			finally
			{
				Tray = null;
				trayMenu = null;
			}
		}

		public override string ToString()
		{
			return $"Script {scriptName} {instanceCount++}";
		}

		public static void VerifyVersion(string ver, bool reqAhk, int line, string code)
		{
			static bool HasOperator(string v)
			{
				return v.StartsWith("<", StringComparison.Ordinal)
					|| v.StartsWith(">", StringComparison.Ordinal)
					|| v.StartsWith("=", StringComparison.Ordinal);
			}

			var requirement = (ver ?? string.Empty).Trim();
			if (requirement.EndsWith("+", StringComparison.Ordinal))
				requirement = ">=" + requirement.TrimEnd('+').Trim();
			bool hasOp = HasOperator(requirement);

			var target = reqAhk ? "2.1" : A_AhkVersion;
			var cmp = Strings.VerCompare(target, requirement);
			var ok = hasOp ? cmp == 1L : cmp >= 0L;

			if (ok)
				return;

			if (reqAhk)
				throw new ParseException($"This script requires AutoHotkey {ver}, but Keysharp supports AutoHotkey v2", line, code);

			throw new ParseException($"This script requires Keysharp {ver}, but you have v{A_AhkVersion}", line, code);
		}

		public void WaitThreads()
		{
			//Check against 1 instead of 0, because this may be launched in a thread as a result of a hotkey.
			//If this gets stuck in a loop it means we have a thread imbalance/mismatch somewhere.
			//We added them, but never removed. While seemingly dangerous to have, it's a handy
			//way to know we've found a bug.
			while (totalExistingThreads > 1)
				Keysharp.Internals.Flow.Sleep(200);
		}

		internal static string MakeTitleWithVersion(string title) => title + " - Keysharp " + A_AhkVersion;

		internal static int[] ParseVersionToInts(string ver)
		{
			var i = 0;
			var vers = new int[] { 0, 0, 0, 0 };

			foreach (Range r in ver.AsSpan().Split('.'))
			{
				var split = ver.AsSpan(r).Trim();

				if (split.Length > 0)
				{
					if (int.TryParse(split, out var v))
						vers[i] = v;

					i++;
				}
			}

			return vers;
		}

		internal static void SetInitialFloatFormat()
		{
			var t = Thread.CurrentThread;
			var ci = new CultureInfo(t.CurrentCulture.Name);
			ci.NumberFormat.NumberDecimalDigits = 6;
			t.CurrentCulture = ci;
		}

		internal bool AnyPersistent()
		{
			if (totalExistingThreads > 0)
				return true;

			if (Gui.AnyExistingVisibleWindows())
				return true;

			if (HotkeyData.shk.Count > 0)
				return true;

			if (HotstringManager.shs.Count > 0)
				return true;

			if (!FlowData.timers.IsEmpty)
				return true;

			if (ClipFunctions.Count > 0)
				return true;

			if (FlowData.persistentValueSetByUser)
				return true;

			if (input != null)
			{
				for (var i = input; ; i = i.prev)
				{
					if (i != null)
						return true;
				}
			}

			return false;
		}

		internal ResultType IsCycleComplete(int aSleepDuration, DateTime aStartTime, bool aAllowEarlyReturn)
		// This function is used just to make MsgSleep() more readable/understandable.
		{
			var kbdMouseSender = HookThread.kbdMsSender;//This should always be non-null if any hotkeys/strings are present.
			// Note: Even if TickCount has wrapped due to system being up more than about 49 days,
			// DWORD subtraction still gives the right answer as long as aStartTime itself isn't more
			// than about 49 days ago. Note: must cast to int or any negative result will be lost
			// due to DWORD type:
			var tick_now = DateTime.UtcNow;

			if (!aAllowEarlyReturn && (int)(aSleepDuration - (tick_now - aStartTime).TotalMilliseconds) > SLEEP_INTERVAL_HALF)
				// Early return isn't allowed and the time remaining is large enough that we need to
				// wait some more (small amounts of remaining time can't be effectively waited for
				// due to the 10ms granularity limit of SetTimer):
				return ResultType.Fail; // Tell the caller to wait some more.

			// v1.0.38.04: Reset mLastPeekTime because caller has just done a GetMessage() or PeekMessage(),
			// both of which should have routed events to the keyboard/mouse hooks like LONG_OPERATION_UPDATE's
			// PeekMessage() and thus satisfied the reason that mLastPeekTime is tracked in the first place.
			// UPDATE: Although the hooks now have a dedicated thread, there's a good chance mLastPeekTime is
			// beneficial in terms of increasing GUI & script responsiveness, so it is kept.
			// The following might also improve performance slightly by avoiding extra Peek() calls, while also
			// reducing premature thread interruptions.
				RecordMessageCheck();
				return ResultType.Ok;
			}

		internal void SetHotNamesAndTimes(string name)
		{
			// Just prior to launching the hotkey, update these values to support built-in
			// variables such as A_TimeSincePriorHotkey:
			priorHotkeyName = thisHotkeyName;//None of this will work until we come up with a way to manage thread order.//TODO
			priorHotkeyStartTime = thisHotkeyStartTime;
			// Unlike hotkeys -- which can have a name independent of their label by being created or updated
			// with the HOTKEY command -- a hot string's unique name is always its label since that includes
			// the options that distinguish between (for example) :c:ahk:: and ::ahk::
			thisHotkeyName = name;
			thisHotkeyStartTime = DateTime.UtcNow; // Fixed for v1.0.35.10 to not happen for GUI
		}

		private static HookThread CreateHookThread()
		{
#if WINDOWS
			return new WindowsHookThread();
#elif LINUX
			return new LinuxHookThread();
#elif OSX
			return new MacHookThread();
#else
#error Unsupported platform. Only WINDOWS, LINUX, and OSX are supported.
#endif
		}

		private void PrivateClipboardUpdate(params object[] o)
		{
#if WINDOWS
			if (Clipboard.ContainsText() || Clipboard.ContainsFileDropList())
#else
			if (Clipboard.Instance.ContainsText)
#endif
				_ = IfTest(ClipFunctions.InvokeEventHandlers(1));
			else if (!Ks.IsClipboardEmpty())
				_ = IfTest(ClipFunctions.InvokeEventHandlers(2));
			else
				_ = IfTest(ClipFunctions.InvokeEventHandlers(0));
		}

		internal Type GetNativeType(Any obj)
		{
				while (obj != null)
				{
					var t = obj.type;
				if (!string.Equals(t.Namespace, ProgramNamespace, StringComparison.OrdinalIgnoreCase))
				{
					// we found a built-in prototype object
					if (t == typeof(Class)) return typeof(KeysharpObject);
					return t;
				}

					// follow the “base” link:
					obj = obj.Base;
				}
				// fallback?
				return typeof(Any);
		}

		internal void UpdateClipboardMonitoring()
		{
			var window = mainWindow;

			if (window == null)
				return;

			var enabled = ClipFunctions.Count > 0;
			PostToUIThread(() =>
			{
				if (window.IsClosing)
					return;

				window.ClipboardUpdate -= PrivateClipboardUpdate;

				if (enabled)
					window.ClipboardUpdate += PrivateClipboardUpdate;

#if !WINDOWS
				window.SetClipboardMonitoringEnabled(enabled);
#endif
			});
		}
	}
}
