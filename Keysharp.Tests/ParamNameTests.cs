using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Keysharp.Builtins;
using Keysharp.Internals.Invoke;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Keysharp.Tests
{
	/// <summary>
	/// Keeps the built-in parameter names that scripts bind by (`f(Name: value)`) in agreement with KeysharpDocs.
	/// <para>
	/// KeysharpDocs is the source of truth (see its SOURCE_OF_TRUTH.md); <c>param-signatures.json</c> is scraped
	/// from it by <c>scripts/Export-DocSignatures.ps1</c> and checked in so this test needs no sibling checkout.
	/// </para>
	/// <para>
	/// <c>param-name-exceptions.txt</c> is a RATCHET, not a suppression list: the test fails both when a member
	/// outside it disagrees with the docs AND when a member inside it has come into agreement. Fixing a signature
	/// therefore requires deleting its line, and the list can only shrink.
	/// </para>
	/// </summary>
	public class ParamNameTests
	{
		private sealed record DocParam(string name, bool byref, bool variadic);
		private sealed record DocEntry(string page, string member, List<DocParam> parameters);

		private static string DataPath(string file)
		{
			// The .json/.txt live next to the test sources; the test binary runs from bin/<cfg>/<tfm>.
			var dir = TestContext.CurrentContext.TestDirectory;
			for (var d = new System.IO.DirectoryInfo(dir); d != null; d = d.Parent)
			{
				var candidate = System.IO.Path.Combine(d.FullName, "Keysharp.Tests", file);
				if (System.IO.File.Exists(candidate)) return candidate;
			}
			throw new System.IO.FileNotFoundException($"could not locate {file} above {dir}");
		}

		private static Dictionary<string, DocEntry> LoadDocs()
		{
			using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(DataPath("param-signatures.json")));
			var map = new Dictionary<string, DocEntry>(System.StringComparer.OrdinalIgnoreCase);

			foreach (var prop in doc.RootElement.GetProperty("signatures").EnumerateObject())
				map[prop.Name] = prop.Value.Deserialize<DocEntry>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

			return map;
		}

		private static HashSet<string> LoadExceptions() =>
			new(System.IO.File.ReadAllLines(DataPath("param-name-exceptions.txt"))
					.Select(l => l.Trim())
					.Where(l => l.Length > 0 && !l.StartsWith("#", System.StringComparison.Ordinal)),
				System.StringComparer.OrdinalIgnoreCase);

		/// <summary>Every script-visible built-in method whose parameters are all `object`, with its doc key.</summary>
		private static IEnumerable<(string key, string typeName, MethodInfo mi, MethodPropertyHolder mph, bool isGlobalFunc)> Surface()
		{
			var asm = typeof(Any).Assembly;

			foreach (var t in asm.GetExportedTypes()
								 .Where(t => t.Namespace != null && t.Namespace.StartsWith("Keysharp.Builtins", System.StringComparison.Ordinal)
											 && t.IsClass && (t.IsPublic || t.IsNestedPublic)
											 && t.GetCustomAttribute<PublicHiddenFromUser>() == null)
								 .OrderBy(t => t.FullName, System.StringComparer.Ordinal))
			{
				var typeName = Keysharp.Runtime.Script.GetUserDeclaredName(t) ?? t.Name;

				foreach (var mi in t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
									.Where(m => !m.IsSpecialName && m.DeclaringType == t
												&& m.GetCustomAttribute<PublicHiddenFromUser>() == null
												&& m.GetParameters().Length > 0
												&& m.GetParameters().All(p => p.ParameterType == typeof(object) || p.ParameterType == typeof(object[])))
									.OrderBy(m => m.Name, System.StringComparer.Ordinal))
				{
					var member = Keysharp.Runtime.Script.GetUserDeclaredName(mi) ?? mi.Name;
					// `staticFoo` is the class-static spelling of `Foo` (see MethodPropertyHolder.Name).
					if (member.StartsWith("static", System.StringComparison.Ordinal)) member = member.Substring("static".Length);

					// A property accessor is not a named-argument call site (`x.P := v`, not `x.P(value: v)`), and a
					// setter's implicit value parameter would otherwise be compared against the property's empty
					// documented signature.
					if (member.StartsWith("get_", System.StringComparison.Ordinal) || member.StartsWith("set_", System.StringComparison.Ordinal))
						continue;

					// A static holder class (sealed + abstract) is what Reflections.Initialize scans for GLOBAL
					// functions -- Objects, Math, Strings, Functions. Its members are documented under a bare name,
					// often on a page belonging to some other type (ObjSetCapacity lives on Object.htm), so they are
					// the ones for which the unqualified doc key is safe to trust.
					yield return ($"{typeName}.{member}", typeName, mi, MethodPropertyHolder.GetOrAdd(mi), t.IsSealed && t.IsAbstract);
				}
			}
		}

		/// <summary>The names this method can actually be called by, in parameter order -- what the binder uses.</summary>
		private static List<string> BindableNames(MethodPropertyHolder mph) =>
			mph.ParamIndexByName.OrderBy(kv => kv.Value).Select(kv => kv.Key).ToList();

		private static bool Matches(List<string> code, List<string> docNames)
		{
			bool Same(List<string> a, IReadOnlyList<string> b) =>
				a.Count == b.Count && !a.Where((n, i) => !string.Equals(n, b[i], System.StringComparison.OrdinalIgnoreCase)).Any();

			// Some pages document the RECEIVER as the leading parameter (`HasProp(Value, Name)`), which is not an
			// argument and is never bindable. Accept either alignment.
			return Same(code, docNames) || (docNames.Count > 0 && Same(code, docNames.Skip(1).ToList()));
		}

		[Test, Category("Misc")]
		public void BuiltInParameterNamesMatchTheDocs()
		{
			var docs = LoadDocs();
			var exceptions = LoadExceptions();
			var mismatched = new List<string>();
			var fixedButStillListed = new List<string>();
			// The two shapes the join cannot decide, tracked rather than silently dropped: a growing count means
			// the surface is drifting out from under the ratchet, which is how Format's mismatch stayed hidden.
			var unnamable = new List<string>();
			var shapeGap = new List<string>();
			var compared = 0;

			foreach (var (key, typeName, mi, mph, isGlobalFunc) in Surface())
			{
				// Prefer the type-qualified page entry. The bare fallback is only safe for a GLOBAL function: those
				// are documented under a bare name, frequently on some other type's page (ObjSetCapacity lives on
				// Object.htm, RandomSeed on Random.htm). Restricting it to static holder classes is what keeps an
				// instance member called `Call` or `Add` from joining to whichever unrelated page documents a
				// function of that name (BoundFunc.Call vs Error.Call(Message, What, Extra)).
				var member = key.Substring(typeName.Length + 1);

				if (!docs.TryGetValue(key, out var entry) && !(isGlobalFunc && docs.TryGetValue(member, out entry)))
					continue;

				// A documented variadic tail (`Value1, Value2, ...`) is positional by nature and has no name to
				// compare -- but the parameters BEFORE it do, and reflection's own bindable list already drops the
				// code's variadic tail, so the two line up. Skipping the whole member instead is what let
				// Format's `str` (documented FormatStr) survive.
				var variadicAt = entry.parameters.FindIndex(p => p.variadic);
				var docNames = (variadicAt < 0 ? entry.parameters : entry.parameters.Take(variadicAt)).Select(p => p.name).ToList();

				// ... unless that leading run is itself a placeholder SERIES rather than real parameter names:
				// `Call(Param1, Param2, ...)`, `Push(Value, Value2, ...)`, `NumPut(Type, Number, Type2, Number2, ...)`.
				// A repeated base name (the same word bar a trailing digit) is what distinguishes those from a genuine
				// leading parameter such as `Format(FormatStr, Values...)`.
				if (variadicAt >= 0 && docNames.Select(n => n.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9'))
											   .GroupBy(n => n, System.StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
					continue;

				// A doc name that is not an identifier cannot be a named argument at all (`"DllFile\Function"`), so
				// there is nothing reflection could agree with. Counted, not silently dropped.
				if (docNames.Any(n => !System.Text.RegularExpressions.Regex.IsMatch(n, @"^[A-Za-z_]\w*$")))
				{
					unnamable.Add($"  {key}: doc[{string.Join(", ", docNames)}]");
					continue;
				}

				// A method whose whole signature is `params object[]` has no named slot at all. Where the docs are
				// variadic too (`Push(Value, Value2, ...)`) that is inherent and there is nothing to fix. Where they
				// name a FIXED list, the code could have flat formals and does not -- an API-shape decision, out of
				// scope for a rename pass, but it must stay visible rather than vanish into a silent skip.
				if (BindableNames(mph).Count == 0 && mph.IsVariadic)
				{
					if (variadicAt < 0 && docNames.Count != 0)
						shapeGap.Add($"  {key}: doc[{string.Join(", ", docNames)}]");

					continue;
				}

				compared++;
				var ok = Matches(BindableNames(mph), docNames);
				var listed = exceptions.Contains(key);

				if (!ok && !listed)
					mismatched.Add($"  {key}: code[{string.Join(", ", BindableNames(mph))}] doc[{string.Join(", ", docNames)}]");
				else if (ok && listed)
					fixedButStillListed.Add($"  {key}");
			}

			Assert.Greater(compared, 100, "the doc/reflection join produced almost nothing -- the extractor or the surface filter is broken");

			// Ratchets on the two shapes the join cannot decide: neither may grow. Lower each bound when the count
			// drops -- like the exceptions file, they can only shrink.
			Assert.LessOrEqual(unnamable.Count, 4,
							   "more built-ins now document a parameter name that is not an identifier, so it cannot be "
							   + "supplied by name. Fix the doc page, then lower this bound:\n" + string.Join("\n", unnamable.OrderBy(s => s)));
			Assert.LessOrEqual(shapeGap.Count, 5,
							   "more built-ins now take a single `params object[]` where the docs name a FIXED parameter "
							   + "list, so nothing about them can be bound by name. Give them flat formals, then lower this bound:\n"
							   + string.Join("\n", shapeGap.OrderBy(s => s)));

			var msg = "";

			if (mismatched.Count > 0)
				msg += $"\n{mismatched.Count} built-in(s) disagree with KeysharpDocs on parameter names.\n"
					 + "Rename the C# parameter to the documented name (or add [ParamName] / fix the docs):\n"
					 + string.Join("\n", mismatched.OrderBy(s => s)) + "\n";

			if (fixedButStillListed.Count > 0)
				msg += $"\n{fixedButStillListed.Count} built-in(s) now MATCH the docs but are still listed in "
					 + "param-name-exceptions.txt. Delete these lines -- the list is a ratchet:\n"
					 + string.Join("\n", fixedButStillListed.OrderBy(s => s)) + "\n";

			Assert.IsTrue(msg.Length == 0, msg);
		}

		/// <summary>
		/// The scrape is checked in so the ratchet stays hermetic, which also means it can silently fall behind the
		/// docs it was taken from. Whenever the sibling checkout happens to be present -- a developer machine, not CI
		/// -- re-fingerprint its pages and say so. Skipped, never failed, when the checkout is absent.
		/// </summary>
		[Test, Category("Misc")]
		public void TheCheckedInSignaturesMatchTheSiblingDocs()
		{
			var lib = System.IO.Path.GetFullPath(System.IO.Path.Combine(
						  System.IO.Path.GetDirectoryName(DataPath("param-signatures.json")), "..", "..", "KeysharpDocs", "docs", "lib"));

			if (!System.IO.Directory.Exists(lib))
				Assert.Ignore($"KeysharpDocs not checked out beside this repo ({lib}); the checked-in scrape is taken as current.");

			using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(DataPath("param-signatures.json")));
			Assert.IsTrue(doc.RootElement.TryGetProperty("docsHash", out var recorded),
						  "param-signatures.json predates the freshness fingerprint -- re-run scripts/Export-DocSignatures.ps1.");
			Assert.AreEqual(recorded.GetString(), HashDocPages(lib),
							"param-signatures.json is stale: KeysharpDocs has changed since it was generated. "
							+ "Re-run scripts/Export-DocSignatures.ps1 and commit the result.");
		}

		/// <summary>
		/// Fingerprints the doc pages. Byte-for-byte identical to the block in Export-DocSignatures.ps1: raw file
		/// bytes, ordinal filename order, NUL between fields.
		/// </summary>
		private static string HashDocPages(string libDir)
		{
			using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
			var nul = new byte[] { 0 };

			foreach (var file in System.IO.Directory.GetFiles(libDir, "*.htm")
													.OrderBy(System.IO.Path.GetFileName, System.StringComparer.Ordinal))
			{
				sha.AppendData(System.Text.Encoding.UTF8.GetBytes(System.IO.Path.GetFileName(file)));
				sha.AppendData(nul);
				sha.AppendData(System.IO.File.ReadAllBytes(file));
				sha.AppendData(nul);
			}

			return System.Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
		}
	}
}
