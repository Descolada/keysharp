using Keysharp.Builtins;
using KsDebug = Keysharp.Builtins.Debug;
// PEReader.GetMetadataReader() is an extension method declared in this namespace, so it must be imported by name.
using System.Reflection.Metadata;

namespace Keysharp.Internals.Os
{
	/// <summary>
	/// Backs <c>#Package</c> and <c>Clr.LoadPackage</c>. Nothing here opens a socket, parses a .nuspec or extracts an
	/// archive: the .NET SDK resolves the packages and this reads back the <c>project.assets.json</c> it writes.
	/// See docs/design-nuget-packages.md for why.
	/// </summary>
	internal static class NuGetPackageLoader
	{
		/// <summary>Restore is a network operation; bound it so a hung feed fails loudly instead of wedging startup.</summary>
		private const int RestoreTimeoutMs = 180_000;

		private static readonly Lock sync = new();

		// The two maps below are read by the AssemblyLoadContext hooks, which fire on any thread and at any time —
		// including while this class holds `sync` for the duration of a restore (up to RestoreTimeoutMs). They are
		// therefore concurrent rather than guarded by `sync`, so a resolution on another thread is never blocked
		// behind a network operation.
		/// <summary>Assembly simple name -> path, for the whole resolved closure. Dependencies are resolvable, not surfaced.</summary>
		private static readonly ConcurrentDictionary<string, string> managedByName = new(StringComparer.OrdinalIgnoreCase);

		/// <summary>Native library name (several spellings per file — see <see cref="AddNativeAliases"/>) -> path.</summary>
		private static readonly ConcurrentDictionary<string, string> nativeByName = new(StringComparer.OrdinalIgnoreCase);

		private static bool hooksInstalled;

		/// <summary>
		/// What to call this feature in messages, so a script that only used <c>Clr.LoadPackage</c> is never told a
		/// directive it does not contain failed. Set by both entry points, under <see cref="sync"/>.
		/// </summary>
		private static string label = "#Package";

		/// <summary>
		/// Every package requested so far in this process, from <c>#Package</c> and <c>Clr.LoadPackage</c> alike. A
		/// later request resolves the UNION of this and itself rather than its own island: resolution is whole-graph,
		/// so two independent resolutions can pick different versions of a shared dependency and try to load both.
		/// The directive avoids this by construction (one batched call); the runtime API cannot, so it accumulates.
		/// </summary>
		private static readonly List<PackageRef> requested = [];

		/// <summary>Package id -> the managed assembly paths that package itself contributed, for LoadPackage's return.</summary>
		private static readonly Dictionary<string, List<string>> assembliesByPackage = new(StringComparer.OrdinalIgnoreCase);

		/// <summary>
		/// Package id -> whether it was applied as a directly requested package (loaded) rather than a dependency
		/// (registered only). Re-resolving after a later call replays the same closure; this is what stops it
		/// re-loading and re-reading metadata for work already done.
		/// </summary>
		private static readonly Dictionary<string, bool> applied = new(StringComparer.OrdinalIgnoreCase);

		/// <summary>Restore subprocesses spawned. A warm set must spawn none, which is otherwise unobservable.</summary>
		internal static int RestoreCount;

		/// <summary>
		/// Package-graph resolutions performed, cached or not. A batched request must resolve exactly once however
		/// many packages it carries; unlike <see cref="RestoreCount"/> this holds whether the cache is warm or cold.
		/// </summary>
		internal static int ResolveCount;

		/// <summary>
		/// Resets bookkeeping so a test starts from a known state. Package content and already-loaded assemblies are
		/// untouched — .NET cannot unload them.
		/// </summary>
		internal static void ResetForTests()
		{
			lock (sync)
			{
				requested.Clear();
				applied.Clear();
				assembliesByPackage.Clear();
				RestoreCount = ResolveCount = 0;
			}
		}

		/// <summary>
		/// Backs the <c>#Package</c> directive. An unavailable package not marked <c>*i</c> stops the script.
		/// </summary>
		/// <param name="packages">Every <c>#Package</c> in the program, already validated by the lowerer.</param>
		internal static void Load((string Id, string Version, bool Optional)[] packages)
		{
			lock (sync)
			{
				label = "#Package";
				var refs = new List<PackageRef>(packages.Length);

				foreach (var (id, version, optional) in packages)
				{
					// Re-checked because these are written verbatim into the generated csproj. The lowerer already
					// rejected anything malformed, so a failure here means the two ends disagree.
					if (!IsValidId(id) || !IsValidVersion(version))
					{
						_ = Errors.ErrorOccurred($"{label}: malformed package '{id} {version}' (this is a Keysharp bug, not a script error)",
												 null, Keyword_ExitApp);
						continue;
					}

					refs.Add(new PackageRef(id, version, optional));
				}

				// ONE Add for the whole set, never one per package. Add resolves the union in a single restore, so
				// the graph is unified once; feeding the packages in one at a time would load each one's closure
				// before the next was resolved, and the later resolution could pick a different version of a shared
				// dependency than the one already in the process — which .NET cannot unload. That diamond is the
				// reason the directive batches, and the reason Clr.LoadPackage (which cannot) re-resolves the union.
				if (!Add(refs, out var error))
					_ = Errors.ErrorOccurred(error, null, Keyword_ExitApp);
			}
		}

		/// <summary>
		/// Backs <c>Clr.LoadPackage</c>. Returns the named package's own managed assemblies, null when an optional
		/// package could not be made available, and sets <paramref name="error"/> when a required one could not.
		/// </summary>
		internal static Assembly[] LoadOne(string id, string version, bool optional, out string error)
		{
			lock (sync)
			{
				label = "Clr.LoadPackage";

				if (!IsValidId(id))
				{
					error = $"{label}: '{id}' is not a valid package name";
					return null;
				}

				if (!TryTranslateVersion(version, out var range, out var verr))
				{
					error = $"{label}: {verr} for package '{id}'";
					return null;
				}

				if (!Add([new PackageRef(id, range, optional)], out error))
					return null;

				// Absent, or present but empty (a package whose only asset for this framework is the `_._` placeholder):
				// either way there is nothing to hand back.
				if (!assembliesByPackage.TryGetValue(id, out var paths) || paths.Count == 0)
				{
					if (!optional)
						error = $"{label}: '{id}' resolved but contributed no assemblies for this framework.";

					return null;
				}

				var loaded = new List<Assembly>(paths.Count);

				foreach (var path in paths)
				{
					// Apply already loaded these; re-resolving by path is how the Assembly objects are recovered. An
					// identity the shared framework also ships throws here, exactly as it does in Apply, and is benign.
					try { loaded.Add(AssemblyLoadContext.Default.LoadFromAssemblyPath(path)); }
					catch (FileLoadException) { }
				}

				return loaded.Count == 0 ? null : loaded.ToArray();
			}
		}

		/// <summary>
		/// Folds <paramref name="incoming"/> into <see cref="requested"/> and resolves the union. Returns false with a
		/// reason when a required package could not be made available; the additions are rolled back in that case, so a
		/// caught error does not poison every later call with a set that is known to fail. Caller holds <see cref="sync"/>.
		/// </summary>
		private static bool Add(List<PackageRef> incoming, out string error)
		{
			error = null;

			if (incoming.Count == 0)
				return true;

			var restore = new List<PackageRef>(requested);

			foreach (var p in incoming)
			{
				var i = requested.FindIndex(r => r.Id.Equals(p.Id, StringComparison.OrdinalIgnoreCase));

				if (i < 0)
					requested.Add(p);
				else if (!requested[i].Version.Equals(p.Version, StringComparison.OrdinalIgnoreCase))
				{
					error = $"{label}: '{p.Id}' was already requested as version '{requested[i].Version}' and is now requested as '{p.Version}'";
					return Rollback(restore);
				}
				else if (!p.Optional)
					requested[i] = p;   // asking again without `*i` promotes it: the stricter requirement wins
			}

			if (TryLoadSet(requested, out error))
				return true;

			// A `*i` package that cannot be resolved must not stop the script — but resolution is whole-graph (see
			// `requested`), so one unavailable optional package fails the restore for the required ones too. Drop the
			// optional packages and resolve the remainder as its own set (its own cache entry, since the set differs).
			var required = requested.Where(p => !p.Optional).ToList();

			if (required.Count != requested.Count && (required.Count == 0 || TryLoadSet(required, out error)))
			{
				// `error` still holds the failure from the full-set attempt above, and an all-optional set reaches here
				// without overwriting it. Clearing it is what keeps an unavailable optional package non-fatal.
				error = null;
				_ = KsDebug.OutputDebug($"{label}: optional package(s) not available, continuing without: "
													   + string.Join(", ", requested.Where(p => p.Optional).Select(p => p.Id)));
				return true;
			}

			return Rollback(restore);

			static bool Rollback(List<PackageRef> to)
			{
				requested.Clear();
				requested.AddRange(to);
				return false;
			}
		}

		/// <summary>
		/// Resolves and loads exactly this package set, or reports why it could not. Never fatal — the caller decides,
		/// because an unavailable <c>*i</c> package is recoverable and a required one is not.
		/// </summary>
		private static bool TryLoadSet(List<PackageRef> packages, out string failure)
		{
			failure = null;
			ResolveCount++;
			var dir = ProjectDir(packages);
			var assetsPath = Path.Combine(dir, "obj", "project.assets.json");
			// 1) Cache hit: no SDK, no network, no subprocess. This is the common case after first run. A restore that
			// FAILED still writes a complete, well-formed assets file for whichever packages did resolve, so the
			// assets file alone cannot be trusted — reading it would turn a hard error into a silently missing
			// package on every later run. RestoreSucceeded consults NuGet's own verdict alongside it.
			var resolved = RestoreSucceeded(dir) ? TryReadAssets(assetsPath) : null;

			if (resolved == null)
			{
				// 2) Miss: let the SDK do the actual work.
				if (!TryRestore(dir, packages, out failure))
					return false;

				resolved = TryReadAssets(assetsPath);

				if (resolved == null)
				{
					failure = $"{label}: restore succeeded but no usable package list was produced in \"{assetsPath}\".";
					return false;
				}
			}

			// Reported on both paths, so a script with a floating version can always read back what it actually got —
			// on the restore that first resolved it and on every run that reuses the answer.
			ReportResolved(packages, resolved);
			Apply(resolved, packages);
			return true;
		}

		// ---- directive spec ----

		/// <summary>
		/// A package id and an optional version. Both are validated by the lowerer (so a malformed one is a compile
		/// error) and again here, which is also what makes writing them straight into the generated csproj safe.
		/// </summary>
		internal static bool IsValidId(string s) =>
			s.Length > 0 && s.Length < 128 && s.All(c => char.IsAsciiLetterOrDigit(c) || c == '.' || c == '_' || c == '-');

		internal static bool IsValidVersion(string s) =>
			s.Length < 64 && s.All(c => char.IsAsciiLetterOrDigit(c) || c == '.' || c == '-' || c == '+' || c == '*'
										|| c == '[' || c == ']' || c == '(' || c == ')' || c == ',');

		/// <summary>A floating version: a plain version whose last component is the only <c>*</c> (<c>13.*</c>, <c>*</c>).</summary>
		private static bool IsFloatingVersion(string s) =>
			s == "*" || (s.EndsWith(".*", StringComparison.Ordinal) && IsPlainVersion(s[..^2]));

		/// <summary>
		/// A literal NuGet interval — a bracket, one or two comma-separated plain versions, a closing bracket. Either
		/// bound may be empty (an open end), and the single-version form <c>[1.0]</c> means exactly that version.
		/// </summary>
		private static bool IsValidRange(string s)
		{
			if (!IsValidVersion(s) || s.Length < 3 || (s[0] != '[' && s[0] != '(') || (s[^1] != ']' && s[^1] != ')'))
				return false;

			var parts = s[1..^1].Split(',');

			if (parts.Length > 2 || parts.All(p => p.Length == 0))
				return false;   // "[]" / "[,]" bounds nothing

			// A single version is only meaningful as the inclusive `[1.0]` form; `(1.0)` bounds nothing.
			if (parts.Length == 1 && (s[0] != '[' || s[^1] != ']'))
				return false;

			return parts.All(p => p.Length == 0 || IsPlainVersion(p));
		}

		/// <summary>
		/// Translates the version a script writes (omitted, partial, exact, comparison-bounded, or already a NuGet
		/// range) into the range NuGet understands, mirroring what <c>#Requires</c> accepts. Two rules are not
		/// self-evident from the code: a FULL version becomes exact (<c>13.0.3</c> → <c>[13.0.3]</c>, because NuGet
		/// reads a bare <c>13.0.3</c> as "or newer" and a script naming a full version wants reproducibility), and
		/// translation must happen at all because <c>&lt;</c>/<c>&gt;</c> are not legal in an XML attribute value and
		/// so can never reach the generated project file as typed. Runs at compile time, so a bad version is a
		/// compile error and the cache key is already canonical. VersionFormsTranslateToNuGetRanges pins every form.
		/// </summary>
		internal static bool TryTranslateVersion(string written, out string range, out string error)
		{
			range = Translate((written ?? "").Trim());
			error = range == null ? $"'{written}' is not a valid version" : null;
			return range != null;

			// Returns null for anything malformed; the single caller above turns that into the one error message.
			static string Translate(string s)
			{
				if (s.Length == 0)
					return "*";   // newest stable

				// A literal NuGet range or a floating version is already in the target language, but still has to be
				// well-formed: it is written verbatim into the csproj, where a malformed one would surface as a NuGet
				// error at run time instead of the compile error this method promises.
				if (s[0] == '[' || s[0] == '(')
					return IsValidRange(s) ? s : null;

				if (s.Contains('*'))
					return IsValidVersion(s) && IsFloatingVersion(s) ? s : null;

				var tokens = s.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);

				// A bare version is exactly one token with no comparison, so a stray trailing word (`13.0.3 extra`)
				// is rejected rather than having only its first token honoured.
				if (tokens.Length == 1 && Operator(tokens[0]).Length == 0)
				{
					var only = StripV(tokens[0]);
					// A partial version floats within what was written; a full one is exact.
					return !IsPlainVersion(only) ? null
						 : only.Split('.').Length >= 3 ? $"[{only}]" : $"{only}.*";
				}

				string lo = null, hi = null, exact = null;
				bool loInclusive = false, hiInclusive = false;

				foreach (var raw in tokens)
				{
					var op = Operator(raw);
					var tok = op.Length == 0 ? null : StripV(raw.Substring(op.Length));

					if (tok == null || !IsPlainVersion(tok))
						return null;

					switch (op)
					{
						case ">=": lo = tok; loInclusive = true; break;
						case ">": lo = tok; break;
						case "<=": hi = tok; hiInclusive = true; break;
						case "<": hi = tok; break;
						default: exact = tok; break;   // "="
					}
				}

				// `=` pins the version, so a bound alongside it is contradictory rather than merely redundant. This is
				// checked after the loop, not on sight, so that the tokens following it are still validated.
				if (exact != null)
					return lo == null && hi == null ? $"[{exact}]" : null;

				// An absent bound is open, and an open bound is always exclusive in NuGet's syntax: `(,14)`, never `[,14)`.
				return $"{(loInclusive ? '[' : '(')}{lo},{hi}{(hiInclusive ? ']' : ')')}";
			}

			static string Operator(string t)
			{
				foreach (var c in new[] { ">=", "<=", ">", "<", "=" })
					if (t.StartsWith(c, StringComparison.Ordinal))
						return c;

				return "";
			}

			// `#Requires` allows an optional leading "v"; accept it here so the two read the same.
			static string StripV(string t) =>
				t.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? t.Substring(1) : t;
		}

		/// <summary>A version with no operator, range syntax or wildcard — digits, dots and a pre-release/metadata tail.</summary>
		private static bool IsPlainVersion(string s) =>
			s.Length != 0 && char.IsAsciiDigit(s[0])
			&& s.All(c => char.IsAsciiLetterOrDigit(c) || c == '.' || c == '-' || c == '+');

		/// <summary>One `#Package` directive: its name, requested version (empty = newest stable) and `*i` flag.</summary>
		internal readonly record struct PackageRef(string Id, string Version, bool Optional);

		// ---- cache location ----

		/// <summary>
		/// One directory per (package set, framework, RID). The generated project and its assets file live here;
		/// package <em>content</em> always lives in the standard global packages folder, written by the SDK —
		/// Keysharp never writes there.
		/// </summary>
		private static string ProjectDir(List<PackageRef> packages) => Path.Combine(CacheRoot(), CacheKeyFor(packages));

		/// <summary>
		/// The cache key for a package set: order-independent (so writing the same directives in a different order is
		/// still a cache hit) but sensitive to every id, version and to the framework/RID the assets were resolved for.
		/// The `*i` flag is deliberately excluded — it changes what happens on failure, not what gets resolved.
		/// </summary>
		internal static string CacheKeyFor(List<PackageRef> packages)
		{
			var key = string.Join(";", packages.Select(p => $"{p.Id.ToLowerInvariant()}|{p.Version.ToLowerInvariant()}")
											   .OrderBy(s => s, StringComparer.Ordinal));
			return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{tfm}\n{rid}\n{key}")))[..16].ToLowerInvariant();
		}

		private static string CacheRoot()
		{
#if WINDOWS
			var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
#else
			var root = Environment.GetEnvironmentVariable("XDG_DATA_HOME");

			if (string.IsNullOrEmpty(root))
				root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

#endif
			return Path.Combine(root, "Keysharp", "packages");
		}

		/// <summary>
		/// The framework and RID a package must be compatible with, and that the assets file is read against — taken
		/// from the running runtime rather than hard-coded, so they track the target Keysharp itself is built for.
		/// </summary>
		internal static string TargetFramework => tfm;

		internal static string RuntimeId => rid;

		private static readonly string tfm =
#if WINDOWS
			$"net{Environment.Version.Major}.{Environment.Version.Minor}-windows";
#else
			$"net{Environment.Version.Major}.{Environment.Version.Minor}";
#endif

		private static readonly string rid = RuntimeInformation.RuntimeIdentifier;

		// ---- restore (step 2) ----

		private static string BuildProject(List<PackageRef> packages)
		{
			var sb = new StringBuilder();
			_ = sb.AppendLine("<!-- Generated by Keysharp for the #Package directive. Safe to delete; it will be recreated. -->");
			_ = sb.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
			_ = sb.AppendLine("  <PropertyGroup>");
			_ = sb.AppendLine($"    <TargetFramework>{tfm}</TargetFramework>");
			_ = sb.AppendLine($"    <RuntimeIdentifier>{rid}</RuntimeIdentifier>");
			_ = sb.AppendLine("    <SelfContained>false</SelfContained>");
			_ = sb.AppendLine("    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>");
			// Advisories are the one security control this design gets for free; make sure they are on.
			_ = sb.AppendLine("    <NuGetAudit>true</NuGetAudit>");
			_ = sb.AppendLine("    <NuGetAuditMode>all</NuGetAuditMode>");
			_ = sb.AppendLine("  </PropertyGroup>");
			_ = sb.AppendLine("  <ItemGroup>");

			foreach (var (id, version, _) in packages)
				// Already a NuGet range: the lowerer translated whatever the script wrote (see TryTranslateVersion),
				// so nothing here can contain a character that is illegal in an XML attribute value. A floating range
				// stays pinned by this cache entry — the assets file is written once and reused, so a script does not
				// silently upgrade on later runs.
				_ = sb.AppendLine($"    <PackageReference Include=\"{id}\" Version=\"{version}\" />");

			_ = sb.AppendLine("  </ItemGroup>");
			_ = sb.AppendLine("</Project>");
			return sb.ToString();
		}

		private static bool TryRestore(string dir, List<PackageRef> packages, out string failure)
		{
			failure = null;
			RestoreCount++;

			try
			{
				_ = Directory.CreateDirectory(dir);
				// MSBuild walks up from the project directory looking for these; an unrelated one above the cache
				// root would silently change how the generated project restores. Terminate the walk here.
				File.WriteAllText(Path.Combine(dir, "Directory.Build.props"), "<Project />");
				File.WriteAllText(Path.Combine(dir, "Directory.Build.targets"), "<Project />");
				File.WriteAllText(Path.Combine(dir, "Directory.Packages.props"), "<Project />");   // NuGet walks up for this one too
				File.WriteAllText(Path.Combine(dir, "keysharp-packages.csproj"), BuildProject(packages));
			}
			catch (Exception e)
			{
				failure = $"{label}: could not write the package project to \"{dir}\": {e.Message}";
				return false;
			}

			var psi = new ProcessStartInfo("dotnet", "restore --nologo")
			{
				WorkingDirectory = dir,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true
			};
			string output;

			try
			{
				using var proc = Process.Start(psi);

				if (proc == null)
					return Fail(dir, packages, "the 'dotnet' command could not be started", "", true, out failure);

				// Read both pipes before waiting, or a chatty restore can fill a pipe buffer and deadlock.
				var stdout = proc.StandardOutput.ReadToEndAsync();
				var stderr = proc.StandardError.ReadToEndAsync();

				if (!proc.WaitForExit(RestoreTimeoutMs))
				{
					try { proc.Kill(entireProcessTree: true); } catch { }

					return Fail(dir, packages, $"'dotnet restore' did not finish within {RestoreTimeoutMs / 1000} seconds", "", false, out failure);
				}

				output = (stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult()).Trim();

				if (proc.ExitCode != 0)
					return Fail(dir, packages, $"'dotnet restore' failed (exit code {proc.ExitCode})", output, false, out failure);
			}
			catch (System.ComponentModel.Win32Exception e)
			{
				// The process could not be started at all — "file not found" here means the .NET SDK is not installed
				// or not on PATH, which is the one case the install-the-SDK advice fits.
				return Fail(dir, packages, $"'dotnet restore' could not be run ({e.Message})", "", true, out failure);
			}
			catch (Exception e)
			{
				return Fail(dir, packages, $"'dotnet restore' failed ({e.Message})", "", false, out failure);
			}

			// NuGet audit findings (NU1901-NU1904) are advisory, not fatal: the user asked for these packages, and a
			// vulnerable transitive dependency is information they need rather than a reason to refuse to start.
			foreach (var line in output.Split('\n'))
				if (line.Contains("NU190", StringComparison.Ordinal))
					_ = KsDebug.OutputDebug($"{label}: {line.Trim()}");

			return true;
		}

		/// <summary>
		/// Builds the restore-failure message. The "install the SDK" paragraph appears only when the SDK is actually
		/// the problem: printing it for a plain NU1101 (package not found, SDK working fine) buries the real error
		/// under advice the user has already followed.
		/// </summary>
		private static bool Fail(string dir, List<PackageRef> packages, string reason, string output, bool sdkMissing, out string failure)
		{
			var advice = sdkMissing
						 ? """
						   Keysharp does not download packages itself — it asks the .NET SDK to resolve them, so the
						   SDK must be installed and on PATH the first time a package set is used on this machine.
						   Install it from https://dotnet.microsoft.com/download, or restore manually with:

						   """
						 : "Reproduce and investigate with:";
			var log = output.Length == 0 ? "" : "\n\n" + (output.Length > 4000 ? output[..4000] + " …" : output);
			failure = $"""
					   {label}: {reason}.

					   {advice}
					       cd "{dir}"
					       dotnet restore

					   Requested: {string.Join(", ", packages.Select(p => $"{p.Id} {p.Version}"))}{log}
					   """;
			return false;
		}

		// ---- assets file (steps 1 and 3) ----

		internal sealed class ResolvedPackage
		{
			internal string Id;
			internal string Version;
			internal readonly List<string> Managed = [];
			internal readonly List<string> Native = [];
		}

		/// <summary>
		/// Whether the restore that produced this directory's assets file succeeded, per the `success` flag NuGet
		/// writes to project.nuget.cache beside it. Absent or unreadable counts as failure, so an interrupted restore
		/// re-runs rather than being trusted.
		/// </summary>
		internal static bool RestoreSucceeded(string dir)
		{
			try
			{
				using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "obj", "project.nuget.cache")));
				return doc.RootElement.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.True;
			}
			catch (Exception)
			{
				return false;
			}
		}

		/// <summary>
		/// Reads the SDK's resolved package graph. Returns null when the file is absent, unreadable, or names a file
		/// that is missing — the last case matters because the global packages folder is shared and can be cleared
		/// behind us, and a stale cache entry must fall through to a fresh restore rather than half-load. Whether the
		/// restore that wrote it succeeded at all is a separate question, answered by RestoreSucceeded.
		/// </summary>
		internal static List<ResolvedPackage> TryReadAssets(string assetsPath)
		{
			if (!File.Exists(assetsPath))
				return null;

			try
			{
				using var doc = JsonDocument.Parse(File.ReadAllText(assetsPath));
				var root = doc.RootElement;

				if (!root.TryGetProperty("targets", out var targets) || !root.TryGetProperty("libraries", out var libraries))
					return null;

				var folders = root.TryGetProperty("packageFolders", out var pf)
							  ? pf.EnumerateObject().Select(p => p.Name).ToList()
							  : [];

				if (folders.Count == 0 || !TrySelectTarget(targets, out var target))
					return null;

				var result = new List<ResolvedPackage>();

				foreach (var entry in target.EnumerateObject())
				{
					// "Id/Version"; anything not of type "package" (i.e. a project reference) has no cached assets.
					var slash = entry.Name.LastIndexOf('/');

					if (slash <= 0
						|| !libraries.TryGetProperty(entry.Name, out var lib)
						|| !lib.TryGetProperty("type", out var type) || type.GetString() != "package"
						|| !lib.TryGetProperty("path", out var relElem) || relElem.GetString() is not { } rel)
						continue;

					var baseDir = folders.Select(f => Path.Combine(f, rel.Replace('/', Path.DirectorySeparatorChar)))
										 .FirstOrDefault(Directory.Exists);

					if (baseDir == null)
						return null;   // package content is gone — treat the whole cache entry as stale

					var pkg = new ResolvedPackage { Id = entry.Name.Substring(0, slash), Version = entry.Name.Substring(slash + 1) };

					if (!CollectAssets(entry.Value, "runtime", baseDir, pkg.Managed)
						|| !CollectAssets(entry.Value, "native", baseDir, pkg.Native))
						return null;

					result.Add(pkg);
				}

				return result.Count == 0 ? null : result;
			}
			catch (Exception)
			{
				return null;   // unparseable/unreadable — fall through to a fresh restore
			}
		}

		/// <summary>Prefers the RID-qualified target (which is what carries native and RID-specific managed assets).</summary>
		private static bool TrySelectTarget(JsonElement targets, out JsonElement target)
		{
			target = default;
			var found = false;

			foreach (var t in targets.EnumerateObject())
			{
				if (!t.Name.StartsWith(tfm, StringComparison.OrdinalIgnoreCase))
					continue;

				if (t.Name.EndsWith("/" + rid, StringComparison.OrdinalIgnoreCase))
				{
					target = t.Value;
					return true;
				}

				if (!found)
				{
					target = t.Value;
					found = true;
				}
			}

			return found;
		}

		private static bool CollectAssets(JsonElement package, string section, string baseDir, List<string> into)
		{
			if (!package.TryGetProperty(section, out var assets))
				return true;

			foreach (var asset in assets.EnumerateObject())
			{
				// "_._" is NuGet's explicit placeholder for "this package deliberately contributes nothing here".
				if (asset.Name.EndsWith("_._", StringComparison.Ordinal))
					continue;

				var full = Path.Combine(baseDir, asset.Name.Replace('/', Path.DirectorySeparatorChar));

				if (!File.Exists(full))
					return false;

				into.Add(full);
			}

			return true;
		}

		// ---- loading ----

		/// <summary>
		/// Makes a resolved closure usable. Packages the script named itself are loaded now; everything they drag in
		/// is only *registered*, by reading its type and namespace names out of PE metadata — a dependency is then
		/// loaded on the first lookup that resolves into it and not before, the same laziness a compiled C# program
		/// gets from its assembly references (see <c>TypeResolver.RegisterDeferredAssembly</c>).
		///
		/// Re-resolving after a later <c>Clr.LoadPackage</c> replays the same closure, so packages already applied are
		/// skipped: reloading is idempotent but re-reading every dependency's metadata is not free.
		/// </summary>
		private static void Apply(List<ResolvedPackage> resolved, List<PackageRef> wanted)
		{
			InstallHooks();

			foreach (var pkg in resolved)
			{
				var direct = wanted.Any(r => r.Id.Equals(pkg.Id, StringComparison.OrdinalIgnoreCase));

				// Skip what is already done, but a package first seen as a dependency and later named directly still
				// has to be loaded rather than left deferred, so only a same-or-weaker repeat is skipped.
				if (applied.TryGetValue(pkg.Id, out var wasDirect) && (wasDirect || !direct))
					continue;

				foreach (var path in pkg.Managed)
					managedByName[Path.GetFileNameWithoutExtension(path)] = path;

				foreach (var path in pkg.Native)
					AddNativeAliases(path);

				// What this package itself contributed, so Clr.LoadPackage can hand back exactly those assemblies
				// rather than the whole closure.
				assembliesByPackage[pkg.Id] = pkg.Managed;

				foreach (var path in pkg.Managed)
				{
					if (!direct && TryRegisterDeferred(path))
						continue;

					try
					{
						_ = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
					}
					catch (Exception e)
					{
						// An assembly whose identity is already loaded from elsewhere (commonly one the shared
						// framework also ships) is not an error — the existing one is used. Anything else is only
						// fatal for a package the script named itself; an unloadable dependency may simply be one
						// this platform never needs, and it stays reachable through the resolving hook if it is.
						if (e is FileLoadException || !direct)
							continue;

						_ = Errors.ErrorOccurred($"{label}: failed to load \"{path}\" from package {pkg.Id} {pkg.Version}: {e.Message}",
												 null, Keyword_ExitApp);
					}
				}

				// Recorded only once the package's assemblies are in hand: an OnError handler can let the error above
				// through, and marking it applied any earlier would let a later LoadPackage hand back the partial set
				// as if it had succeeded.
				applied[pkg.Id] = direct;
			}
		}

		/// <summary>
		/// Registers an assembly's public top-level type names with the resolver without loading it. Reading the
		/// metadata tables directly is what makes deferral worth having: it yields strings out of the metadata heap
		/// and allocates no <see cref="Type"/> objects, whereas loading would pull the assembly's entire type set into
		/// the resolver's index (via <c>GetTypes()</c>) for a dependency the script may never touch.
		///
		/// Nested types are intentionally skipped. `Clr` walks dotted names, which never match the <c>Outer+Inner</c>
		/// spelling anyway, and reaching a nested type requires naming its declaring type first — which materializes
		/// the assembly and re-indexes it properly.
		///
		/// Returns false if the file has no managed metadata or cannot be read, in which case the caller falls back
		/// to loading it.
		/// </summary>
		private static bool TryRegisterDeferred(string path)
		{
			try
			{
				using var fs = File.OpenRead(path);
				using var pe = new System.Reflection.PortableExecutable.PEReader(fs);

				if (!pe.HasMetadata)
					return false;

				var mr = pe.GetMetadataReader();
				var names = new List<(string, string)>(mr.TypeDefinitions.Count);

				foreach (var handle in mr.TypeDefinitions)
				{
					var td = mr.GetTypeDefinition(handle);

					if ((td.Attributes & TypeAttributes.VisibilityMask) != TypeAttributes.Public)
						continue;

					names.Add((mr.GetString(td.Namespace), mr.GetString(td.Name)));
				}

				// Type forwarders: names this assembly publicly answers to even though the type lives elsewhere.
				// Loading it is still the right response, since the forward is what redirects the lookup.
				foreach (var handle in mr.ExportedTypes)
				{
					var et = mr.GetExportedType(handle);

					if (et.IsForwarder)
						names.Add((mr.GetString(et.Namespace), mr.GetString(et.Name)));
				}

				if (names.Count == 0)
					return false;

				TypeResolver.RegisterDeferredAssembly(path, names);
				return true;
			}
			catch (Exception)
			{
				return false;
			}
		}

		/// <summary>
		/// Registers the spellings a P/Invoke might use for one native file: DllImport("e_sqlite3") and
		/// DllImport("libfoo.so.1") both have to find their file, and on Unix the "lib" prefix is conventionally
		/// dropped in source.
		/// </summary>
		private static void AddNativeAliases(string path)
		{
			foreach (var alias in NativeAliasesFor(path))
				nativeByName[alias] = path;
		}

		/// <summary>The spellings a DllImport might use for one native file. Pure, so it is unit-testable.</summary>
		internal static List<string> NativeAliasesFor(string path)
		{
			var file = Path.GetFileName(path);
			var stem = Path.GetFileNameWithoutExtension(file);
			var aliases = new List<string> { file, stem };
			var soIdx = file.IndexOf(".so.", StringComparison.Ordinal);

			if (soIdx > 0)
			{
				stem = file.Substring(0, soIdx);
				aliases.Add(stem + ".so");
				aliases.Add(stem);
			}

			if (stem.StartsWith("lib", StringComparison.Ordinal) && stem.Length > 3)
				aliases.Add(stem.Substring(3));

			return aliases.Distinct().ToList();
		}

		private static void InstallHooks()
		{
			if (hooksInstalled)
				return;

			hooksInstalled = true;

			AssemblyLoadContext.Default.Resolving += (ctx, name) =>
				managedByName.TryGetValue(name.Name ?? "", out var path) && File.Exists(path)
				? ctx.LoadFromAssemblyPath(path)
				: null;
			AssemblyLoadContext.Default.ResolvingUnmanagedDll += (_, name) =>
				// The Windows module loader rejects '/' separators; route through the same chokepoint DllCall uses.
				nativeByName.TryGetValue(name ?? "", out var path) && File.Exists(path)
				? NativeLibrary.Load(Dll.NormalizeLoaderPath(path))
				: 0;
		}

		/// <summary>
		/// Reports what a floating request actually resolved to, so `#Package Foo` has a discoverable answer the
		/// user can paste back as an explicit version. Only floating ones: an exact request already knows.
		/// </summary>
		private static void ReportResolved(List<PackageRef> wanted, List<ResolvedPackage> resolved)
		{
			foreach (var w in wanted.Where(w => w.Version.Contains('*')))
				if (resolved.FirstOrDefault(r => r.Id.Equals(w.Id, StringComparison.OrdinalIgnoreCase)) is { } hit)
					_ = KsDebug.OutputDebug($"{label}: {hit.Id} {w.Version} resolved to {hit.Version}");
		}
	}
}
