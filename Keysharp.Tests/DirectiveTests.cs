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
	}
}
