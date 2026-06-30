#if LINUX
using System.Globalization;
using Tmds.DBus;
namespace Keysharp.Internals.Window.Linux.Wayland
{
	// D-Bus member names are case-sensitive; suppress the IDE naming rule.
#pragma warning disable IDE1006

	/// <summary>
	/// Tmds.DBus proxy for Cinnamon's privileged JavaScript evaluation endpoint.
	/// Cinnamon's window manager (Muffin) is Mutter-based, so the same Meta/global
	/// JS API used by the GNOME Shell extension works here, but Cinnamon still exposes
	/// org.Cinnamon.Eval directly (unlike GNOME, which locked Eval down in 41+), so no
	/// bundled extension is required. The interface must be public for Tmds.DBus.
	/// </summary>
	[DBusInterface("org.Cinnamon")]
	public interface ICinnamon : IDBusObject
	{
		Task<(bool success, string result)> EvalAsync(string script);
	}

#pragma warning restore IDE1006

	/// <summary>
	/// Static bridge to Cinnamon's org.Cinnamon.Eval D-Bus method. Mirrors
	/// <see cref="GnomeShellBridge"/>'s lazy, thread-safe, timeout-guarded connection
	/// handling. Each query runs a small self-contained JS snippet whose return value is
	/// a JSON string; Cinnamon JSON-encodes that return value again, so results come back
	/// double-encoded and are unwrapped in <see cref="EvalJson"/>.
	/// </summary>
	internal static class CinnamonShellBridge
	{
		private const string ServiceName = "org.Cinnamon";
		private const string ObjectPath  = "/org/Cinnamon";
		private const int    TimeoutMs   = 2000;

		// Shared JS helpers injected into every query: window-type filter, window->info
		// serializer (identical shape to the GNOME extension so the parser is shared in
		// spirit), and a stable_sequence lookup. Uses single-quoted JS string literals so
		// the surrounding C# string needs no escaping.
		private const string JsHelpers =
			"const Meta=imports.gi.Meta;" +
			"function tracked(w){switch(w.window_type){case Meta.WindowType.NORMAL:case Meta.WindowType.DIALOG:case Meta.WindowType.MODAL_DIALOG:case Meta.WindowType.UTILITY:return true;default:return false;}}" +
			"function info(w){const f=w.get_frame_rect();return{id:String(w.get_stable_sequence()),title:w.get_title()||'',appId:w.get_wm_class()||w.get_wm_class_instance()||'',pid:w.get_pid(),frame:{x:f.x,y:f.y,width:f.width,height:f.height},client:{x:f.x,y:f.y,width:f.width,height:f.height},active:!!w.appears_focused,minimized:!!w.minimized,maximized:!!(w.maximized_horizontally&&w.maximized_vertically),visible:!w.minimized};}" +
			"function find(s){const a=global.get_window_actors();for(let i=0;i<a.length;i++){const w=a[i].get_meta_window();if(w&&w.get_stable_sequence()===s)return w;}return null;}";

		private static readonly SemaphoreSlim initSemaphore = new(1, 1);
		private static Connection connection;
		private static ICinnamon proxy;
		private static bool initFailed;

		internal static string QueryActiveWindow()
			=> EvalJson("(function(){try{" + JsHelpers + "const w=global.display.get_focus_window();return JSON.stringify({ok:true,window:(w&&tracked(w))?info(w):null});}catch(e){return JSON.stringify({ok:false});}})()");

		internal static string QueryWindowList(bool includeHidden)
			=> EvalJson("(function(){try{" + JsHelpers + "const a=global.get_window_actors();const out=[];for(let i=0;i<a.length;i++){const w=a[i].get_meta_window();if(!w||!tracked(w))continue;if(!" + (includeHidden ? "true" : "false") + "&&w.minimized)continue;out.push(info(w));}return JSON.stringify({ok:true,windows:out});}catch(e){return JSON.stringify({ok:false,windows:[]});}})()");

		internal static bool QueryCursorPosition(out int x, out int y)
		{
			x = 0;
			y = 0;
			var json = EvalJson("(function(){try{const p=global.get_pointer();return JSON.stringify({ok:true,x:Math.round(p[0]),y:Math.round(p[1])});}catch(e){return JSON.stringify({ok:false});}})()");

			if (json.IsNullOrEmpty())
				return false;

			try
			{
				using var doc = JsonDocument.Parse(json);
				var root = doc.RootElement;

				if (!GetBool(root, "ok"))
					return false;

				x = GetInt(root, "x");
				y = GetInt(root, "y");
				return true;
			}
			catch
			{
				return false;
			}
		}

		internal static bool SendFocusWindow(ulong seq)
			=> RunOk("(function(){try{" + JsHelpers + "const w=find(" + seq + ");if(w){if(w.minimized)w.unminimize();w.activate(global.get_current_time());}return JSON.stringify({ok:!!w});}catch(e){return JSON.stringify({ok:false});}})()");

		internal static bool SendMoveResize(ulong seq, int x, int y, int width, int height)
			=> RunOk("(function(){try{" + JsHelpers + "const w=find(" + seq + ");if(w){const f=w.get_frame_rect();w.move_resize_frame(true," + x + "===-2147483648?f.x:" + x + "," + y + "===-2147483648?f.y:" + y + "," + width + ">0?" + width + ":f.width," + height + ">0?" + height + ":f.height);}return JSON.stringify({ok:!!w});}catch(e){return JSON.stringify({ok:false});}})()");

		internal static bool SendSetWindowState(ulong seq, int state)
			=> RunOk("(function(){try{" + JsHelpers + "const w=find(" + seq + ");if(w){if(" + state + "===1){w.minimize();}else if(" + state + "===2){if(w.minimized)w.unminimize();w.maximize(Meta.MaximizeFlags.BOTH);}else{if(w.minimized)w.unminimize();w.unmaximize(Meta.MaximizeFlags.BOTH);}}return JSON.stringify({ok:!!w});}catch(e){return JSON.stringify({ok:false});}})()");

		internal static bool SendCloseWindow(ulong seq)
			=> RunOk("(function(){try{" + JsHelpers + "const w=find(" + seq + ");if(w)w.delete(global.get_current_time());return JSON.stringify({ok:!!w});}catch(e){return JSON.stringify({ok:false});}})()");

		internal static bool SendSetAlwaysOnTop(ulong seq, bool above)
			=> RunOk("(function(){try{" + JsHelpers + "const w=find(" + seq + ");if(w){if(" + (above ? "true" : "false") + "){if(!w.is_above())w.make_above();}else{if(w.is_above())w.unmake_above();}}return JSON.stringify({ok:!!w});}catch(e){return JSON.stringify({ok:false});}})()");

		// Lazily creates a Clutter virtual pointer (Muffin is Clutter-based, same API as the
		// GNOME extension) and stashes it on `global` so it persists across Eval calls.
		private const string JsVPointer =
			"const Clutter=imports.gi.Clutter;const GLib=imports.gi.GLib;" +
			"if(!global._ksVPointer){global._ksVPointer=Clutter.get_default_backend().get_default_seat().create_virtual_device(Clutter.InputDeviceType.POINTER_DEVICE);}" +
			"const vp=global._ksVPointer;";

		internal static bool SendMouseMoveAbsolute(int x, int y)
			=> RunOk("(function(){try{" + JsVPointer + "vp.notify_absolute_motion(GLib.get_monotonic_time()," + x + "," + y + ");return JSON.stringify({ok:true});}catch(e){return JSON.stringify({ok:false});}})()");

		internal static bool SendMouseMoveRelative(int dx, int dy)
			=> RunOk("(function(){try{" + JsVPointer + "const p=global.get_pointer();vp.notify_absolute_motion(GLib.get_monotonic_time(),p[0]+(" + dx + "),p[1]+(" + dy + "));return JSON.stringify({ok:true});}catch(e){return JSON.stringify({ok:false});}})()");

		internal static bool SendMouseButton(uint button, bool pressed)
			=> RunOk("(function(){try{" + JsVPointer + "vp.notify_button(GLib.get_monotonic_time()," + button + ",Clutter.ButtonState." + (pressed ? "PRESSED" : "RELEASED") + ");return JSON.stringify({ok:true});}catch(e){return JSON.stringify({ok:false});}})()");

		internal static bool SendMouseScroll(int delta, bool vertical)
		{
			var dir = vertical ? (delta > 0 ? "UP" : "DOWN") : (delta > 0 ? "RIGHT" : "LEFT");
			var notches = Math.Max(1, Math.Abs((int)Math.Round(delta / 120.0)));
			return RunOk("(function(){try{" + JsVPointer + "const t=GLib.get_monotonic_time();for(let i=0;i<" + notches + ";i++)vp.notify_discrete_scroll(t,Clutter.ScrollDirection." + dir + ",Clutter.ScrollSource.WHEEL);return JSON.stringify({ok:true});}catch(e){return JSON.stringify({ok:false});}})()");
		}

		private static bool RunOk(string js)
		{
			var json = EvalJson(js);

			if (json.IsNullOrEmpty())
				return false;

			try
			{
				using var doc = JsonDocument.Parse(json);
				return GetBool(doc.RootElement, "ok");
			}
			catch
			{
				return false;
			}
		}

		// Runs JS via Eval and returns the inner JSON string. Cinnamon returns
		// [success, JSON.stringify(returnValue)]; since our snippets already return a JSON
		// string, the D-Bus payload is that string encoded a second time — decode one layer.
		private static string EvalJson(string js)
		{
			var raw = Eval(js);

			if (raw == null)
				return null;

			try
			{
				return JsonSerializer.Deserialize<string>(raw);
			}
			catch
			{
				// Not double-encoded (older Cinnamon); use as-is.
				return raw;
			}
		}

		private static string Eval(string js)
		{
			try
			{
				var p = EnsureProxy();

				if (p == null)
					return null;

				using var cts = new CancellationTokenSource(TimeoutMs);
				var (ok, result) = Task.Run(() => p.EvalAsync(js), cts.Token).GetAwaiter().GetResult();
				return ok ? result : null;
			}
			catch
			{
				return null;
			}
		}

		private static ICinnamon EnsureProxy()
		{
			if (proxy != null)
				return proxy;

			if (initFailed)
				return null;

			initSemaphore.Wait();

			try
			{
				if (proxy != null)
					return proxy;

				if (initFailed)
					return null;

				var localConn = new Connection(Tmds.DBus.Address.Session);
				Task.Run(() => localConn.ConnectAsync()).GetAwaiter().GetResult();
				connection = localConn;
				proxy = localConn.CreateProxy<ICinnamon>(ServiceName, new ObjectPath(ObjectPath));
				return proxy;
			}
			catch
			{
				initFailed = true;
				return null;
			}
			finally
			{
				_ = initSemaphore.Release();
			}
		}

		private static bool GetBool(JsonElement e, string name)
			=> e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

		private static int GetInt(JsonElement e, string name)
			=> e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
	}

	/// <summary>
	/// Wayland window-management backend for the Cinnamon desktop (Muffin compositor),
	/// driven through <see cref="CinnamonShellBridge"/>. Provides the introspection that
	/// Wayland denies foreign clients (active window, window list/geometry, cursor) so
	/// WinActive/WinExist/WinGetTitle("A")/WinGetPos work on Cinnamon-Wayland sessions.
	/// </summary>
	internal sealed class CinnamonBackend : IWaylandBackend
	{
		// Bit 60 marks a handle as Cinnamon's; keeps it above the 32-bit X11 XID range and
		// distinct from GNOME (bit 61) and KWin (bit 62).
		private const long CinnamonBit = unchecked((long)0x1000_0000_0000_0000L);

		public string Name => "Cinnamon";

		// Inject pointer events through Muffin's Clutter virtual device (same approach as the
		// GNOME/KWin backends). The inputd sender prefers this for absolute moves/clicks/scroll
		// because uinput's normalized coordinates don't map reliably on Wayland; relative moves
		// still use inputd. Falls back to inputd automatically if any Eval call fails.
		public bool SupportsMouse => true;

		internal static bool IsAvailable()
			=> EnvContains("XDG_CURRENT_DESKTOP", "cinnamon")
			   || EnvContains("DESKTOP_SESSION", "cinnamon")
			   || EnvContains("XDG_SESSION_DESKTOP", "cinnamon");

		// Cinnamon's bridge has no push channel, so window events come from the generic polling source
		// (diffs successive TryListWindows/TryGetActiveWindow snapshots).
		public bool SupportsWindowEvents => true;

		public IDisposable SubscribeWindowEvents(Action<WaylandWindowEvent> sink)
			=> sink == null ? null : new WaylandPollingEventSource(this, sink);

		public bool TryGetCursorPos(out int x, out int y)
			=> CinnamonShellBridge.QueryCursorPosition(out x, out y);

		public bool TryListWindows(bool includeHidden, out IReadOnlyList<WaylandWindowInfo> windows)
		{
			windows = [];
			var json = CinnamonShellBridge.QueryWindowList(includeHidden);

			if (json.IsNullOrEmpty())
				return false;

			try
			{
				using var doc = JsonDocument.Parse(json);
				var root = doc.RootElement;

				if (!JsonBool(root, "ok") || !root.TryGetProperty("windows", out var arr) || arr.ValueKind != JsonValueKind.Array)
					return false;

				var parsed = new List<WaylandWindowInfo>();

				foreach (var item in arr.EnumerateArray())
					if (TryParseWindow(item, out var info))
						parsed.Add(info);

				windows = parsed;
				return true;
			}
			catch
			{
				return false;
			}
		}

		public bool TryGetActiveWindow(out WaylandWindowInfo window)
			=> TryParseSingleWindow(CinnamonShellBridge.QueryActiveWindow(), out window);

		public bool TryGetWindow(nint handle, out WaylandWindowInfo window)
		{
			window = null;

			if (!TryHandleToSeq(handle, out _) || !TryListWindows(true, out var all))
				return false;

			window = all.FirstOrDefault(w => w.Handle == handle);
			return window != null;
		}

		public bool TryGetWindowAt(int x, int y, out WaylandWindowInfo window)
		{
			window = null;

			if (!TryListWindows(false, out var all))
				return false;

			// List is bottom-to-top; walk top-down so the topmost window at the point wins.
			for (var i = all.Count - 1; i >= 0; i--)
			{
				if (all[i].FrameGeometry.Contains(x, y))
				{
					window = all[i];
					return true;
				}
			}

			return false;
		}

		public bool TryActivateWindow(nint handle)
			=> TryHandleToSeq(handle, out var seq) && CinnamonShellBridge.SendFocusWindow(seq);

		public bool TryMoveResizeWindow(nint handle, Rectangle bounds, bool setPosition, bool setSize)
			=> TryHandleToSeq(handle, out var seq)
			   && CinnamonShellBridge.SendMoveResize(
				   seq,
				   setPosition ? bounds.X : int.MinValue,
				   setPosition ? bounds.Y : int.MinValue,
				   setSize && bounds.Width > 0 ? bounds.Width : 0,
				   setSize && bounds.Height > 0 ? bounds.Height : 0);

		public bool TrySetWindowState(nint handle, FormWindowState state)
			=> TryHandleToSeq(handle, out var seq) && CinnamonShellBridge.SendSetWindowState(seq, (int)state);

		public bool TrySetAlwaysOnTop(nint handle, bool onTop)
			=> TryHandleToSeq(handle, out var seq) && CinnamonShellBridge.SendSetAlwaysOnTop(seq, onTop);

		public bool TryCloseWindow(nint handle)
			=> TryHandleToSeq(handle, out var seq) && CinnamonShellBridge.SendCloseWindow(seq);

		public bool TrySendMouseMoveAbsolute(int x, int y)
			=> CinnamonShellBridge.SendMouseMoveAbsolute(x, y);

		public bool TrySendMouseMoveRelative(int dx, int dy)
			=> CinnamonShellBridge.SendMouseMoveRelative(dx, dy);

		public bool TrySendMouseButton(uint button, bool pressed)
			=> CinnamonShellBridge.SendMouseButton(button, pressed);

		public bool TrySendMouseScroll(int delta, bool vertical)
			=> CinnamonShellBridge.SendMouseScroll(delta, vertical);

		// ---- helpers ------------------------------------------------

		private static bool EnvContains(string variable, string token)
		{
			var value = Environment.GetEnvironmentVariable(variable);
			return !string.IsNullOrEmpty(value) && value.Contains(token, StringComparison.OrdinalIgnoreCase);
		}

		private static bool TryParseWindow(JsonElement item, out WaylandWindowInfo info)
		{
			info = null;

			if (!JsonString(item, "id", out var id) || id.IsNullOrEmpty()
				|| !ulong.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seq))
				return false;

			info = new WaylandWindowInfo(
				handle: SeqToHandle(seq),
				compositorId: id,
				title: JsonString(item, "title"),
				appId: JsonString(item, "appId"),
				pid: JsonLong(item, "pid"),
				frameGeometry: JsonRectangle(item, "frame"),
				clientGeometry: JsonRectangle(item, "client"),
				active: JsonBool(item, "active"),
				minimized: JsonBool(item, "minimized"),
				maximized: JsonBool(item, "maximized"),
				visible: JsonBool(item, "visible"));
			return true;
		}

		private static bool TryParseSingleWindow(string json, out WaylandWindowInfo window)
		{
			window = null;

			if (json.IsNullOrEmpty())
				return false;

			try
			{
				using var doc = JsonDocument.Parse(json);
				var root = doc.RootElement;

				if (!JsonBool(root, "ok")
					|| !root.TryGetProperty("window", out var item)
					|| item.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
					return false;

				return TryParseWindow(item, out window);
			}
			catch
			{
				return false;
			}
		}

		private static nint SeqToHandle(ulong seq)
			=> new((long)((seq & 0xFFFF_FFFF) | (ulong)CinnamonBit));

		private static bool TryHandleToSeq(nint handle, out ulong seq)
		{
			var h = handle.ToInt64();

			if ((h & unchecked((long)0x7000_0000_0000_0000L)) == CinnamonBit)
			{
				seq = (ulong)(h & 0xFFFF_FFFF);
				return true;
			}

			seq = 0;
			return false;
		}

		private static bool JsonBool(JsonElement element, string property)
			=> element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

		private static long JsonLong(JsonElement element, string property)
			=> element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : 0;

		private static string JsonString(JsonElement element, string property)
			=> JsonString(element, property, out var value) ? value : string.Empty;

		private static bool JsonString(JsonElement element, string property, out string value)
		{
			if (element.TryGetProperty(property, out var prop) && prop.ValueKind == JsonValueKind.String)
			{
				value = prop.GetString() ?? string.Empty;
				return true;
			}

			value = string.Empty;
			return false;
		}

		private static Rectangle JsonRectangle(JsonElement element, string property)
		{
			if (!element.TryGetProperty(property, out var rect) || rect.ValueKind != JsonValueKind.Object)
				return Rectangle.Empty;

			return new Rectangle(
				RectInt(rect, "x"),
				RectInt(rect, "y"),
				RectInt(rect, "width"),
				RectInt(rect, "height"));
		}

		private static int RectInt(JsonElement element, string property)
			=> element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;
	}
}
#endif
