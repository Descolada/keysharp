using System.Collections.Generic;
using System.Linq;
using Keysharp.Builtins;
using Keysharp.Internals.Invoke;
using Keysharp.Runtime;
using Assert = NUnit.Framework.Legacy.ClassicAssert;
using CollectionAssert = NUnit.Framework.Legacy.CollectionAssert;

namespace Keysharp.Tests
{
	/// <summary>
	/// Named-argument paths that a .ks script cannot reach or cannot assert on: the compile-time
	/// <c>#Warn NamedArg</c> pass, and the receiver-offset matrix that decides which slot a name lands in.
	/// <para>
	/// The end-to-end behaviour lives in <c>Code/func-named-params.ahk</c>; this covers what that cannot.
	/// </para>
	/// </summary>
	public class NamedArgTests : TestRunner
	{
		private string Warnings(string source) =>
			// StdOut mode so the warnings land in the captured output instead of a dialog. Only the compile-time
			// pass is under test, so the scripts do as little as possible at run time.
			RunScript("#Warn NamedArg, StdOut\n" + source, "ks_warn_" + System.Guid.NewGuid().ToString("N"),
					  execute: true, exeout: false) ?? "";

		[Test, Category("Misc")]
		public void WarnNamedArgReportsAMisspelledName()
		{
			var w = Warnings("f(alpha, beta := 2) => alpha\nx := f(1, betaa: 3)\n");
			Assert.IsTrue(w.Contains("betaa"), "expected a warning naming the misspelling, got: " + w);
			Assert.IsTrue(w.Contains("alpha") && w.Contains("beta"), "the warning should list the valid names, got: " + w);
		}

		[Test, Category("Misc")]
		public void WarnNamedArgIsSilentOnACorrectName()
		{
			var w = Warnings("f(alpha, beta := 2) => alpha\nx := f(1, beta: 3)\n");
			Assert.IsFalse(w.Contains("not a parameter"), "a correct name must not warn, got: " + w);
		}

		[Test, Category("Misc")]
		public void WarnNamedArgReportsAParameterSuppliedTwice()
		{
			var w = Warnings("f(alpha, beta := 2) => alpha\nx := f(1, alpha: 3)\n");
			Assert.IsTrue(w.Contains("alpha") && w.Contains("more than once"),
						  "expected a supplied-more-than-once collision warning, got: " + w);
		}

		[Test, Category("Misc")]
		public void WarnNamedArgChecksConstructorCalls()
		{
			var w = Warnings("class W {\n__New(alpha := 1) {\nthis.a := alpha\n}\n}\nx := W(nosuch: 1)\n");
			Assert.IsTrue(w.Contains("nosuch"), "a constructor's named argument should be checked too, got: " + w);
		}

		[Test, Category("Misc")]
		public void WarnNamedArgChecksBuiltInConstructorCalls()
		{
			// A BUILT-IN class name resolves to its __New's real signature, so a constructor typo is caught at
			// build time -- and a correct name stays silent.
			var w = Warnings("try b := Buffer(nosuch: 1)\ntry c := Buffer(ByteCount: 4)\n");
			Assert.IsTrue(w.Contains("nosuch") && w.Contains("ByteCount") && w.Contains("FillByte"),
						  "expected a warning listing Buffer's constructor names, got: " + w);
			Assert.IsFalse(w.Contains("'ByteCount' is not"), "a correct constructor name must not warn, got: " + w);
		}

		[Test, Category("Misc")]
		public void WarnNamedArgIsSilentWhenTheCalleeCannotBeIdentified()
		{
			// Called through a variable: which function this is cannot be known until it runs, so the compile-time
			// pass must say nothing rather than guess.
			var w = Warnings("f(alpha) => alpha\ng := f\nx := g(nosuch: 1)\n");
			Assert.IsFalse(w.Contains("not a parameter"), "must not warn on a call through a variable, got: " + w);
		}

		/// <summary>
		/// <see cref="NamedArgBinder.ArgBase"/> decides which argument slot a parameter index maps to, and it
		/// differs per receiver convention. An off-by-one here silently writes the wrong parameter, so each shape
		/// is pinned rather than left to the end-to-end script, which cannot tell the shapes apart.
		/// </summary>
		[Test, Category("Misc")]
		public void ArgBaseCoversEveryReceiverConvention()
		{
			// A real instance method: the receiver is args[0] when it is not supplied out-of-band.
			var instance = MethodPropertyHolder.GetOrAdd(typeof(Keysharp.Builtins.Array).GetMethod("Get"));
			Assert.IsFalse(instance.IsStatic, "Array.Get should be a C# instance method");
			Assert.AreEqual(1, NamedArgBinder.ArgBase(instance, null), "instance method, receiver in args[0]");
			Assert.AreEqual(0, NamedArgBinder.ArgBase(instance, new Keysharp.Builtins.Array()), "instance method, receiver out-of-band");

			// The `object @this` convention: parameters[0] IS the receiver, so an out-of-band one shifts the rest down.
			var explicitThis = MethodPropertyHolder.GetOrAdd(typeof(Any).GetMethod("HasProp"));
			Assert.IsTrue(explicitThis.IsStatic, "Any.HasProp should be static with an explicit @this");
			Assert.AreEqual(0, NamedArgBinder.ArgBase(explicitThis, null), "@this convention, receiver in args[0]");
			Assert.AreEqual(-1, NamedArgBinder.ArgBase(explicitThis, new KeysharpObject()), "@this convention, receiver out-of-band");

			// A plain global function has no receiver at all.
			var global = MethodPropertyHolder.GetOrAdd(typeof(Keysharp.Builtins.Strings).GetMethod("SubStr"));
			Assert.AreEqual(0, NamedArgBinder.ArgBase(global, null), "plain static");
		}

		/// <summary>
		/// The COM layout is the mirror of the internal one: IDispatch wants the named values LEADING, parallel to
		/// namedParameters, where everything else here keeps them trailing. Pinned because the convention was
		/// established by experiment against a live IDispatch target, not from documentation.
		/// <para>
		/// What is asserted is the PARALLELISM, not an order: the container enumerates in Map order, so which name
		/// comes first is not a property this layout has -- only that <c>names[i]</c> names <c>vals[i]</c>, and that
		/// the positional arguments follow the whole named run.
		/// </para>
		/// </summary>
		[Test, Category("Misc")]
		public void ComLayoutPutsNamedValuesFirst()
		{
			var named = new object[] { "pos0", Script.NamedArgs("Key", "k", "Item", "v") };
			var vals = NamedArgBinder.ToComLayout(named, out var names);
			Assert.AreEqual(2, names.Length);
			Assert.AreEqual(3, vals.Length);
			var expected = new Dictionary<string, object> { ["Key"] = "k", ["Item"] = "v" };

			for (var i = 0; i < names.Length; i++)
				Assert.AreEqual(expected[names[i]], vals[i], $"names[{i}] must name vals[{i}]");

			Assert.AreEqual("pos0", vals[2], "the positional arguments follow the named run");

			// A purely positional call must come back untouched, with no names array to marshal.
			var positional = new object[] { "a", "b" };
			Assert.AreSame(positional, NamedArgBinder.ToComLayout(positional, out var none));
			Assert.IsEmpty(none);
		}

		/// <summary>
		/// The raw IDispatch path is the mirror image: the names come out of band, but their VALUES stay trailing,
		/// where rgvarg's reverse fill puts them at the front for rgdispidNamedArgs. Same parallelism rule.
		/// </summary>
		[Test, Category("Misc")]
		public void StripNamesLeavesValuesTrailing()
		{
			var named = new object[] { "pos0", Script.NamedArgs("Key", "k", "Item", "v") };
			var vals = NamedArgBinder.StripNames(named, out var names);
			Assert.AreEqual(2, names.Length);
			Assert.AreEqual("pos0", vals[0], "the positional arguments lead");
			var expected = new Dictionary<string, object> { ["Key"] = "k", ["Item"] = "v" };

			for (var i = 0; i < names.Length; i++)
				Assert.AreEqual(expected[names[i]], vals[1 + i], $"names[{i}] must name vals[{1 + i}]");
		}

		/// <summary>
		/// Clr overload selection is name-aware: a candidate that does not declare the name is skipped, and the
		/// candidate that survives is scored against its EXPANDED arguments (scoring the positional prefix alone
		/// picks whichever overload merely declares the names).
		/// </summary>
		[Test, Category("Misc")]
		public void ClrOverloadSelectionUsesTheNames()
		{
			var src = "#import \"Ks\" { Clr }\nClr.Load(\"System\")\n"
					  + "FileAppend(Clr.System.Convert.ToString(value: 255, toBase: 16) \"|\" "
					  + "Clr.System.Convert.ToString(value: 255) \"|\" Clr.System.Math.Round(value: 2.567, digits: 2), \"*\")\n";
			var got = RunScript(src, "ks_clr_" + System.Guid.NewGuid().ToString("N"), execute: true, exeout: false) ?? "";
			Assert.IsTrue(got.Contains("ff|255|2.57"), "expected 'ff|255|2.57', got: " + got);
		}

		/// <summary>
		/// The receiver must never be bindable by name, under either convention, and the variadic tail must not be
		/// either. Both exclusions live in the one map every binder consults.
		/// </summary>
		[Test, Category("Misc")]
		public void TheReceiverAndVariadicTailAreNotBindable()
		{
			var explicitThis = MethodPropertyHolder.GetOrAdd(typeof(Any).GetMethod("HasProp"));
			Assert.IsFalse(explicitThis.ParamIndexByName.ContainsKey("this"), "@this must not be bindable");
			Assert.IsTrue(explicitThis.ParamIndexByName.ContainsKey("name"), "the real parameter must be bindable");

			var variadic = MethodPropertyHolder.GetOrAdd(typeof(Keysharp.Builtins.Strings).GetMethod("Format"));
			Assert.IsTrue(variadic.IsVariadic, "Format should be variadic");
			Assert.IsFalse(variadic.ParamIndexByName.Values.Contains(variadic.variadicParamIndex),
						   "the variadic tail must not be bindable by name");
		}
	}
}
