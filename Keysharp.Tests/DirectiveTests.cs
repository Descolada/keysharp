using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Keysharp.Tests
{
	public class DirectivesTests : TestRunner
	{
		[Test, Category("Directives")]
		public void AsmInfo()
		{
			var scriptpath = string.Concat(path, "directive-asminfo", ".ahk");
			var exepath = "./directive-asminfo.exe";
			_ = RunScript(scriptpath, "directive-asminfo", false, true);
			Assert.IsTrue(File.Exists(exepath));
			var asm = Assembly.LoadFrom(exepath);
			var title = asm.GetCustomAttribute<AssemblyTitleAttribute>();
			Assert.IsNotNull(title);
			Assert.AreEqual(title.Title, "This is a title!");
			//
			var desc = asm.GetCustomAttribute<AssemblyDescriptionAttribute>();
			Assert.IsNotNull(desc);
			Assert.AreEqual(desc.Description, "This is a description!");
			//
			var config = asm.GetCustomAttribute<AssemblyConfigurationAttribute>();
			Assert.IsNotNull(config);
			Assert.AreEqual(config.Configuration, "This is a config!");
			//
			var comp = asm.GetCustomAttribute<AssemblyCompanyAttribute>();
			Assert.IsNotNull(comp);
			Assert.AreEqual(comp.Company, "This is a company!");
			//
			var prod = asm.GetCustomAttribute<AssemblyProductAttribute>();
			Assert.IsNotNull(prod);
			Assert.AreEqual(prod.Product, "This is a product!");
			//
			var copy = asm.GetCustomAttribute<AssemblyCopyrightAttribute>();
			Assert.IsNotNull(copy);
			Assert.AreEqual(copy.Copyright, "This is a copyright!");
			//
			var tm = asm.GetCustomAttribute<AssemblyTrademarkAttribute>();
			Assert.IsNotNull(tm);
			Assert.AreEqual(tm.Trademark, "This is a trademark!");
			//
			var ver = asm.GetCustomAttribute<AssemblyFileVersionAttribute>();
			Assert.IsNotNull(ver);
			Assert.AreEqual(ver.Version, "9.8.7.6");
			//
			Assert.IsTrue(TestScript("directive-asminfo", false));
		}

		[Test, Category("Directives")]
		public void IncludeAsmInfo() => Assert.IsTrue(TestScript("directive-include-asminfo", false));

		[Test, Category("Directives")]
		public void Include() => Assert.IsTrue(TestScript("directive-include", false));

		[Test, Category("Directives")]
		public void Defines() => Assert.IsTrue(TestScript("directive-defines", true));

		[Test, Category("Directives")]
		public void Misc() => Assert.IsTrue(TestScript("directive-misc", false));

		[Test, Category("Directives")]
		public void RequiresCapability()
		{
			// `#Requires capability <names>` must lower to a RequireCapabilities(...) call in the auto-exec
			// section. Unlike the runtime RequestCapabilities builtin, denial of a declared hard requirement
			// terminates startup instead of allowing the script to continue without the required permission.
			// A bare version requirement (`#Requires AutoHotkey v2.0`) must NOT emit one. Assert on the
			// generated C# (emitCode: true) so the check never contacts the permission daemon.
			var ch = new CompilerHelper();

			var (arr, code) = ch.CompileCodeToByteArray(
				"#Requires AutoHotkey v2.0\n#Requires capability ScreenCapture, InputMonitoring\nx := 1\n",
				"reqcap-emit", null, false, true);
			Assert.IsNotNull(arr, code);
			Assert.IsTrue(code.Contains("RequireCapabilities(\"ScreenCapture, InputMonitoring\")"),
				"the capability directive should emit a RequireCapabilities call; generated:\n" + code);

			// The plural alias also works.
			var (arrPl, codePl) = ch.CompileCodeToByteArray(
				"#Requires AutoHotkey v2.0\n#Requires capabilities InputMonitoring\nx := 1\n",
				"reqcap-plural", null, false, true);
			Assert.IsNotNull(arrPl, codePl);
			Assert.IsTrue(codePl.Contains("RequireCapabilities(\"InputMonitoring\")"),
				"the plural `#Requires capabilities` alias should emit a RequireCapabilities call");

			// Control: a version-only #Requires must NOT emit a capability request.
			var (arrNone, codeNone) = ch.CompileCodeToByteArray(
				"#Requires AutoHotkey v2.0\nx := 1\n", "reqcap-none", null, false, true);
			Assert.IsNotNull(arrNone, codeNone);
			Assert.IsFalse(codeNone.Contains("RequireCapabilities"),
				"a version-only #Requires must not emit RequireCapabilities");
		}

		[Test, Category("Directives")]
		public void WarnQuotesTheFileTheLineCameFrom()
		{
			// A #Warn line number counts from the file the offending line is IN, but the dialog excerpt was always
			// read from the MAIN script — so a warning raised in an #included file quoted whatever unrelated text the
			// main script happened to have at that line number, and named no file. Both the VarUnset and the
			// Unreachable warning are checked; assert on the generated C# (emitCode: true) so nothing has to run.
			var dir = Path.Combine(Path.GetTempPath(), "ks_warnfile_" + Guid.NewGuid().ToString("N"));
			_ = Directory.CreateDirectory(dir);

			try
			{
				// Line 3 of the include is the unset read; line 4 is unreachable. The main script's own lines 3/4 are
				// deliberately different text, so quoting the wrong file is unmistakable.
				var incPath = Path.Combine(dir, "warn-inc.ks");
				File.WriteAllText(incPath, "IncWarnHelper() {\n\treturn 1\n\tzzUnsetInInclude := zzUnsetInInclude + 1\n\tzzNeverRuns := 2\n}\n");
				var mainPath = Path.Combine(dir, "warn-main.ks");
				File.WriteAllText(mainPath, $"#Warn All, MsgBox\n#include \"{incPath}\"\nMainWarnHelper() {{\n\treturn zzUnsetInMain\n}}\n");

				var (arr, code) = new CompilerHelper().CompileCodeToByteArray(mainPath, "warn-main", null, false, true);
				Assert.IsNotNull(arr, code);

				// The included file's warnings quote ITS text at ITS line numbers, and name it.
				Assert.IsTrue(code.Contains("In warn-inc.ks:"), "an included file's warning should name the file; generated:\n" + code);
				Assert.IsTrue(code.Contains("3: zzUnsetInInclude := zzUnsetInInclude + 1"),
					"the VarUnset excerpt should quote the include's own line 3; generated:\n" + code);
				Assert.IsTrue(code.Contains("4: zzNeverRuns := 2"),
					"the Unreachable excerpt should quote the include's own line 4; generated:\n" + code);
				// The regression: the main script's line 3 must never be quoted for a warning raised in the include.
				Assert.IsFalse(code.Contains("3: MainWarnHelper"),
					"an included file's warning must not quote the main script at the same line number; generated:\n" + code);

				// A main-script warning is unchanged: its own text, no file header.
				Assert.IsTrue(code.Contains("4: return zzUnsetInMain"),
					"a main-script warning should still quote the main script; generated:\n" + code);
				Assert.IsFalse(code.Contains("In warn-main.ks:"),
					"a main-script warning should not be prefixed with a file name; generated:\n" + code);
			}
			finally
			{
				try { Directory.Delete(dir, true); } catch { }
			}
		}
	}
}
