using static Keysharp.Internals.Input.Keyboard.KeyboardUtils;
using static Keysharp.Internals.Input.Keyboard.VirtualKeys;

namespace Keysharp.Internals.Input.Keyboard
{
	/// <summary>
	/// One physical key which firmware reports as a chord of several ordinary keys, rather than as a
	/// dedicated VK/SC — the Copilot and Office keys. Note this is the opposite relationship to a
	/// "composite hotkey" (`a &amp; b`), which pairs two separate keys into one trigger.
	/// </summary>
	/// <remarks>
	/// These names are deliberately absent from TextToVKandSC, so every consumer opts in explicitly. That
	/// is a fail-closed choice: a site which has not opted in reports "Invalid key name" rather than
	/// silently treating Copilot as a bare F23. It also means GetKeyVK and friends correctly refuse them,
	/// since a chord has no single VK to name.
	/// <para>The cost is that opting in is a decision per site. If you add somewhere that resolves a
	/// user-supplied key name, decide whether it should accept a chord; grep for ChordKeyDefinition.TryGet
	/// to see which places already do.</para>
	/// </remarks>
	internal readonly struct ChordKeyDefinition
	{
		private static readonly ChordKeyDefinition[] definitions =
		[
			new("Copilot", VK_F23, "F23", MOD_LWIN | MOD_LSHIFT),
			// LWin completes the Office chord and remains its suffix for the matching up hotkey.
			// Pairing the two hotkeys lets the hook fire the up variant even after a remap has
			// logically released the chord's modifiers.
			new("Office", VK_LWIN, "LWin", MOD_LCONTROL | MOD_LSHIFT | MOD_LALT | MOD_LWIN)
		];

		internal readonly string Name;
		internal readonly uint VK;
		internal readonly string KeyName;
		internal readonly uint ModifiersLR;

		private ChordKeyDefinition(string name, uint vk, string keyName, uint modifiersLR)
		{
			Name = name;
			VK = vk;
			KeyName = keyName;
			ModifiersLR = modifiersLR;
		}

		/// <summary>
		/// The chord's modifiers minus the trigger key's own bit, since a hotkey whose suffix is a modifier
		/// must not also list that modifier as a prefix (HotkeyDefinition's constructor strips such a
		/// self-reference anyway).
		/// </summary>
		internal uint HotkeyModifiersLR() => ModifiersLR & ~ModifierLRMaskFromVK(VK);

		internal bool IsDown(KeyStateTypes stateType)
		{
			if (stateType == KeyStateTypes.Toggle)
				return false;

			var ht = Runtime.Script.TheScript.HookThread;
			var modifiersLR = 0u;

			if (stateType != KeyStateTypes.Physical || !ht.TryGetTrackedModifierLRStatePhysical(out modifiersLR))
			{
				if (!Platform.Keyboard.TryGetModifierLRStateLogical(out modifiersLR))
					modifiersLR = ht.kbdMsSender.GetModifierLRState(true);
			}

			// A trigger which is itself one of the chord's modifiers is already covered by the mask test.
			var triggerDown = ModifierLRMaskFromVK(VK) != 0
							  || Builtins.Keyboard.ScriptGetKeyState(VK, stateType);
			return IsDown(modifiersLR, triggerDown);
		}

		internal bool IsDown(uint modifiersLR, bool triggerDown) =>
			(modifiersLR & ModifiersLR) == ModifiersLR && triggerDown;

		internal static int Count => definitions.Length;

		internal static ChordKeyDefinition At(int index) => definitions[index];

		internal static bool TryGet(string name, out ChordKeyDefinition definition)
			=> TryGet(name.AsSpan(), out definition, out _);

		internal static bool TryGet(ReadOnlySpan<char> name, out ChordKeyDefinition definition)
			=> TryGet(name, out definition, out _);

		/// <summary>
		/// Looks a chord up by name. The index identifies it for per-chord runtime state, which must be kept
		/// separately because one chord's trigger can be another's modifier (Office is triggered by LWin,
		/// which Copilot merely holds down).
		/// </summary>
		internal static bool TryGet(ReadOnlySpan<char> name, out ChordKeyDefinition definition, out int index)
		{
			for (var i = 0; i < definitions.Length; i++)
				if (name.Equals(definitions[i].Name, StringComparison.OrdinalIgnoreCase))
				{
					definition = definitions[i];
					index = i;
					return true;
				}

			definition = default;
			index = -1;
			return false;
		}
	}
}
