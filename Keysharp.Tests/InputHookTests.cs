using Keysharp.Builtins;
using Keysharp.Internals.Input;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Keysharp.Tests
{
	[TestFixture, NonParallelizable]
	public class InputHookTests : TestRunner
	{
		// Joined with '|' so the exact phrase list (including order and embedded commas) is compared.
		private static string MatchListOf(string matchList)
		{
			var io = (InputObject)Input.InputHook("", "", matchList);
			return string.Join("|", io.input.match);
		}

		// Bug fix: the match list is parsed faithfully like AHK's input_type::SetMatchList.
		// Two consecutive commas are a single literal comma; empty phrases are omitted; and an
		// empty match list yields no phrases (previously it produced a spurious "," phrase that
		// silently terminated every InputHook on a typed comma).
		[Test, Category("InputHook")]
		public void MatchListParsing()
		{
			Assert.AreEqual("", MatchListOf(""));                       // no phrases (was a spurious ",")
			Assert.AreEqual("abc", MatchListOf("abc"));
			Assert.AreEqual("abc|def", MatchListOf("abc,def"));
			Assert.AreEqual("abc", MatchListOf("abc,"));               // trailing comma omitted
			Assert.AreEqual("ab,cd", MatchListOf("ab,,cd"));           // double comma -> literal comma
			Assert.AreEqual("single,item", MatchListOf("single,,item"));     // doc example
			Assert.AreEqual("string1,|string2", MatchListOf("string1,,,string2")); // doc example
			Assert.AreEqual("btw|otoh|fl", MatchListOf("btw,otoh,fl"));
		}

		// Bug fix: a trailing I/L/T option (the last char of the options string) must not throw;
		// AHK reads the C-string null terminator safely and defaults the value.
		[Test, Category("InputHook")]
		public void OptionsTrailingNumericDoesNotThrow()
		{
			Assert.DoesNotThrow(() => Input.InputHook("I"));
			Assert.DoesNotThrow(() => Input.InputHook("L"));
			Assert.DoesNotThrow(() => Input.InputHook("T"));

			var i = (InputObject)Input.InputHook("I");
			Assert.AreEqual(1L, i.MinSendLevel); // 'I' with no number -> level 1

			var l = (InputObject)Input.InputHook("L");
			Assert.AreEqual(0, l.input.bufferLengthMax); // 'L' with no number -> 0
		}

		// Mouse-event support: VisibleMouseMove defaults to true (movement passes through), and
		// InputType.MouseIsNeeded becomes true once movement is being suppressed or a mouse button
		// carries Input key options (e.g. an end key). MouseIsNeeded is what makes Start() install
		// the low-level mouse hook in addition to the keyboard hook.
		[Test, Category("InputHook")]
		public void MouseHookNeedDetection()
		{
			var io = (InputObject)Input.InputHook("");
			Assert.AreEqual(true, io.VisibleMouseMove); // default: movement passes through
			Assert.IsFalse(io.input.MouseIsNeeded);     // keyboard-only hook needs no mouse hook

			io.VisibleMouseMove = false;                // suppressing movement requires the mouse hook
			Assert.AreEqual(false, io.VisibleMouseMove);
			Assert.IsTrue(io.input.MouseIsNeeded);

			var io2 = (InputObject)Input.InputHook("");
			Assert.IsFalse(io2.input.MouseIsNeeded);
			io2.KeyOpt("{LButton}", "+E");              // LButton as an end key also needs the mouse hook
			Assert.IsTrue(io2.input.MouseIsNeeded);
		}

		// InputType.KeyboardIsNeeded gates installing the keyboard hook, mirroring MouseIsNeeded for the
		// mouse hook. A default (text-suppressing) input needs it; a purely-mouse visible observer does not.
		[Test, Category("InputHook")]
		public void KeyboardHookNeedDetection()
		{
			var def = (InputObject)Input.InputHook("");  // default options suppress typed text
			Assert.IsTrue(def.input.KeyboardIsNeeded);   // ...so the keyboard hook is required
			Assert.IsFalse(def.input.MouseIsNeeded);

			var vis = (InputObject)Input.InputHook("V"); // visible: no text suppression
			Assert.IsTrue(vis.input.KeyboardIsNeeded);   // still a keyboard collector (not mouse-only)

			vis.VisibleMouseMove = false;                // now a pure mouse observer
			Assert.IsTrue(vis.input.MouseIsNeeded);
			Assert.IsFalse(vis.input.KeyboardIsNeeded);  // ...so the keyboard hook is no longer needed

			var ek = (InputObject)Input.InputHook("V", "{Enter}"); // visible + a keyboard end key
			Assert.IsTrue(ek.input.KeyboardIsNeeded);    // keyboard end key keeps the keyboard hook
		}
	}
}
