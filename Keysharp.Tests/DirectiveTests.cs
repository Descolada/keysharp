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
		public void UseHookInterception()
		{
			// `#UseHook Interception` (and the quoted form) select the Interception backend for the whole
			// script: unlike `#UseHook 0/1`, it must NOT emit a positional MainScript.ForceKeybdHook
			// assignment, and it must be threaded into the generated Script constructor call so
			// CreateHookThread can resolve it before any hook is installed -- regardless of where in the
			// script the directive appears (see Lowerer._hookBackend/_requireHookBackend).
			var ch = new CompilerHelper();

			var (arrUnquoted, codeUnquoted) = ch.CompileCodeToByteArray(
				"#UseHook Interception\nx := 1\n", "usehook-interception", null, false, true);
			Assert.IsNotNull(arrUnquoted, codeUnquoted);
			Assert.IsFalse(codeUnquoted.Contains("ForceKeybdHook"),
				"#UseHook Interception must not emit the positional ForceKeybdHook assignment");
			Assert.IsTrue(codeUnquoted.Contains("new Keysharp.Runtime.Script(typeof(Program), null, \"Interception\")"),
				"#UseHook Interception should pass the backend name to the Script constructor; generated:\n" + codeUnquoted);

			var (arrQuoted, codeQuoted) = ch.CompileCodeToByteArray(
				"#UseHook \"Interception\"\nx := 1\n", "usehook-interception-quoted", null, false, true);
			Assert.IsNotNull(arrQuoted, codeQuoted);
			Assert.IsTrue(codeQuoted.Contains("new Keysharp.Runtime.Script(typeof(Program), null, \"Interception\")"),
				"the quoted form #UseHook \"Interception\" must behave identically to the unquoted form; generated:\n" + codeQuoted);

			// Control: the existing numeric/boolean form is unchanged.
			var (arrBool, codeBool) = ch.CompileCodeToByteArray(
				"#UseHook 1\nx := 1\n", "usehook-bool", null, false, true);
			Assert.IsNotNull(arrBool, codeBool);
			Assert.IsTrue(codeBool.Contains("ForceKeybdHook"),
				"#UseHook 1 must keep emitting the positional ForceKeybdHook assignment");
			Assert.IsFalse(codeBool.Contains("\"Interception\""),
				"#UseHook 1 must not select the Interception backend");

			// `#Requires capability interception` converts the default graceful fallback into a hard
			// requirement, regardless of whether it appears before or after #UseHook Interception.
			var (arrBefore, codeBefore) = ch.CompileCodeToByteArray(
				"#Requires capability interception\n#UseHook Interception\nx := 1\n", "usehook-req-before", null, false, true);
			Assert.IsNotNull(arrBefore, codeBefore);
			Assert.IsTrue(codeBefore.Contains("new Keysharp.Runtime.Script(typeof(Program), null, \"Interception\", true)"),
				"#Requires capability interception before #UseHook Interception should still set requireHookBackend; generated:\n" + codeBefore);

			var (arrAfter, codeAfter) = ch.CompileCodeToByteArray(
				"#UseHook Interception\n#Requires capability interception\nx := 1\n", "usehook-req-after", null, false, true);
			Assert.IsNotNull(arrAfter, codeAfter);
			Assert.IsTrue(codeAfter.Contains("new Keysharp.Runtime.Script(typeof(Program), null, \"Interception\", true)"),
				"#Requires capability interception after #UseHook Interception must behave identically; generated:\n" + codeAfter);
		}
	}
}
