#if LINUX
namespace Keysharp.Internals.Window.Linux.Wayland
{
	/// <summary>
	/// Singleton chooser for <see cref="IWaylandBackend"/>. Probes the running
	/// compositor on first access and caches the result. Override probing with
	/// <c>KEYSHARP_WAYLAND_BACKEND=auto|kwin|sway|hyprland|cosmic|none</c>.
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

			internal static bool IsAvailable()
				=> DesktopMatches("KDE")
				   || EnvironmentContains("DESKTOP_SESSION", "kde")
				   || EnvironmentContains("DESKTOP_SESSION", "plasma")
				   || EnvironmentContains("XDG_SESSION_DESKTOP", "kde")
				   || EnvironmentContains("XDG_SESSION_DESKTOP", "plasma")
				   || string.Equals(Environment.GetEnvironmentVariable("KDE_FULL_SESSION"), "true", StringComparison.OrdinalIgnoreCase)
				   || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KDE_SESSION_VERSION"));

			public bool TryGetCursorPos(out int x, out int y)
				=> KWinDBusBridge.QueryCursorPosition(out x, out y);

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
    var client = readRect(safeRead(w, "clientGeometry", safeRead(w, "geometry", null)));
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
      visible: !safeBool(w.hidden)
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

				info = new WaylandWindowInfo
				{
					Handle = GetOrCreateHandle(id),
					CompositorId = id,
					Title = JsonString(item, "title"),
					AppId = JsonString(item, "appId"),
					PID = JsonLong(item, "pid"),
					FrameGeometry = JsonRectangle(item, "frame"),
					ClientGeometry = JsonRectangle(item, "client"),
					Active = JsonBool(item, "active"),
					Minimized = JsonBool(item, "minimized"),
					Maximized = JsonBool(item, "maximized"),
					Visible = JsonBool(item, "visible")
				};
				return true;
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
				// TODO: open $XDG_RUNTIME_DIR/hypr/$HYPRLAND_INSTANCE_SIGNATURE/.socket.sock,
				// send the literal request "cursorpos", read "X, Y" back. Trivial — would
				// be ~30 lines of socket code.
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
