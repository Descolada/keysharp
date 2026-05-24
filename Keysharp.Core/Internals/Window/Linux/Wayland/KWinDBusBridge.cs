using Tmds.DBus;

#if LINUX
namespace Keysharp.Internals.Window.Linux.Wayland
{
	// These interfaces must be public because Tmds.DBus generates proxy classes in a
	// separate dynamic assembly (Tmds.DBus.Emit) and that assembly cannot implement
	// internal interfaces.
#pragma warning disable IDE1006 // D-Bus member names are case-sensitive.
	[DBusInterface("org.kde.kwin.Scripting")]
	public interface IKWinScripting : IDBusObject
	{
		Task<int> loadScriptAsync(string filePath, string pluginName);
		Task<bool> unloadScriptAsync(string pluginName);
	}

	[DBusInterface("org.kde.kwin.Script")]
	public interface IKWinScript : IDBusObject
	{
		Task runAsync();
	}

	[DBusInterface("org.keysharp.KWinBridge")]
	public interface IKWinBridge : IDBusObject
	{
		Task ReportJsonAsync(string requestId, string json);
	}
#pragma warning restore IDE1006


	/// <summary>
	/// D-Bus bridge to KWin's scripting subsystem. KWin exposes compositor internals to
	/// scripts, so Keysharp loads small one-shot scripts and receives their results through a
	/// process-local D-Bus service. The session-bus connection and bridge service are opened
	/// once and reused for every request.
	/// </summary>
	internal static class KWinDBusBridge
	{
		private const int QueryTimeoutMs = 2000;
		private static readonly object initLock = new();
		private static readonly SemaphoreSlim initSemaphore = new(1, 1);
		private static readonly string bridgeServiceName = $"org.keysharp.KWinBridge_{Environment.ProcessId}";
		internal static readonly ObjectPath BridgeObjectPath = new("/keysharp/kwin");

		private static Connection connection;
		private static KWinBridgeService bridgeService;
		private static IKWinScripting scriptingProxy;
		private static bool initFailed;

		// KWin 6 uses "/Scripting/Script{id}" as the per-script object path; KWin 5
		// uses just "/{id}". We don't know which we're on until the first run fails;
		// cache the format once the right one is identified so we don't pay the
		// failed-call cost on every query.
		private static string scriptPathFormat;

		internal static bool QueryCursorPosition(out int x, out int y)
		{
			x = 0;
			y = 0;

			try
			{
				var json = Task.Run(() => RunJsonQueryAsync(
					"var pos = workspace.cursorPos;\n"
					+ "report({ ok: true, x: Math.round(pos.x), y: Math.round(pos.y) });\n",
					"cursor")).GetAwaiter().GetResult();

				if (json.IsNullOrEmpty())
					return false;

				using var doc = JsonDocument.Parse(json);
				var root = doc.RootElement;

				if (!JsonBool(root, "ok") || !JsonInt(root, "x", out x) || !JsonInt(root, "y", out y))
					return false;

				return true;
			}
			catch
			{
				return false;
			}
		}

		internal static async Task<string> RunJsonQueryAsync(string scriptBody, string requestPrefix)
		{
			if (!await EnsureConnectedAsync().ConfigureAwait(false))
				return null;

			var requestId = Guid.NewGuid().ToString("N");
			var pluginName = $"keysharp_{SanitizePrefix(requestPrefix)}_{requestId}";
			string scriptPath = null;

			try
			{
				scriptPath = WriteScript(requestId, pluginName, scriptBody);
				var awaiterTask = bridgeService.WaitForJsonAsync(requestId, QueryTimeoutMs);
				int scriptId;

				try
				{
					scriptId = await scriptingProxy.loadScriptAsync(scriptPath, pluginName).ConfigureAwait(false);
				}
				catch
				{
					return null;
				}

				if (!await TryRunScriptAsync(scriptId).ConfigureAwait(false))
				{
					await TryUnloadScript(pluginName).ConfigureAwait(false);
					return null;
				}

				var (ok, json) = await awaiterTask.ConfigureAwait(false);
				await TryUnloadScript(pluginName).ConfigureAwait(false);
				return ok ? json : null;
			}
			catch
			{
				return null;
			}
			finally
			{
				if (scriptPath != null)
				{
					try { File.Delete(scriptPath); }
					catch { }
				}
			}
		}

		internal static async Task<bool> RunCommandAsync(string scriptBody, string requestPrefix)
		{
			try
			{
				var json = await RunJsonQueryAsync(scriptBody, requestPrefix).ConfigureAwait(false);

				if (json.IsNullOrEmpty())
					return false;

				using var doc = JsonDocument.Parse(json);
				return JsonBool(doc.RootElement, "ok");
			}
			catch
			{
				return false;
			}
		}

		private static async Task<bool> TryRunScriptAsync(int scriptId)
		{
			var cachedFormat = scriptPathFormat;

			if (cachedFormat != null)
				return await TryRunWithFormatAsync(cachedFormat, scriptId).ConfigureAwait(false);

			// First call on this process: probe KWin 6 path, then KWin 5 fallback.
			// Whichever responds without an UnknownObject error is the one we keep.
			foreach (var format in new[] { "/Scripting/Script{0}", "/{0}" })
			{
				if (await TryRunWithFormatAsync(format, scriptId).ConfigureAwait(false))
				{
					scriptPathFormat = format;
					return true;
				}
			}

			return false;
		}

		private static async Task<bool> TryRunWithFormatAsync(string format, int scriptId)
		{
			try
			{
				var path = string.Format(CultureInfo.InvariantCulture, format, scriptId);
				var proxy = connection.CreateProxy<IKWinScript>("org.kde.KWin", new ObjectPath(path));
				await proxy.runAsync().ConfigureAwait(false);
				return true;
			}
			catch
			{
				return false;
			}
		}

		private static async Task TryUnloadScript(string pluginName)
		{
			try
			{
				_ = await scriptingProxy.unloadScriptAsync(pluginName).ConfigureAwait(false);
			}
			catch
			{
				// Best-effort cleanup; the script already reported or failed.
			}
		}

		private static async Task<bool> EnsureConnectedAsync()
		{
			if (connection != null)
				return true;

			Connection localConnection = null;
			KWinBridgeService localBridge = null;

			await initSemaphore.WaitAsync().ConfigureAwait(false);

			try
			{
				lock (initLock)
				{
					if (initFailed)
						return false;

					if (connection != null)
						return true;
				}

				localConnection = new Connection(Tmds.DBus.Address.Session);
				await localConnection.ConnectAsync().ConfigureAwait(false);
				localBridge = new KWinBridgeService();
				await localConnection.RegisterObjectAsync(localBridge).ConfigureAwait(false);
				await localConnection.RegisterServiceAsync(bridgeServiceName).ConfigureAwait(false);
				var proxy = localConnection.CreateProxy<IKWinScripting>("org.kde.KWin", new ObjectPath("/Scripting"));

				lock (initLock)
				{
					if (connection != null)
					{
						localConnection.Dispose();
						return true;
					}

					connection = localConnection;
					bridgeService = localBridge;
					scriptingProxy = proxy;
				}

				return true;
			}
			catch
			{
				try { localConnection?.Dispose(); }
				catch { }

				lock (initLock)
					initFailed = true;

				return false;
			}
			finally
			{
				initSemaphore.Release();
			}
		}

		private static string WriteScript(string requestId, string pluginName, string scriptBody)
		{
			var serviceName = bridgeServiceName;
			var script =
				"(function() {\n"
				+ $"  var __keysharpRequestId = \"{requestId}\";\n"
				+ "  function reportJson(json) {\n"
				+ $"    callDBus(\"{serviceName}\", \"{BridgeObjectPath}\", \"org.keysharp.KWinBridge\", \"ReportJson\", __keysharpRequestId, String(json));\n"
				+ "  }\n"
				+ "  function report(value) { reportJson(JSON.stringify(value)); }\n"
				+ "  function reportError(error) { report({ ok: false, error: String(error) }); }\n"
				+ "  try {\n"
				+ scriptBody
				+ "\n  } catch (e) {\n"
				+ "    reportError(e);\n"
				+ "  }\n"
				+ "})();\n";
			var path = Path.Combine(Path.GetTempPath(), $"{pluginName}.js");
			File.WriteAllText(path, script);
			return path;
		}

		private static string SanitizePrefix(string prefix)
		{
			if (prefix.IsNullOrEmpty())
				return "request";

			var sb = new StringBuilder(prefix.Length);

			foreach (var ch in prefix)
				sb.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');

			return sb.ToString();
		}

		private static bool JsonBool(JsonElement element, string property)
			=> element.TryGetProperty(property, out var value)
			   && (value.ValueKind == JsonValueKind.True || (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var i) && i != 0));

		private static bool JsonInt(JsonElement element, string property, out int result)
		{
			result = 0;

			if (!element.TryGetProperty(property, out var value))
				return false;

			if (value.ValueKind == JsonValueKind.Number)
				return value.TryGetInt32(out result);

			if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
				return true;

			return false;
		}

		private sealed class KWinBridgeService : IKWinBridge
		{
			private readonly object pendingLock = new();
			private readonly Dictionary<string, TaskCompletionSource<(bool, string)>> pending = new();

			public ObjectPath ObjectPath => BridgeObjectPath;

			public Task ReportJsonAsync(string requestId, string json)
			{
				TaskCompletionSource<(bool, string)> tcs = null;

				lock (pendingLock)
				{
					if (pending.TryGetValue(requestId, out tcs))
						pending.Remove(requestId);
				}

				tcs?.TrySetResult((true, json));
				return Task.CompletedTask;
			}

			internal Task<(bool Ok, string Json)> WaitForJsonAsync(string requestId, int timeoutMs)
			{
				var tcs = new TaskCompletionSource<(bool, string)>(TaskCreationOptions.RunContinuationsAsynchronously);

				lock (pendingLock)
					pending[requestId] = tcs;

				_ = Task.Delay(timeoutMs).ContinueWith(_ =>
				{
					lock (pendingLock)
						pending.Remove(requestId);

					tcs.TrySetResult((false, null));
				}, TaskScheduler.Default);

				return tcs.Task;
			}
		}
	}
}
#endif
