namespace Keysharp.Internals
{

	/// <summary>
	/// Base for the per-OS <see cref="IWindow"/> implementations. Required query members throw until a platform
	/// implementation overrides them. Optional Try* capabilities return false by default so user-facing callers
	/// can report unsupported operations consistently.
	/// </summary>
	internal abstract class WindowBase : IWindow
	{
		private static T NotYet<T>([System.Runtime.CompilerServices.CallerMemberName] string m = "")
			=> throw new NotImplementedException($"Platform.Window.{m} is not routed through the host yet.");

		protected static bool Unsupported() => false;

		// --- query (item factories: build the neutral WindowInfo, seeded where the platform batches) ---
		public virtual Keysharp.Internals.Window.WindowInfoBase CreateWindow(nint id) => NotYet<Keysharp.Internals.Window.WindowInfoBase>();
		public virtual Keysharp.Internals.Window.WindowInfoBase ActiveWindow() => NotYet<Keysharp.Internals.Window.WindowInfoBase>();
		public virtual IReadOnlyList<Keysharp.Internals.Window.WindowInfoBase> Enumerate(bool includeHidden) => NotYet<IReadOnlyList<Keysharp.Internals.Window.WindowInfoBase>>();
		public virtual bool TryGetAt(int x, int y, out nint child) { child = default; return Unsupported(); }

		// No native by-class fast path by default (X11/Wayland/macOS enumerate); Windows overrides this.
		public virtual bool TryFindWindow(string className, string title, out nint handle) { handle = default; return Unsupported(); }
		public virtual bool IsWindow(nint h) => NotYet<bool>();
		public virtual nint GetForegroundHandle() => NotYet<nint>();
		public virtual bool TryGetParent(nint h, out nint parent) { parent = default; return Unsupported(); }
		public virtual bool TryGetTopLevel(nint h, out nint top) { top = default; return Unsupported(); }
		public virtual bool TryEnumerateChildren(nint h, out IReadOnlyList<nint> children) { children = []; return Unsupported(); }
		public virtual bool TryClientToScreen(nint h, ref Point pt) => Unsupported();
		public virtual uint GetFocusedControlThread(nint window, out nint control) { control = default; return NotYet<uint>(); }
		public virtual bool IncludeInGroups(nint h) => true;   // default: every window; Windows narrows it
		public virtual bool TryGetText(nint h, bool detectHidden, out List<string> text) { text = []; return Unsupported(); }
		public virtual void ChildFindPoint(nint h, PointAndHwnd pah) { }
		public virtual string GetTitle(nint h) => NotYet<string>();
		public virtual string GetClassName(nint h) => NotYet<string>();
		public virtual long GetPid(nint h) => NotYet<long>();
		public virtual Rectangle GetBounds(nint h) => NotYet<Rectangle>();
		public virtual Rectangle GetClientBounds(nint h) => NotYet<Rectangle>();
		public virtual long GetStyle(nint h) => NotYet<long>();
		public virtual long GetExStyle(nint h) => NotYet<long>();
		public virtual bool GetActive(nint h) => NotYet<bool>();
		public virtual bool GetVisible(nint h) => NotYet<bool>();
		public virtual bool GetEnabled(nint h) => NotYet<bool>();
		public virtual bool GetHung(nint h) => NotYet<bool>();
		public virtual bool GetExists(nint h) => NotYet<bool>();
		public virtual FormWindowState GetWindowState(nint h) => NotYet<FormWindowState>();
		public virtual bool GetAlwaysOnTop(nint h) => NotYet<bool>();
		public virtual object GetTransparency(nint h) => NotYet<object>();
		public virtual object GetTransparentColor(nint h) => NotYet<object>();
		public virtual POINT ClientToScreen(nint h) => NotYet<POINT>();

		// --- control ---
		public virtual bool TrySetAlwaysOnTop(nint h, bool onTop) => Unsupported();
		public virtual bool TryMoveResize(nint h, Rectangle bounds, bool setPos, bool setSize) => Unsupported();
		public virtual bool TrySetState(nint h, FormWindowState state) => Unsupported();
		public virtual bool TrySetStyle(nint h, long style) => Unsupported();
		public virtual bool TrySetExStyle(nint h, long exStyle) => Unsupported();
		public virtual bool TrySetTransparency(nint h, object alpha) => Unsupported();
		public virtual bool TryActivate(nint h) => Unsupported();
		public virtual bool TrySetZOrder(nint h, ZOrder z) => Unsupported();
		public virtual bool TryClose(nint h) => Unsupported();
		public virtual bool TryKill(nint h) => Unsupported();
		public virtual bool TryHide(nint h) => Unsupported();
		public virtual bool TryShow(nint h) => Unsupported();
		public virtual bool TryRedraw(nint h) => Unsupported();
		public virtual bool TryClick(nint h, Point at, uint button, int count) => Unsupported();
		public virtual bool TrySetTitle(nint h, string title) => Unsupported();
		public virtual bool TrySetVisible(nint h, bool visible) => Unsupported();
		public virtual bool TrySetEnabled(nint h, bool enabled) => Unsupported();
		public virtual bool TrySetTransparentColor(nint h, object color) => Unsupported();
	}
}
