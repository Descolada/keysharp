using Keysharp.Builtins;
namespace Keysharp.Internals.Input.Keyboard
{
	internal class KeyType
	{
		internal const int AS_PASSTHROUGH_PREFIX = -1;
		internal const int AS_PREFIX = 1;
		internal const int AS_PREFIX_FOR_HOTKEY = 2;
		internal const int PREFIX_ACTUAL = 1; // Values for used_as_prefix below, for places that need to distinguish between type of prefix.
		internal const int PREFIX_FORCED = 2; // v1.0.44: Added so that a neutral hotkey like Control can be forced to fire on key-up even though it isn't actually a prefix key.
		internal uint asModifiersLR = 0u;// If this key is a modifier, this will have the corresponding bit(s) for that key.
		internal bool downPerformedAction;
		internal uint firstHotkey;
		internal ToggleStates forceToggle = null;  // Pointer to a global variable for toggleable keys only.  NULL for others.
		internal byte downWasSuppressed = 0;// Bitmask of SendLevel buckets whose down-event was suppressed (thus their matching up-event should be too).
		internal uint hotkeyToFireUponRelease; // A up-event hotkey queued by a prior down-event.
		internal bool isDown;// this key is currently down.
		// Which modifiers were held at the moment this key was pressed, or null while it is not down. Used by
		// hotkeys whose composite prefix carries modifiers (`<^a & b::`): the answer is fixed at that moment
		// and kept until release, so a modifier let go while the key is still held does not disarm the
		// combination. That matters for keys whose firmware asserts the modifiers itself and drops them
		// microseconds later, which is how the Copilot and Office keys behave.
		//
		// This is written only alongside isDown, because the two are one fact: the modifiers belong to the
		// press that isDown describes. Keeping them together means every path which ends a press ends both,
		// so there is no second lifecycle to leak.
		internal uint? downModifiersLR;
		// Whether any registered hotkey uses this key as a composite prefix carrying modifiers, so a press of
		// it needs the sample above. Derived in ChangeHookState beside the other per-key attributes.
		internal bool samplesPrefixModifiers;
		internal bool itPutAltDown;// this key resulted in ALT being pushed down (due to alt-tab).
		internal bool itPutShiftDown;
		internal uint noSuppress;
		internal bool scTakesPrecedence;// used only by the scan code array: this scan code should take precedence over vk.
		internal bool usedAsKeyUp;
		internal byte usedAsPrefix; // Bitwise PREFIX_* flags describing whether a given virtual key or scan code is used as a prefix.
		internal bool usedAsSuffix;// The first hotkey using this key as a suffix.

		// Whether this suffix also has an enabled key-up hotkey.
		// Contains bitwise flags such as NO_SUPPRESS_PREFIX.
		// this key resulted in SHIFT being pushed down (due to shift-alt-tab).
		// Whether the down-event for a key was suppressed (thus its up-event should be too).
		// The values for "was_just_used" (zero is the initialized default, meaning it wasn't just used):
		internal int wasJustUsed;

		internal uint Pos { get; }

		// a non-modifier key of any kind was pressed while this prefix key was down.

		internal KeyType(uint p)
		{
			Pos = p;
		}

		internal void ResetKeyTypeAttrib()
		{
			firstHotkey = HotkeyDefinition.HOTKEY_ID_INVALID;
			usedAsPrefix = 0;
			samplesPrefixModifiers = false;
			usedAsSuffix = false;
			usedAsKeyUp = false;
			noSuppress = 0;
			scTakesPrecedence = false;
		}

		internal void ResetKeyTypeState()
		{
			isDown = false;
			downModifiersLR = null;
			itPutAltDown = false;
			itPutShiftDown = false;
			downPerformedAction = false;
			downWasSuppressed = 0;
			wasJustUsed = 0;
			hotkeyToFireUponRelease = HotkeyDefinition.HOTKEY_ID_INVALID;
			// ABOVE line was added in v1.0.48.03 to fix various ways in which the hook didn't receive the key-down
			// hotkey that goes with this key-up, resulting in hotkey_to_fire_upon_release being left at its initial
			// value of zero (which is a valid hotkey ID).  Examples include:
			// The hotkey command being used to create a key-up hotkey while that key is being held down.
			// The script being reloaded or (re)started while the key is being held down.
		}

		/// <summary>
		/// This is all done because C# doesn't allow class members to be references.
		/// </summary>
		/// <param name="vk"></param>
		/// <returns></returns>
		internal ToggleValueType? ToggleVal(uint vk)
		{
			var key = (Keys)vk;

			if (forceToggle != null) // Key is a toggleable key.
			{
				if (key == Keys.Scroll)
					return forceToggle.forceScrollLock;
				else if (key == Keys.Capital)
					return forceToggle.forceCapsLock;
				else if (key == Keys.NumLock)
					return forceToggle.forceNumLock;
			}

			return null;
		}
	}
}
