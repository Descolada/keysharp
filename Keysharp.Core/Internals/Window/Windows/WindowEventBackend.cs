#if WINDOWS
using Keysharp.Builtins;

namespace Keysharp.Internals.Window.Windows
{
	/// <summary>
	/// Windows <see cref="IWindowEventBackend"/> built on <c>SetWinEventHook</c> (WINEVENT_OUTOFCONTEXT). One
	/// hook is installed per requested category; a single shared <c>WINEVENTPROC</c> normalizes each native
	/// event into a <see cref="WindowEventRaw"/>. Out-of-context hooks are delivered to the message queue of the
	/// thread that installed them, so install/uninstall is marshalled onto the UI thread (which runs the
	/// WinForms message loop); the proc therefore fires on the UI thread during normal message dispatch and
	/// never re-enters script code synchronously.
	/// </summary>
	internal sealed class WindowEventBackend : IWindowEventBackend
	{
		// WINEVENTPROC dwFlags.
		private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
		// Object identifiers: we only care about whole top-level windows, not child controls/caret/etc.
		private const int OBJID_WINDOW = 0;
		private const int CHILDID_SELF = 0;

		// Native event ids we map (see winuser.h).
		private const uint EVENT_SYSTEM_FOREGROUND     = 0x0003;
		private const uint EVENT_SYSTEM_MINIMIZESTART  = 0x0016;
		private const uint EVENT_SYSTEM_MINIMIZEEND    = 0x0017;
		private const uint EVENT_OBJECT_CREATE         = 0x8000;
		private const uint EVENT_OBJECT_DESTROY        = 0x8001;
		private const uint EVENT_OBJECT_SHOW           = 0x8002;
		private const uint EVENT_OBJECT_HIDE           = 0x8003;
		private const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
		private const uint EVENT_OBJECT_NAMECHANGE     = 0x800C;
		private const uint EVENT_OBJECT_CLOAKED        = 0x8017;

		private static readonly WindowEventMask[] AllBits =
		[
			WindowEventMask.Active, WindowEventMask.Create, WindowEventMask.Close, WindowEventMask.Move,
			WindowEventMask.Show, WindowEventMask.Minimize, WindowEventMask.Restore, WindowEventMask.TitleChange
		];

		// HWINEVENTHOOK handles keyed by category (a category may install several hooks, e.g. Close watches
		// destroy/hide/cloak). Only touched on the UI thread.
		private readonly Dictionary<WindowEventMask, nint[]> hooks = new();
		// Held for the backend lifetime so the GC cannot collect the delegate the OS calls.
		private readonly WindowsAPI.WinEventProc proc;
		private bool disposed;

		internal WindowEventBackend() => proc = OnWinEvent;

		public Action<WindowEventRaw> Sink { get; set; }

		public void Start(WindowEventMask mask) => PostToUIThread(() => InstallOnUI(mask));

		public void Stop(WindowEventMask mask) => PostToUIThread(() => UninstallOnUI(mask));

		public void Dispose()
		{
			disposed = true;
			PostToUIThread(() => UninstallOnUI((WindowEventMask)~0));
		}

		// ---- UI-thread hook management ------------------------------------------------------

		private void InstallOnUI(WindowEventMask mask)
		{
			if (disposed)
				return;

			foreach (var bit in AllBits)
			{
				if ((mask & bit) == 0 || hooks.ContainsKey(bit))
					continue;

				var handles = new List<nint>();

				foreach (var id in EventIdsFor(bit))
				{
					var handle = WindowsAPI.SetWinEventHook(id, id, 0, proc, 0, 0, WINEVENT_OUTOFCONTEXT);

					if (handle != 0)
						handles.Add(handle);
					else
						Ks.OutputDebugLine($"SetWinEventHook failed for {bit} (event 0x{id:X}).");
				}

				hooks[bit] = handles.ToArray();
			}
		}

		private void UninstallOnUI(WindowEventMask mask)
		{
			foreach (var bit in AllBits)
			{
				if ((mask & bit) == 0 || !hooks.TryGetValue(bit, out var handles))
					continue;

				foreach (var handle in handles)
					_ = WindowsAPI.UnhookWinEvent(handle);

				_ = hooks.Remove(bit);
			}
		}

		// ---- native callback (runs on the UI thread) ---------------------------------------

		private void OnWinEvent(nint hWinEventHook, uint eventType, nint hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
		{
			// Ignore non-window objects: caret, cursor, menu items, scrollbars, list items, etc. all arrive with
			// a non-window idObject or a non-self idChild.
			if (hwnd == 0 || idObject != OBJID_WINDOW || idChild != CHILDID_SELF)
				return;

			var sink = Sink;

			if (sink == null)
				return;

			var type = TypeFor(eventType);

			if (type == null)
				return;

			// Restrict to top-level windows (a window that is its own root ancestor) so child controls don't
			// generate window events — e.g. an edit/text control whose text changes raises EVENT_OBJECT_NAMECHANGE,
			// which would otherwise flood TitleChange subscribers. The window is already gone for Close (destroy)
			// or merely hidden (hide/cloak), so GetAncestor can't be used there; the manager keeps Close top-level
			// via its matching-window set (populated only from these top-level Create/Show events).
			if (type != WindowEventType.Close && WindowsAPI.GetAncestor(hwnd, gaFlags.GA_ROOT) != hwnd)
				return;

			// Spurious foreground events are sometimes reported for windows that aren't actually the foreground
			// window; drop those so Active fires only for the real foreground window.
			if (eventType == EVENT_SYSTEM_FOREGROUND && WindowsAPI.GetForegroundWindow() != hwnd)
				return;

			sink(new WindowEventRaw(type.Value, hwnd, ToMonotonicMs(dwmsEventTime)));
		}

		/// <summary>
		/// Normalizes the native 32-bit <paramref name="dwmsEventTime"/> (GetTickCount milliseconds at the moment the
		/// event occurred, which wraps every ~49.7 days) into the locked cross-platform WinEvent time contract: a
		/// 64-bit monotonic milliseconds-since-boot timestamp on the same clock as <see cref="Environment.TickCount64"/>
		/// (what the Linux/macOS backends emit). The full 64-bit value is reconstructed from the current tick count so
		/// it never wraps and stays comparable across backends. Out-of-context hooks are delivered within milliseconds
		/// of the event, so the unsigned 32-bit delta recovers the true event time even across a 32-bit wrap boundary;
		/// a timestamp that appears to be in the future (minor clock skew) is clamped to "now".
		/// </summary>
		private static long ToMonotonicMs(uint dwmsEventTime)
		{
			var now64 = Environment.TickCount64;
			var elapsed = (uint)now64 - dwmsEventTime;   // unsigned subtraction: correct across a 32-bit GetTickCount wrap
			return elapsed > int.MaxValue ? now64 : now64 - elapsed;
		}

		private static WindowEventType? TypeFor(uint eventType) => eventType switch
		{
			EVENT_SYSTEM_FOREGROUND     => WindowEventType.Active,
			EVENT_OBJECT_CREATE         => WindowEventType.Create,
			EVENT_OBJECT_DESTROY        => WindowEventType.Close,
			EVENT_OBJECT_HIDE           => WindowEventType.Close,   // hidden window = "closed" for a DetectHiddenWindows-off hook
			EVENT_OBJECT_CLOAKED        => WindowEventType.Close,   // cloaked (e.g. virtual desktop) = hidden
			EVENT_OBJECT_LOCATIONCHANGE => WindowEventType.Move,
			EVENT_OBJECT_SHOW           => WindowEventType.Show,
			EVENT_SYSTEM_MINIMIZESTART  => WindowEventType.Minimize,
			EVENT_SYSTEM_MINIMIZEEND    => WindowEventType.Restore,
			EVENT_OBJECT_NAMECHANGE     => WindowEventType.TitleChange,
			_ => null
		};

		private static uint[] EventIdsFor(WindowEventMask bit) => bit switch
		{
			WindowEventMask.Active      => [EVENT_SYSTEM_FOREGROUND],
			WindowEventMask.Create      => [EVENT_OBJECT_CREATE],
			WindowEventMask.Close       => [EVENT_OBJECT_DESTROY, EVENT_OBJECT_HIDE, EVENT_OBJECT_CLOAKED],
			WindowEventMask.Move        => [EVENT_OBJECT_LOCATIONCHANGE],
			WindowEventMask.Show        => [EVENT_OBJECT_SHOW],
			WindowEventMask.Minimize    => [EVENT_SYSTEM_MINIMIZESTART],
			WindowEventMask.Restore     => [EVENT_SYSTEM_MINIMIZEEND],
			WindowEventMask.TitleChange => [EVENT_OBJECT_NAMECHANGE],
			_ => []
		};
	}
}
#endif
