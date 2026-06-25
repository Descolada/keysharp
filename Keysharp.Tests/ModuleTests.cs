using static Keysharp.Builtins.External;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Keysharp.Tests
{
	public partial class ModuleTests : TestRunner
	{
		[Test, Category("Module")]
		public void Basic() => Assert.IsTrue(TestScript("module-basic", false));

		[Test, Category("Module")]
		public void Import() => Assert.IsTrue(TestScript("module-import", false));

		[Test, Category("Module")]
		public void ImportFile() => Assert.IsTrue(TestScript("module-import-file", false));

		[Test, Category("Module")]
		public void Export() => Assert.IsTrue(TestScript("module-export", false));

		[Test, Category("Module")]
		public void Order() => Assert.IsTrue(TestScript("module-order", false));

		[Test, Category("Module")]
		public void CompatibilityMode() => Assert.IsTrue(TestScript("module-compatibility-mode", false));

		[Test, Category("Module")]
		public void ImportUnknownBuiltinMemberRejected()
		{
			// Importing a name a built-in module does not expose (e.g. `#import "Ks" { OCR }` — there is no
			// Ks.OCR) was silently dropped, so the directive compiled and the missing name surfaced only later.
			// It must be a compile-time error instead, matching AutoHotkey's load-time error for the same case.
			static string[] Lower(string src)
			{
				var (prog, parseDiags) = Keysharp.Parsing.Syntax.Parser.ParseWithDiagnostics(src);
				Assert.IsEmpty(parseDiags, "unexpected parse diagnostics: " + string.Join("; ", parseDiags));
				var lowerer = new Keysharp.Parsing.Syntax.Lowerer();
				_ = lowerer.Build(prog, "Test");
				return lowerer.Diagnostics.ToArray();
			}

			// Real Ks members still compile cleanly (the fix must not over-reject) — both a method (Cosh) and a
			// type exposed under a [UserDeclaredName] (Image, whose CLR type is KeysharpImage).
			Assert.IsEmpty(Lower("#import \"Ks\" { Cosh }\n"), "a valid built-in method import should not error");
			Assert.IsEmpty(Lower("#import KS { Image }\n"), "a valid built-in type import (UserDeclaredName) should not error");

			// A name the module does not expose is rejected, and the diagnostic names the offending member.
			var bad = Lower("#import \"Ks\" { NotARealKsMember123 }\n");
			Assert.IsTrue(System.Array.Exists(bad, d => d.Contains("has no exported member") && d.Contains("NotARealKsMember123")),
				"expected a 'has no exported member' diagnostic, got: " + string.Join("; ", bad));
		}
	}
}
