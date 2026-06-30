#if LINUX
namespace Keysharp.Internals.Window.Linux.Wayland
{
	/// <summary>
	/// Singleton chooser for <see cref="IWaylandBackend"/>. Probes the running
	/// compositor on first access and caches the result. Override probing with
	/// <c>KEYSHARP_WAYLAND_BACKEND=auto|kwin|sway|hyprland|cosmic|gnome|cinnamon|none</c>.
	/// </summary>
	internal static class WaylandBackend
	{
		private static readonly object sync = new();
		private static IWaylandBackend current;
		private static bool probed;

		internal static IWaylandBackend Current
		{
			get
			{
				lock (sync)
				{
					if (probed)
						return current;

					probed = true;
					current = Probe();
					return current;
				}
			}
		}

		private static IWaylandBackend Probe()
		{
			// These backends drive the *Wayland* compositor's privileged introspection (cursor/window
			// geometry, z-order, input injection, overlays). In an X11/Xorg session the always-present X
			// server owns all of that, so a Wayland backend must never engage — not even when
			// XDG_CURRENT_DESKTOP names a compositor (GNOME and KDE both ship an Xorg session whose
			// XDG_CURRENT_DESKTOP is still "GNOME"/"KDE", which is exactly how a probe-by-desktop picks the
			// wrong path). Gating here, at the single source of every backend, means no current or future
			// caller can pick one up in a non-Wayland session by forgetting its own IsWaylandSession check.
			//
			// This is deliberately above the KEYSHARP_WAYLAND_BACKEND override: that override selects AMONG
			// Wayland compositors within a Wayland session; it is not an escape hatch for running a Wayland
			// path under X11 (every consumer already short-circuits on !IsWaylandSession, so a forced backend
			// would be dead code there anyway). An XWayland *client* in a Wayland session is unaffected —
			// XDG_SESSION_TYPE is still "wayland" there, so IsWaylandSession stays true.
			if (!Platform.Desktop.IsWaylandSession)
				return null;

			var forced = Environment.GetEnvironmentVariable("KEYSHARP_WAYLAND_BACKEND")?.Trim().ToLowerInvariant();

			if (!string.IsNullOrEmpty(forced))
			{
				return forced switch
				{
					"none" => null,
					"kwin" => new KWinBackend(),
					"sway" => new SwayBackend(),
					"hyprland" => new HyprlandBackend(),
					"cosmic" => new CosmicBackend(),
					"gnome" => new GnomeBackend(),
					"cinnamon" => new CinnamonBackend(),
					_ => AutoProbe()
				};
			}

			return AutoProbe();
		}

		private static IWaylandBackend AutoProbe()
		{
			// Order matters: prefer the compositor whose IPC is richest where multiple
			// detections might collide (e.g. a session set up to talk to both KWin and
			// COSMIC for transition reasons).
			if (KWinBackend.IsAvailable())
				return new KWinBackend();

			if (SwayBackend.IsAvailable())
				return new SwayBackend();

			if (HyprlandBackend.IsAvailable())
				return new HyprlandBackend();

			if (CosmicBackend.IsAvailable())
				return new CosmicBackend();

			if (GnomeBackend.IsAvailable())
				return new GnomeBackend();

			if (CinnamonBackend.IsAvailable())
				return new CinnamonBackend();

			return null;
		}

		private static bool DesktopMatches(string token)
		{
			var current = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP");

			if (string.IsNullOrEmpty(current))
				return false;

			foreach (var part in current.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
				if (part.Equals(token, StringComparison.OrdinalIgnoreCase))
					return true;

			return false;
		}

		private static bool EnvironmentContains(string variable, string token)
		{
			var value = Environment.GetEnvironmentVariable(variable);
			return !string.IsNullOrEmpty(value) && value.Contains(token, StringComparison.OrdinalIgnoreCase);
		}

		internal sealed class KWinBackend : IWaylandBackend
		{
			private readonly object handleLock = new();
			private readonly Dictionary<string, nint> handlesById = new(StringComparer.Ordinal);
			private readonly Dictionary<nint, string> idsByHandle = new();

			public string Name => "KWin";

			// KWin FakeInput evdev button codes (Linux BTN_* constants).
			private const uint BtnLeft    = 0x110;
			private const uint BtnRight   = 0x111;
			private const uint BtnMiddle  = 0x112;
			private const uint BtnBack    = 0x116;
			private const uint BtnForward = 0x115;

			internal static bool IsAvailable()
				=> DesktopMatches("KDE")
				   || EnvironmentContains("DESKTOP_SESSION", "kde")
				   || EnvironmentContains("DESKTOP_SESSION", "plasma")
				   || EnvironmentContains("XDG_SESSION_DESKTOP", "kde")
				   || EnvironmentContains("XDG_SESSION_DESKTOP", "plasma")
				   || string.Equals(Environment.GetEnvironmentVariable("KDE_FULL_SESSION"), "true", StringComparison.OrdinalIgnoreCase)
				   || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KDE_SESSION_VERSION"));

			public bool SupportsMouse => true;

			public bool TryGetCursorPos(out int x, out int y)
				=> KWinDBusBridge.QueryCursorPosition(out x, out y);

			public bool TrySendMouseMoveAbsolute(int x, int y)
				=> KWinFakeInputBridge.TryMoveAbsolute(x, y);

			public bool TrySendMouseMoveRelative(int dx, int dy)
				=> KWinFakeInputBridge.TryMoveRelative(dx, dy);

			public bool TrySendMouseButton(uint button, bool pressed)
			{
				var evdev = button switch
				{
					1 => BtnLeft,
					2 => BtnMiddle,
					3 => BtnRight,
					8 => BtnBack,
					9 => BtnForward,
					_ => 0u
				};
				return evdev != 0 && KWinFakeInputBridge.TryButton(evdev, pressed);
			}

			public bool TrySendMouseScroll(int delta, bool vertical)
			{
				// KWin FakeInput axis: 0 = vertical, 1 = horizontal.
				// KWin convention: positive delta = scroll down/right.
				// AHK/Keysharp convention: positive delta = scroll up/right.
				// Negate the vertical axis; horizontal direction is the same.
				var axis      = vertical ? 0u : 1u;
				var kwinDelta = vertical ? -(double)delta / 120.0 : (double)delta / 120.0;
				return KWinFakeInputBridge.TryAxis(axis, kwinDelta);
			}

			public bool TryListWindows(bool includeHidden, out IReadOnlyList<WaylandWindowInfo> windows)
			{
				windows = [];

				var json = RunJson(BuildListScript(includeHidden), "list");
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
			{
				window = null;
				var json = RunJson(WindowHelpers
					+ "var w = activeWindowCompat();\n"
					+ "if (!w) {\n"
					+ "  var order = windowListCompat();\n"
					+ "  for (var i = order.length - 1; i >= 0; --i) {\n"
					+ "    if (safeBool(order[i].active)) { w = order[i]; break; }\n"
					+ "  }\n"
					+ "}\n"
					+ "report({ ok: true, window: windowInfo(w, true) });\n", "active");
				return TryParseSingleWindow(json, out window);
			}

			public bool TryGetWindow(nint handle, out WaylandWindowInfo window)
			{
				window = null;

				if (!TryGetCompositorId(handle, out var id))
					return false;

				var json = RunJson(WindowHelpers
					+ $"var w = findWindow({JsonSerializer.Serialize(id)});\n"
					+ "report({ ok: true, window: windowInfo(w) });\n", "get_window");
				return TryParseSingleWindow(json, out window);
			}

			public bool TryGetWindowAt(int x, int y, out WaylandWindowInfo window)
			{
				window = null;
				var xs = x.ToString(CultureInfo.InvariantCulture);
				var ys = y.ToString(CultureInfo.InvariantCulture);
				var json = RunJson(WindowHelpers
					+ $"var px = {xs}, py = {ys};\n"
					+ "var w = null;\n"
					// KWin 6 has workspace.windowAt(Qt.point, hint); KWin 5 does not.
					+ "try {\n"
					+ "  if (typeof workspace.windowAt === \"function\") {\n"
					+ "    var hits = workspace.windowAt(Qt.point(px, py), 1);\n"
					+ "    if (hits && typeof hits.length !== \"undefined\" && hits.length > 0) w = hits[0];\n"
					+ "    else if (hits) w = hits;\n"
					+ "  }\n"
					+ "} catch (e) { w = null; }\n"
					// Fallback for KWin 5 (no windowAt, and stackingOrder isn't exposed to scripts):
					// gather every real, visible window whose frame contains the point, then prefer:
					// (a) the active window if it's a hit, else (b) the smallest area — innermost
					// windows are usually drawn on top of larger ones that share their region.
					+ "if (!w) {\n"
					+ "  var order = windowListCompat();\n"
					+ "  var hits = [];\n"
					+ "  for (var i = 0; i < order.length; ++i) {\n"
					+ "    var cand = order[i];\n"
					+ "    if (!cand) continue;\n"
					+ "    if (safeBool(cand.deleted) || safeBool(cand.hidden) || safeBool(cand.minimized)) continue;\n"
					+ "    if (safeBool(cand.specialWindow)) continue;\n"
					+ "    if (typeof cand.managed !== \"undefined\" && !safeBool(cand.managed)) continue;\n"
					+ "    var g = safeRead(cand, \"frameGeometry\", safeRead(cand, \"geometry\", null));\n"
					+ "    if (!g || g.width <= 0 || g.height <= 0) continue;\n"
					+ "    if (px < g.x || py < g.y || px >= g.x + g.width || py >= g.y + g.height) continue;\n"
					+ "    if (safeBool(cand.active)) { w = cand; break; }\n"
					+ "    hits.push({ cand: cand, area: g.width * g.height });\n"
					+ "  }\n"
					+ "  if (!w && hits.length > 0) {\n"
					+ "    hits.sort(function(a, b) { return a.area - b.area; });\n"
					+ "    w = hits[0].cand;\n"
					+ "  }\n"
					+ "}\n"
					+ "report({ ok: true, window: windowInfo(w) });\n", "window_at");
				return TryParseSingleWindow(json, out window);
			}

			public bool TryGetWorkArea(out Rectangle area)
			{
				area = Rectangle.Empty;

				var json = RunJson(WorkAreaScript, "workarea");
				if (json.IsNullOrEmpty())
					return false;

				try
				{
					using var doc = JsonDocument.Parse(json);
					var root = doc.RootElement;

					if (!JsonBool(root, "ok") || !root.TryGetProperty("area", out var a) || a.ValueKind != JsonValueKind.Object)
						return false;

					var rect = new Rectangle(JsonInt(a, "x"), JsonInt(a, "y"), JsonInt(a, "width"), JsonInt(a, "height"));
					if (rect.Width <= 0 || rect.Height <= 0)
						return false;

					area = rect;
					return true;
				}
				catch
				{
					return false;
				}
			}

			// clientArea(MaximizeArea, ...) is the area a maximized window gets = the work area (monitor minus
			// panels). activeScreen/currentDesktop are an int+int on KWin 5 and Output+VirtualDesktop objects on
			// KWin 6, but the matching clientArea overload exists on each, so passing them through works on both.
			private const string WorkAreaScript = """
function rd(r){ return r ? { x: Math.round(r.x||0), y: Math.round(r.y||0), width: Math.round(r.width||0), height: Math.round(r.height||0) } : null; }
var area = null;
try { area = workspace.clientArea(KWin.MaximizeArea, workspace.activeScreen, workspace.currentDesktop); } catch (e) { area = null; }
if (!area) { try { area = workspace.clientArea(KWin.PlacementArea, workspace.activeScreen, workspace.currentDesktop); } catch (e) {} }
report({ ok: !!area, area: rd(area) });
""";

			public bool TryActivateWindow(nint handle)
			{
				if (!TryGetCompositorId(handle, out var id))
					return false;

				return RunCommand(WindowHelpers
					+ $"var w = findWindow({JsonSerializer.Serialize(id)});\n"
					+ "if (!w) { report({ ok: false }); return; }\n"
					+ "try { w.minimized = false; } catch (e) {}\n"
					+ "workspace.activeWindow = w;\n"
					+ "try { workspace.raiseWindow(w); } catch (e) {}\n"
					+ "report({ ok: true });\n", "activate");
			}

			public bool TryMoveResizeWindow(nint handle, Rectangle bounds, bool setPosition, bool setSize)
			{
				if (!TryGetCompositorId(handle, out var id))
					return false;

				return RunCommand(WindowHelpers
					+ $"var w = findWindow({JsonSerializer.Serialize(id)});\n"
					+ "if (!w) { report({ ok: false }); return; }\n"
					+ "var g = w.frameGeometry;\n"
					+ $"var nx = {(setPosition ? bounds.X : int.MinValue).ToString(CultureInfo.InvariantCulture)};\n"
					+ $"var ny = {(setPosition ? bounds.Y : int.MinValue).ToString(CultureInfo.InvariantCulture)};\n"
					+ $"var nw = {(setSize ? bounds.Width : int.MinValue).ToString(CultureInfo.InvariantCulture)};\n"
					+ $"var nh = {(setSize ? bounds.Height : int.MinValue).ToString(CultureInfo.InvariantCulture)};\n"
					+ "if (nx === -2147483648) nx = Math.round(g.x);\n"
					+ "if (ny === -2147483648) ny = Math.round(g.y);\n"
					+ "if (nw === -2147483648 || nw <= 0) nw = Math.round(g.width);\n"
					+ "if (nh === -2147483648 || nh <= 0) nh = Math.round(g.height);\n"
					+ "w.frameGeometry = { x: nx, y: ny, width: nw, height: nh };\n"
					+ "report({ ok: true });\n", "move_resize");
			}

			public bool TrySetNoBorder(nint handle, bool noBorder)
			{
				if (!TryGetCompositorId(handle, out var id))
					return false;

				// KWin's per-window noBorder removes the server-side decoration (titlebar) without putting the
				// window into GTK client-side-decoration mode — so, unlike the empty-titlebar CSD trick, it does
				// not make GTK manage (and clobber) the surface input region. That is what lets a click-through
				// overlay be both borderless and input-transparent.
				return RunCommand(WindowHelpers
					+ $"var w = findWindow({JsonSerializer.Serialize(id)});\n"
					+ "if (!w) { report({ ok: false }); return; }\n"
					+ $"try {{ w.noBorder = {(noBorder ? "true" : "false")}; }} catch (e) {{}}\n"
					+ "report({ ok: true });\n", "no_border");
			}

			public bool TrySetWindowState(nint handle, FormWindowState state)
			{
				if (!TryGetCompositorId(handle, out var id))
					return false;

				var body = WindowHelpers
					+ $"var w = findWindow({JsonSerializer.Serialize(id)});\n"
					+ "if (!w) { report({ ok: false }); return; }\n";

				body += state switch
				{
					FormWindowState.Minimized => "w.minimized = true;\n",
					FormWindowState.Maximized => "w.minimized = false;\nif (typeof w.setMaximize === \"function\") w.setMaximize(true, true);\n",
					_ => "w.minimized = false;\nif (typeof w.setMaximize === \"function\") w.setMaximize(false, false);\n"
				};

				body += "report({ ok: true });\n";
				return RunCommand(body, "state");
			}

			public bool TrySetAlwaysOnTop(nint handle, bool onTop)
			{
				if (!TryGetCompositorId(handle, out var id))
					return false;

				return RunCommand(WindowHelpers
					+ $"var w = findWindow({JsonSerializer.Serialize(id)});\n"
					+ "if (!w) { report({ ok: false }); return; }\n"
					+ $"w.keepAbove = {(onTop ? "true" : "false")};\n"
					+ "report({ ok: true });\n", "keepabove");
			}

			public bool TrySetZOrder(nint handle, ZOrder z)
			{
				if (z != ZOrder.Top || !TryGetCompositorId(handle, out var id))
					return false;

				return RunCommand(WindowHelpers
					+ $"var w = findWindow({JsonSerializer.Serialize(id)});\n"
					+ "if (!w) { report({ ok: false }); return; }\n"
					+ "var ok = false;\n"
					+ "try {\n"
					+ "  if (typeof workspace.raiseWindow === \"function\") { workspace.raiseWindow(w); ok = true; }\n"
					+ "} catch (e) { ok = false; }\n"
					+ "report({ ok: ok });\n", "raise");
			}

			public bool TrySetTransparency(nint handle, object alpha)
			{
				if (!TryGetCompositorId(handle, out var id))
					return false;

				var opacity = AlphaToOpacity(alpha).ToString("R", CultureInfo.InvariantCulture);

				return RunCommand(WindowHelpers
					+ $"var w = findWindow({JsonSerializer.Serialize(id)});\n"
					+ "if (!w) { report({ ok: false }); return; }\n"
					+ "var ok = false;\n"
					+ "try {\n"
					+ "  if (typeof w.opacity !== \"undefined\") { w.opacity = " + opacity + "; ok = true; }\n"
					+ "} catch (e) { ok = false; }\n"
					+ "report({ ok: ok });\n", "opacity");
			}

			public bool TryCloseWindow(nint handle)
			{
				if (!TryGetCompositorId(handle, out var id))
					return false;

				return RunCommand(WindowHelpers
					+ $"var w = findWindow({JsonSerializer.Serialize(id)});\n"
					+ "if (!w) { report({ ok: false }); return; }\n"
					+ "w.closeWindow();\n"
					+ "report({ ok: true });\n", "close");
			}

			public bool SupportsWindowEvents => true;

			public IDisposable SubscribeWindowEvents(Action<WaylandWindowEvent> sink)
			{
				if (sink == null)
					return null;

				var token = Guid.NewGuid().ToString("N");
				var script = WindowHelpers + "\n" + EventScriptBody;

				void OnEvent(string type, string json)
				{
					var kind = MapEventKind(type);

					if (kind == null || json.IsNullOrEmpty())
						return;

					try
					{
						using var doc = JsonDocument.Parse(json);

						if (TryParseWindow(doc.RootElement, out var info) && info.Handle != 0)
							sink(new WaylandWindowEvent(kind.Value, info.Handle) { Bounds = info.FrameGeometry });
					}
					catch
					{
					}
				}

				IDisposable sub = null;

				try
				{
					sub = Task.Run(() => KWinDBusBridge.StartEventScriptAsync(token, script, OnEvent)).GetAwaiter().GetResult();
				}
				catch
				{
				}

				// If the persistent script couldn't be loaded (KWin scripting unavailable), degrade to polling
				// rather than silently delivering nothing.
				return sub ?? new WaylandPollingEventSource(this, sink);
			}

			private static WaylandWindowEventKind? MapEventKind(string type) => type switch
			{
				"create"   => WaylandWindowEventKind.Created,
				"close"    => WaylandWindowEventKind.Closed,
				"active"   => WaylandWindowEventKind.Activated,
				"title"    => WaylandWindowEventKind.TitleChanged,
				"minimize" => WaylandWindowEventKind.Minimized,
				"restore"  => WaylandWindowEventKind.Restored,
				"move"     => WaylandWindowEventKind.MoveResized,
				_          => null
			};

			// Persistent KWin script body (prefixed with WindowHelpers): connects workspace + per-window signals and
			// pushes events via the harness-provided emit(type, value). Existing windows get their per-window signals
			// hooked without re-emitting "create" (they already exist). KWin 6 signal names are tried first, then the
			// KWin 5 client* equivalents.
			private const string EventScriptBody = """
function connectFirst(names, handler) {
  for (var i = 0; i < names.length; ++i) {
    try {
      var sig = workspace[names[i]];
      if (sig && typeof sig.connect === "function") { sig.connect(handler); return true; }
    } catch (e) {}
  }
  return false;
}
function tryConnect(obj, name, handler) {
  try {
    var sig = obj[name];
    if (sig && typeof sig.connect === "function") { sig.connect(handler); return true; }
  } catch (e) {}
  return false;
}
function trackWindow(w) {
  if (!w) return;
  tryConnect(w, "captionChanged", function() { var info = windowInfo(w, true); if (info) emit("title", info); });
  tryConnect(w, "minimizedChanged", function() {
    var info = windowInfo(w, true);
    if (info) emit(safeBool(w.minimized) ? "minimize" : "restore", info);
  });
  if (!tryConnect(w, "frameGeometryChanged", function() { var info = windowInfo(w, true); if (info) emit("move", info); }))
    tryConnect(w, "geometryChanged", function() { var info = windowInfo(w, true); if (info) emit("move", info); });
}
function onAdded(w) {
  var info = windowInfo(w);
  if (!info) return;
  trackWindow(w);
  emit("create", info);
}
function onRemoved(w) {
  if (!w) return;
  var id = windowId(w);
  if (id) emit("close", { id: id });
}
function onActivated(w) {
  if (!w) return;
  var info = windowInfo(w, true);
  if (info) emit("active", info);
}
connectFirst(["windowAdded", "clientAdded"], onAdded);
connectFirst(["windowRemoved", "clientRemoved"], onRemoved);
connectFirst(["windowActivated", "clientActivated"], onActivated);
var __order = windowListCompat();
for (var __i = 0; __i < __order.length; ++__i) {
  if (windowInfo(__order[__i])) trackWindow(__order[__i]);
}
""";

			private static string BuildListScript(bool includeHidden)
				=> WindowHelpers
				   + "var result = [];\n"
				   + "var order = windowListCompat();\n"
				   + "for (var i = 0; i < order.length; ++i) {\n"
				   + "  var info = windowInfo(order[i]);\n"
				   + "  if (!info) continue;\n"
				   + (includeHidden ? "" : "  if (!info.visible) continue;\n")
				   + "  result.push(info);\n"
				   + "}\n"
				   + "report({ ok: true, windows: result });\n";

			private const string WindowHelpers = """
function safeRead(object, name, fallback) {
  try {
    if (!object || typeof object[name] === "undefined" || object[name] === null) return fallback;
    return object[name];
  } catch (e) {
    return fallback;
  }
}
function safeString(value) {
  if (value === null || value === undefined) return "";
  return String(value);
}
function safeBool(value) {
  try { return !!value; } catch (e) { return false; }
}
function readRect(rect) {
  if (!rect) return { x: 0, y: 0, width: 0, height: 0 };
  return {
    x: Math.round(rect.x || 0),
    y: Math.round(rect.y || 0),
    width: Math.round(rect.width || 0),
    height: Math.round(rect.height || 0)
  };
}
function windowListCompat() {
  try {
    if (workspace.stackingOrder && workspace.stackingOrder.length !== undefined) return workspace.stackingOrder;
  } catch (e) {}
  try {
    if (typeof workspace.windowList === "function") return workspace.windowList();
  } catch (e) {}
  try {
    if (typeof workspace.clientList === "function") return workspace.clientList();
  } catch (e) {}
  try {
    if (workspace.clientList && workspace.clientList.length !== undefined) return workspace.clientList;
  } catch (e) {}
  return [];
}
function activeWindowCompat() {
  try {
    if (workspace.activeWindow) return workspace.activeWindow;
  } catch (e) {}
  try {
    if (workspace.activeClient) return workspace.activeClient;
  } catch (e) {}
  return null;
}
function firstNonEmpty() {
  for (var i = 0; i < arguments.length; ++i) {
    var value = safeString(arguments[i]);
    if (value.length > 0) return value;
  }
  return "";
}
function windowId(w) {
  try {
    var id = safeString(w.internalId);
    if (id.length > 0) return id;
  } catch (e) {}
  try {
    var windowId = safeString(w.windowId);
    if (windowId.length > 0) return "windowId:" + windowId;
  } catch (e) {}
  var frame = readRect(safeRead(w, "frameGeometry", safeRead(w, "geometry", null)));
  var pid = safeString(safeRead(w, "pid", 0));
  var cls = firstNonEmpty(safeRead(w, "resourceClass", ""), safeRead(w, "desktopFileName", ""), safeRead(w, "resourceName", ""));
  var caption = safeString(safeRead(w, "caption", ""));
  if (pid.length > 0 || cls.length > 0 || caption.length > 0) {
    return "fallback:" + pid + "|" + cls + "|" + caption + "|" + frame.x + "," + frame.y + "," + frame.width + "," + frame.height;
  }
  return "";
}
function isMaximized(w) {
  try {
    if (typeof w.maximized !== "undefined") return !!w.maximized;
  } catch (e) {}
  // KWin 5 exposes maximizeMode (0=Restore, 1=Vertical, 2=Horizontal, 3=Full) instead of a maximized bool.
  try {
    if (typeof w.maximizeMode !== "undefined") return Number(w.maximizeMode) === 3;
  } catch (e) {}
  try {
    var area = workspace.clientArea(KWin.MaximizeArea, w);
    var g = w.frameGeometry;
    return Math.round(g.x) === Math.round(area.x)
      && Math.round(g.y) === Math.round(area.y)
      && Math.round(g.width) === Math.round(area.width)
      && Math.round(g.height) === Math.round(area.height);
  } catch (e) {}
  return false;
}
function opacityToAlpha(value) {
  var opacity = Number(value);
  if (!isFinite(opacity)) opacity = 1;
  if (opacity < 0) opacity = 0;
  if (opacity > 1) opacity = 1;
  return Math.round(opacity * 255);
}
function windowInfo(w, includeSpecial) {
  if (!w) return null;
  try {
    if (safeBool(w.deleted)) return null;
    if (!includeSpecial) {
    if (typeof w.managed !== "undefined" && !safeBool(w.managed)) return null;
      if (safeBool(w.specialWindow)) return null;
    }
    var id = windowId(w);
    if (!id) return null;
    var frame = readRect(safeRead(w, "frameGeometry", safeRead(w, "geometry", null)));
    // clientGeometry isn't exposed to scripts on every KWin build; when absent it would collapse to the frame.
    // Try it first, then the clientPos (inset relative to the frame) + clientSize pair, then fall back to frame.
    var client = readRect(safeRead(w, "clientGeometry", null));
    if (!client || client.width === 0) {
      var cp = safeRead(w, "clientPos", null);
      var cs = safeRead(w, "clientSize", null);
      if (cp && cs && (cs.width || 0) > 0)
        client = { x: frame.x + Math.round(cp.x || 0), y: frame.y + Math.round(cp.y || 0),
                   width: Math.round(cs.width || 0), height: Math.round(cs.height || 0) };
      else
        client = frame;
    }
    return {
      id: id,
      title: safeString(safeRead(w, "caption", "")),
      appId: firstNonEmpty(safeRead(w, "resourceClass", ""), safeRead(w, "desktopFileName", ""), safeRead(w, "resourceName", "")),
      pid: Math.round(safeRead(w, "pid", 0) || 0),
      frame: frame,
      client: client,
      active: safeBool(w.active),
      minimized: safeBool(w.minimized),
      maximized: isMaximized(w),
      visible: !safeBool(w.hidden),
      alwaysOnTop: safeBool(w.keepAbove),
      decorated: !safeBool(w.noBorder),
      transparency: opacityToAlpha(safeRead(w, "opacity", 1))
    };
  } catch (e) {
    return null;
  }
}
function findWindow(id) {
  var order = windowListCompat();
  for (var i = 0; i < order.length; ++i) {
    if (windowId(order[i]) === id) return order[i];
  }
  var active = activeWindowCompat();
  if (active && windowId(active) === id) return active;
  return null;
}
""";

			private string RunJson(string scriptBody, string requestPrefix)
			{
				try
				{
					return Task.Run(() => KWinDBusBridge.RunJsonQueryAsync(scriptBody, requestPrefix)).GetAwaiter().GetResult();
				}
				catch
				{
					return null;
				}
			}

			private bool RunCommand(string scriptBody, string requestPrefix)
			{
				try
				{
					return Task.Run(() => KWinDBusBridge.RunCommandAsync(scriptBody, requestPrefix)).GetAwaiter().GetResult();
				}
				catch
				{
					return false;
				}
			}

			private bool TryParseSingleWindow(string json, out WaylandWindowInfo window)
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

			internal bool TryParseWindow(JsonElement item, out WaylandWindowInfo info)
			{
				info = null;

				if (!JsonString(item, "id", out var id) || id.IsNullOrEmpty())
					return false;

				info = new WaylandWindowInfo(
					handle: GetOrCreateHandle(id),
					compositorId: id,
					title: JsonString(item, "title"),
					appId: JsonString(item, "appId"),
					pid: JsonLong(item, "pid"),
					frameGeometry: JsonRectangle(item, "frame"),
					clientGeometry: JsonRectangle(item, "client"),
					active: JsonBool(item, "active"),
					minimized: JsonBool(item, "minimized"),
					maximized: JsonBool(item, "maximized"),
					visible: JsonBool(item, "visible"),
					alwaysOnTop: JsonBool(item, "alwaysOnTop"),
					decorated: !item.TryGetProperty("decorated", out _) || JsonBool(item, "decorated"),
					transparency: item.TryGetProperty("transparency", out _) ? JsonLong(item, "transparency") : 0xFFL);
				return true;
			}

			private static double AlphaToOpacity(object alpha)
			{
				if (alpha is string s && s.Equals("off", StringComparison.OrdinalIgnoreCase))
					return 1.0;

				return Math.Clamp((int)alpha.Al(), 0, 255) / 255.0;
			}

			private nint GetOrCreateHandle(string id)
			{
				lock (handleLock)
				{
					if (handlesById.TryGetValue(id, out var handle))
						return handle;

					handle = DeriveHandle(id);
					handlesById[id] = handle;
					idsByHandle[handle] = id;
					return handle;
				}
			}

			// Derive a stable 64-bit handle from the compositor's window id (a UUID string for
			// KWin). Deterministic so the same window keeps the same handle across separate
			// Keysharp runs in the same Plasma session — `ahk_id <handle>` round-trips between
			// scripts. Bit 62 is set so the result is positive and lives above the 32-bit X11
			// XID range, eliminating collisions with the X11 fallback path. Non-UUID ids fall
			// back to a string hash (fallback-id windows still get a stable handle).
			private static nint DeriveHandle(string id)
			{
				long folded;

				if (Guid.TryParse(id, out var guid))
				{
					var bytes = guid.ToByteArray();
					folded = BitConverter.ToInt64(bytes, 0) ^ BitConverter.ToInt64(bytes, 8);
				}
				else
				{
					// FNV-1a on the id bytes — only used for non-UUID fallback ids.
					folded = unchecked((long)0xcbf29ce484222325UL);

					foreach (var ch in id)
						folded = unchecked((folded ^ ch) * 0x100000001b3L);
				}

				return new nint((folded & 0x3FFF_FFFF_FFFF_FFFFL) | 0x4000_0000_0000_0000L);
			}

			private bool TryGetCompositorId(nint handle, out string id)
			{
				lock (handleLock)
					return idsByHandle.TryGetValue(handle, out id);
			}

			/// <summary>
			/// The KWin internalId UUID for a window handle, in the canonical <c>{…}</c> form that
			/// org.kde.KWin.ScreenShot2.CaptureWindow expects (matching QUuid::toString()). False for
			/// fallback (non-UUID "windowId:") ids or unknown handles — those have no capturable internalId.
			/// </summary>
			internal bool TryGetWindowUuid(nint handle, out string uuid)
			{
				uuid = null;

				if (TryGetCompositorId(handle, out var id) && Guid.TryParse(id, out var guid))
				{
					uuid = guid.ToString("B");
					return true;
				}

				return false;
			}

			private static bool JsonBool(JsonElement element, string property)
				=> element.TryGetProperty(property, out var value) && JsonBool(value);

			private static bool JsonBool(JsonElement value)
				=> value.ValueKind == JsonValueKind.True
				   || (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var i) && i != 0)
				   || (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var b) && b);

			private static string JsonString(JsonElement element, string property)
				=> JsonString(element, property, out var value) ? value : string.Empty;

			private static bool JsonString(JsonElement element, string property, out string result)
			{
				result = string.Empty;

				if (!element.TryGetProperty(property, out var value))
					return false;

				result = value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString();
				return true;
			}

			private static long JsonLong(JsonElement element, string property)
			{
				if (!element.TryGetProperty(property, out var value))
					return 0L;

				if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var l))
					return l;

				return value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out l) ? l : 0L;
			}

			private static Rectangle JsonRectangle(JsonElement element, string property)
			{
				if (!element.TryGetProperty(property, out var rect) || rect.ValueKind != JsonValueKind.Object)
					return Rectangle.Empty;

				return new Rectangle(JsonInt(rect, "x"), JsonInt(rect, "y"), JsonInt(rect, "width"), JsonInt(rect, "height"));
			}

			private static int JsonInt(JsonElement element, string property)
			{
				if (!element.TryGetProperty(property, out var value))
					return 0;

				if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var i))
					return i;

				return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out i) ? i : 0;
			}
		}

		/// <summary>
		/// Wayland backend for GNOME Shell. Communicates with the bundled
		/// keysharp@keysharp.io GNOME Shell extension via D-Bus. The extension
		/// runs inside the compositor process and therefore has access to all
		/// Mutter window state that is normally inaccessible to foreign clients.
		/// </summary>
		internal sealed class GnomeBackend : IWaylandBackend
		{
			// Bit 61 set marks a handle as originating from this backend.
			// Bit 62 is already used by KWinBackend; X11 XIDs are 32-bit; so
			// bit 61 gives a collision-free range for GNOME stable_sequences.
			private const long GnomeBit = unchecked((long)0x2000_0000_0000_0000L);

			public string Name => "GNOME";

			public bool SupportsMouse => true;

			internal static bool IsAvailable()
				=> DesktopMatches("GNOME")
				   || DesktopMatches("ubuntu")
				   || EnvironmentContains("DESKTOP_SESSION", "gnome")
				   || EnvironmentContains("XDG_SESSION_DESKTOP", "gnome")
				   || EnvironmentContains("XDG_CURRENT_DESKTOP", "gnome");

			public bool TryGetCursorPos(out int x, out int y)
				=> GnomeShellBridge.QueryCursorPosition(out x, out y);

			public bool TryGetWorkArea(out Rectangle area)
				=> GnomeShellBridge.QueryWorkArea(out area);

			public bool TryListWindows(bool includeHidden, out IReadOnlyList<WaylandWindowInfo> windows)
			{
				windows = [];
				var json = GnomeShellBridge.QueryWindowList(includeHidden);

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
			{
				window = null;
				var json = GnomeShellBridge.QueryActiveWindow();
				return TryParseSingleWindow(json, out window);
			}

			public bool TryGetWindow(nint handle, out WaylandWindowInfo window)
			{
				window = null;

				if (!TryHandleToSeq(handle, out var seq))
					return false;

				// Fetch the full list and find by handle — the extension has no
				// single-window-by-id method, but the list is cheap.
				if (!TryListWindows(true, out var all))
					return false;

				window = all.FirstOrDefault(w => w.Handle == handle);
				return window != null;
			}

			public bool TryGetWindowAt(int x, int y, out WaylandWindowInfo window)
			{
				window = null;

				// Walk the window list top-to-bottom (list is bottom-to-top order)
				// and return the first window whose frame contains the point.
				if (!TryListWindows(false, out var all))
					return false;

				for (var i = all.Count - 1; i >= 0; i--)
				{
					var w = all[i];

					if (w.FrameGeometry.Contains(x, y))
					{
						window = w;
						return true;
					}
				}

				return false;
			}

			public bool TryActivateWindow(nint handle)
			{
				if (!TryHandleToSeq(handle, out var seq))
					return false;

				return GnomeShellBridge.SendFocusWindow(seq);
			}

			public bool TryMoveResizeWindow(nint handle, Rectangle bounds, bool setPosition, bool setSize)
			{
				if (!TryHandleToSeq(handle, out var seq))
					return false;

				return GnomeShellBridge.SendMoveResize(
					seq,
					setPosition ? bounds.X : int.MinValue,
					setPosition ? bounds.Y : int.MinValue,
					setSize && bounds.Width > 0 ? bounds.Width : 0,
					setSize && bounds.Height > 0 ? bounds.Height : 0);
			}

			public bool TrySetWindowState(nint handle, FormWindowState state)
			{
				if (!TryHandleToSeq(handle, out var seq))
					return false;

				return GnomeShellBridge.SendSetWindowState(seq, (int)state);
			}

			public bool TrySetNoBorder(nint handle, bool noBorder)
			{
				if (!TryHandleToSeq(handle, out var seq))
					return false;

				// noBorder == hide titlebar == decorated false. (On GNOME this is a no-op for our own
				// overlays — Mutter doesn't add server-side decorations, so decorated=false already yields
				// no titlebar; the extension only acts on XWayland windows.)
				return GnomeShellBridge.SendSetWindowDecorated(seq, !noBorder);
			}

			public bool TrySetAlwaysOnTop(nint handle, bool onTop)
			{
				if (!TryHandleToSeq(handle, out var seq))
					return false;

				return GnomeShellBridge.SendSetWindowAbove(seq, onTop);
			}

			public bool TryCloseWindow(nint handle)
			{
				if (!TryHandleToSeq(handle, out var seq))
					return false;

				return GnomeShellBridge.SendCloseWindow(seq);
			}

			// GNOME has no wlr-layer-shell, so the Highlight builtin can't make a click-through layer
			// surface itself; the shell extension draws the outline inside the compositor instead. The id
			// is a plain caller-chosen token (not a window handle), so no TryHandleToSeq translation here.
			public bool SupportsHighlight => true;

			public bool TryShowHighlight(uint id, int x, int y, int width, int height, string color, int thickness)
				=> GnomeShellBridge.SendShowHighlight(id, x, y, width, height, color, thickness);

			public bool TryHideHighlight(uint id)
				=> GnomeShellBridge.SendHideHighlight(id);

			public bool SupportsWindowEvents => true;

			public IDisposable SubscribeWindowEvents(Action<WaylandWindowEvent> sink)
			{
				if (sink == null)
					return null;

				void OnEvent(string type, string json)
				{
					var kind = MapEventKind(type);

					if (kind == null || json.IsNullOrEmpty())
						return;

					try
					{
						using var doc = JsonDocument.Parse(json);

						if (TryParseWindow(doc.RootElement, out var info) && info.Handle != 0)
							sink(new WaylandWindowEvent(kind.Value, info.Handle) { Bounds = info.FrameGeometry });
					}
					catch
					{
					}
				}

				// Fall back to polling if the extension's signal isn't available (extension missing/old).
				return GnomeShellBridge.WatchWindowEvent(OnEvent) ?? new WaylandPollingEventSource(this, sink);
			}

			private static WaylandWindowEventKind? MapEventKind(string type) => type switch
			{
				"create"   => WaylandWindowEventKind.Created,
				"close"    => WaylandWindowEventKind.Closed,
				"active"   => WaylandWindowEventKind.Activated,
				"title"    => WaylandWindowEventKind.TitleChanged,
				"minimize" => WaylandWindowEventKind.Minimized,
				"restore"  => WaylandWindowEventKind.Restored,
				"move"     => WaylandWindowEventKind.MoveResized,
				_          => null
			};

			public bool TrySendMouseMoveAbsolute(int x, int y)
				=> GnomeShellBridge.SendMouseMoveAbsolute(x, y);

			public bool TrySendMouseMoveRelative(int dx, int dy)
				=> GnomeShellBridge.SendMouseMoveRelative(dx, dy);

			public bool TrySendMouseButton(uint button, bool pressed)
				=> GnomeShellBridge.SendMouseButton(button, pressed);

			public bool TrySendMouseScroll(int delta, bool vertical)
				=> GnomeShellBridge.SendMouseScroll(delta, vertical);

			// ---- helpers ------------------------------------------------

			private static bool TryParseWindow(JsonElement item, out WaylandWindowInfo info)
			{
				info = null;

				if (!JsonString(item, "id", out var id) || id.IsNullOrEmpty())
					return false;

				if (!ulong.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seq))
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
					visible: JsonBool(item, "visible"),
					alwaysOnTop: JsonBool(item, "alwaysOnTop"),
					decorated: !item.TryGetProperty("decorated", out _) || JsonBool(item, "decorated"));
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

			// Encode a stable_sequence as an nint handle. Bit 61 marks it
			// as a GNOME handle, keeping it above the 32-bit X11 XID range
			// and separate from KWin's bit-62 handles.
			private static nint SeqToHandle(ulong seq)
				=> new nint((long)((seq & 0xFFFF_FFFF) | (ulong)GnomeBit));

			private static bool TryHandleToSeq(nint handle, out ulong seq)
			{
				var h = handle.ToInt64();

				if ((h & unchecked((long)0x6000_0000_0000_0000L)) == GnomeBit)
				{
					seq = (ulong)(h & 0xFFFF_FFFF);
					return true;
				}

				seq = 0;
				return false;
			}

			/// <summary>
			/// The bare compositor stable_sequence for a window handle, stripping the GnomeBit marker that
			/// <see cref="SeqToHandle"/> applies. The extension matches windows by raw stable_sequence, so
			/// the marker must be removed before capture. False for non-GNOME handles. Mirrors
			/// <see cref="KWinBackend.TryGetWindowUuid"/>.
			/// </summary>
			internal bool TryGetWindowSeq(nint handle, out ulong seq) => TryHandleToSeq(handle, out seq);

			private static bool JsonBool(JsonElement element, string property)
				=> element.TryGetProperty(property, out var value) && JsonBoolValue(value);

			private static bool JsonBoolValue(JsonElement value)
				=> value.ValueKind == JsonValueKind.True
				   || (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var i) && i != 0)
				   || (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var b) && b);

			private static string JsonString(JsonElement element, string property)
				=> JsonString(element, property, out var value) ? value : string.Empty;

			private static bool JsonString(JsonElement element, string property, out string result)
			{
				result = string.Empty;

				if (!element.TryGetProperty(property, out var value))
					return false;

				result = value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString();
				return true;
			}

			private static long JsonLong(JsonElement element, string property)
			{
				if (!element.TryGetProperty(property, out var value))
					return 0L;

				if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var l))
					return l;

				return value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out l) ? l : 0L;
			}

			private static Rectangle JsonRectangle(JsonElement element, string property)
			{
				if (!element.TryGetProperty(property, out var rect) || rect.ValueKind != JsonValueKind.Object)
					return Rectangle.Empty;

				return new Rectangle(JsonInt(rect, "x"), JsonInt(rect, "y"), JsonInt(rect, "width"), JsonInt(rect, "height"));
			}

			private static int JsonInt(JsonElement element, string property)
			{
				if (!element.TryGetProperty(property, out var value))
					return 0;

				if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var i))
					return i;

				return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out i) ? i : 0;
			}
		}

		internal sealed class SwayBackend : IWaylandBackend
		{
			public string Name => "sway";

			internal static bool IsAvailable() => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SWAYSOCK"));

			public bool TryGetCursorPos(out int x, out int y)
			{
				x = 0;
				y = 0;
				// TODO: sway's IPC over $SWAYSOCK doesn't expose cursor pos directly; we'd
				// need to combine GET_TREE (for output rects) with the focused container's
				// rect, or rely on a future protocol addition. May not be implementable
				// without sway changes.
				return false;
			}
		}

		internal sealed class HyprlandBackend : IWaylandBackend
		{
			public string Name => "Hyprland";

			internal static bool IsAvailable() => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE"));

			public bool TryGetCursorPos(out int x, out int y)
			{
				x = 0;
				y = 0;

				var sig = Environment.GetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE");

				if (string.IsNullOrEmpty(sig))
					return false;

				// Hyprland's command socket reads a single request, writes the reply, then closes.
				// Newer builds place it under $XDG_RUNTIME_DIR/hypr/<sig>/; older ones used /tmp/hypr/<sig>/.
				var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");

				if (!string.IsNullOrEmpty(runtimeDir)
						&& TryQueryCursorPos(Path.Combine(runtimeDir, "hypr", sig, ".socket.sock"), out x, out y))
					return true;

				return TryQueryCursorPos(Path.Combine("/tmp", "hypr", sig, ".socket.sock"), out x, out y);
			}

			// Connects to the given Hyprland command socket, sends "cursorpos", and parses the
			// "X, Y" reply. Returns false (with x/y zeroed) on any failure so the caller can fall back.
			private static bool TryQueryCursorPos(string socketPath, out int x, out int y)
			{
				x = 0;
				y = 0;

				if (!File.Exists(socketPath))
					return false;

				try
				{
					using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
					{
						ReceiveTimeout = 250,
						SendTimeout = 250
					};
					socket.Connect(new UnixDomainSocketEndPoint(socketPath));
					_ = socket.Send(Encoding.ASCII.GetBytes("cursorpos"));

					var sb = new StringBuilder();
					var buffer = new byte[256];
					int read;

					while ((read = socket.Receive(buffer)) > 0)
						_ = sb.Append(Encoding.ASCII.GetString(buffer, 0, read));

					var response = sb.ToString();
					var comma = response.IndexOf(',');

					if (comma >= 0
							&& int.TryParse(response.AsSpan(0, comma).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out x)
							&& int.TryParse(response.AsSpan(comma + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out y))
						return true;
				}
				catch
				{
					// Socket gone, permission denied, malformed reply, etc. — fall through to failure.
				}

				x = 0;
				y = 0;
				return false;
			}
		}

		internal sealed class CosmicBackend : IWaylandBackend
		{
			public string Name => "COSMIC";

			internal static bool IsAvailable() => DesktopMatches("COSMIC");

			public bool TryGetCursorPos(out int x, out int y)
			{
				x = 0;
				y = 0;
				// TODO: bind zcosmic_toplevel_info_v1 (which already includes geometry) and
				// — separately — find or push for a cursor-position Wayland extension on
				// COSMIC. As of writing there isn't a standard cursor-pos protocol there
				// either; the toplevel-info extension exists but doesn't carry the cursor.
				return false;
			}
		}
	}
}
#endif
