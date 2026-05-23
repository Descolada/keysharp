using System.Threading;
using System.Threading.Tasks;
using Tmds.DBus;

#if LINUX
namespace Keysharp.Internals.Window.Linux.Wayland
{
	/// <summary>
	/// D-Bus bridge to KWin's scripting subsystem. KWin doesn't expose cursor position or
	/// per-window geometry over a stable D-Bus method directly — the canonical way to read
	/// them is via the JavaScript scripting API, loading a tiny script that calls
	/// <c>workspace.cursorPos</c> (or the equivalent for whatever we're asking) and uses
	/// KWin's <c>callDBus</c> to push the result back to us.
	///
	/// Lifecycle:
	///   • A long-lived <see cref="Connection"/> to the session bus is established lazily and
	///     reused for every query.
	///   • A single instance of <see cref="KWinBridgeService"/> is registered at
	///     <c>org.keysharp.KWinBridge_{pid}</c> / <c>/keysharp/kwin</c>; KWin scripts push
	///     their results into it tagged with a request ID.
	///   • Per query we write a tiny JS file under <c>$TMPDIR</c>, call
	///     <c>org.kde.kwin.Scripting.loadScript</c> + <c>run</c>, await the bridge callback,
	///     then unload the script and delete the file.
	///
	/// Concurrency: callers may call into <see cref="QueryCursorPosition"/> from any thread;
	/// the connection and bridge are thread-safe and each request gets a unique ID so multiple
	/// in-flight queries don't collide.
	/// </summary>
	internal static class KWinDBusBridge
	{
		private static readonly object initLock = new();
		private static Connection connection;
		private static KWinBridgeService bridgeService;
		private static IKWinScripting scriptingProxy;
		private static bool initFailed;
		private static readonly string bridgeServiceName = $"org.keysharp.KWinBridge_{Environment.ProcessId}";
		internal static readonly ObjectPath BridgeObjectPath = new("/keysharp/kwin");

		private const int QueryTimeoutMs = 2000;

		/// <summary>Synchronous cursor-position query for use from non-async call sites
		/// (e.g. <see cref="System.Drawing.Point"/>-returning legacy APIs). Returns false on
		/// any failure — D-Bus down, KWin missing, script timeout, etc.</summary>
		internal static bool QueryCursorPosition(out int x, out int y)
		{
			x = 0;
			y = 0;

			try
			{
				var result = Task.Run(QueryCursorPositionAsync).GetAwaiter().GetResult();

				if (!result.Ok)
					return false;

				x = result.X;
				y = result.Y;
				return true;
			}
			catch
			{
				return false;
			}
		}

		private static async Task<(bool Ok, int X, int Y)> QueryCursorPositionAsync()
		{
			if (!await EnsureConnectedAsync().ConfigureAwait(false))
				return (false, 0, 0);

			var requestId = Guid.NewGuid().ToString("N");
			var pluginName = $"keysharp_cursor_{requestId}";
			string scriptPath = null;

			try
			{
				scriptPath = WriteCursorScript(requestId);
				var awaiterTask = bridgeService.WaitForCursorAsync(requestId, QueryTimeoutMs);

				// loadScript returns a Qt-side script ID; run() actually executes the script's
				// top-level code, at which point callDBus fires and our bridge resolves the TCS.
				int scriptId;

				try
				{
					scriptId = await scriptingProxy.loadScriptAsync(scriptPath, pluginName).ConfigureAwait(false);
				}
				catch
				{
					// KWin gone or interface drifted; bail.
					return (false, 0, 0);
				}

				try
				{
					var scriptProxy = connection.CreateProxy<IKWinScript>("org.kde.KWin", new ObjectPath($"/Scripting/Script{scriptId}"));
					await scriptProxy.runAsync().ConfigureAwait(false);
				}
				catch
				{
					await TryUnloadScript(pluginName).ConfigureAwait(false);
					return (false, 0, 0);
				}

				var (ok, x, y) = await awaiterTask.ConfigureAwait(false);
				await TryUnloadScript(pluginName).ConfigureAwait(false);
				return (ok, x, y);
			}
			catch
			{
				return (false, 0, 0);
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

		private static async Task TryUnloadScript(string pluginName)
		{
			try
			{
				_ = await scriptingProxy.unloadScriptAsync(pluginName).ConfigureAwait(false);
			}
			catch
			{
				// Best-effort cleanup; the script's already done its job by now.
			}
		}

		private static async Task<bool> EnsureConnectedAsync()
		{
			if (connection != null)
				return true;

			Connection localConnection = null;
			KWinBridgeService localBridge = null;

			lock (initLock)
			{
				if (initFailed)
					return false;

				if (connection != null)
					return true;
			}

			try
			{
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
		}

		private static string WriteCursorScript(string requestId)
		{
			// KWin scripts have access to a `workspace` object whose cursorPos is a QPoint
			// in screen-space integer pixels. callDBus is a built-in: arguments are sent as
			// the obvious D-Bus types (number → INT32, string → STRING).
			var serviceName = bridgeServiceName;
			var script =
				"var pos = workspace.cursorPos;\n"
				+ $"callDBus(\"{serviceName}\", \"{BridgeObjectPath}\", \"org.keysharp.KWinBridge\", \"ReportCursor\", \"{requestId}\", pos.x, pos.y);\n";
			var path = Path.Combine(Path.GetTempPath(), $"keysharp-kwin-cursor-{requestId}.js");
			File.WriteAllText(path, script);
			return path;
		}

		// --- D-Bus interface descriptors ---

		// Lower-case method names match KWin's actual D-Bus members exactly; Tmds.DBus
		// strips the trailing "Async" and uses what remains as-is.

#pragma warning disable IDE1006 // naming styles
		[DBusInterface("org.kde.kwin.Scripting")]
		internal interface IKWinScripting : IDBusObject
		{
			Task<int> loadScriptAsync(string filePath, string pluginName);
			Task<bool> unloadScriptAsync(string pluginName);
		}

		[DBusInterface("org.kde.kwin.Script")]
		internal interface IKWinScript : IDBusObject
		{
			Task runAsync();
		}

		[DBusInterface("org.keysharp.KWinBridge")]
		internal interface IKWinBridge : IDBusObject
		{
			Task ReportCursorAsync(string requestId, int x, int y);
		}
#pragma warning restore IDE1006

		// --- Bridge service that KWin scripts call back into ---

		private sealed class KWinBridgeService : IKWinBridge
		{
			public ObjectPath ObjectPath => BridgeObjectPath;

			private readonly object pendingLock = new();
			private readonly Dictionary<string, TaskCompletionSource<(bool, int, int)>> pending = new();

			public Task ReportCursorAsync(string requestId, int x, int y)
			{
				TaskCompletionSource<(bool, int, int)> tcs = null;

				lock (pendingLock)
				{
					if (pending.TryGetValue(requestId, out tcs))
						pending.Remove(requestId);
				}

				tcs?.TrySetResult((true, x, y));
				return Task.CompletedTask;
			}

			internal Task<(bool Ok, int X, int Y)> WaitForCursorAsync(string requestId, int timeoutMs)
			{
				var tcs = new TaskCompletionSource<(bool, int, int)>(TaskCreationOptions.RunContinuationsAsynchronously);

				lock (pendingLock)
					pending[requestId] = tcs;

				_ = Task.Delay(timeoutMs).ContinueWith(_ =>
				{
					lock (pendingLock)
						pending.Remove(requestId);

					tcs.TrySetResult((false, 0, 0));
				}, TaskScheduler.Default);

				return tcs.Task;
			}
		}
	}
}
#endif
