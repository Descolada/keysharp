namespace Keysharp.Builtins
{
	/// <summary>
	/// Emulates AutoHotkey's A_EventInfo during a PCRE callout by lazily materialising a native
	/// <c>pcre_callout_block</c> and exposing its address.<br/>
	/// AutoHotkey uses PCRE 8.x (PCRE1, 16-bit), so existing callout scripts read this block via NumGet
	/// using the PCRE1 wide-char field offsets. Keysharp matches against PCRE2 (via PCRE.NET), whose
	/// callout block has a different layout, so this class rebuilds the legacy struct from the managed
	/// callout data, letting those scripts run unchanged.<br/>
	/// The native memory is built on first access (see <see cref="EventInfoValue"/>) and released by
	/// <see cref="Dispose"/> once the callout returns.
	/// </summary>
	internal sealed class PcreCalloutBlock : IDisposable
	{
		// Snapshot of the callout fields. Taken eagerly (cheap managed reads) so that lazily building the
		// native block never has to touch the PcreCallout, whose backing data is only valid during the call.
		private readonly int number;
		private readonly int startMatch;
		private readonly int currentPosition;
		private readonly int captureTop;
		private readonly int captureLast;
		private readonly int patternPosition;
		private readonly int nextItemLength;
		private readonly string subject;

		private nint block;       // The pcre_callout_block itself.
		private nint subjectPtr;  // Native UTF-16 copy of the subject string.
		private nint ovectorPtr;  // Native offset vector.
		private bool built;

		internal PcreCalloutBlock(PcreCallout callout, string haystack)
		{
			number          = (int)(long)callout.Number;
			startMatch      = (int)(long)callout.StartOffset;
			currentPosition = (int)(long)callout.CurrentOffset;
			captureTop      = (int)(long)callout.MaxCapture;
			captureLast     = (int)(long)callout.LastCapture;
			patternPosition = (int)(long)callout.PatternPosition;
			nextItemLength  = (int)(long)callout.NextPatternItemLength;
			subject         = haystack ?? "";
		}

		/// <summary>
		/// Builds the native block (once) and returns its address as a script-visible integer. Intended to be
		/// used as the factory for a <c>Lazy&lt;object&gt;</c> parked in A_EventInfo.
		/// </summary>
		internal object Materialize() => (long)Build();

		private nint Build()
		{
			if (built)
				return block;

			// PCRE1 (16-bit) pcre_callout_block layout. Pointer-sized fields force the usual alignment, so the
			// offsets differ between x86 and x64; both are derived from the pointer size here. These match the
			// offsets used in the AutoHotkey docs example (e.g. start_match at 12 + A_PtrSize*2).
			int ps = nint.Size;
			int offOffsetVector    = 8;
			int offSubject         = 8 + ps;
			int offSubjectLength   = 8 + ps * 2;
			int offStartMatch      = 12 + ps * 2;
			int offCurrentPosition = 16 + ps * 2;
			int offCaptureTop      = 20 + ps * 2;
			int offCaptureLast     = 24 + ps * 2;
			int offCalloutData     = Align(28 + ps * 2, ps);
			int offPatternPosition = offCalloutData + ps;
			int offNextItemLength  = offPatternPosition + 4;
			int offMark            = Align(offNextItemLength + 4, ps);
			int size               = offMark + ps;

			block = Marshal.AllocHGlobal(size);

			// Native, NUL-terminated UTF-16 copy of the subject for the `subject` pointer field.
			subjectPtr = Marshal.StringToHGlobalUni(subject);

			// Offset vector. During a callout AutoHotkey only fills [0]=start_match and [1]=current_position
			// (see AutoHotkey source/lib/regex.cpp); size it like a PCRE ovector and zero the remainder so a
			// script reading further entries gets 0 rather than garbage.
			int ovecCount = 3 * (Math.Max(captureTop, 0) + 1);
			ovectorPtr = Marshal.AllocHGlobal(ovecCount * sizeof(int));
			Marshal.Copy(new int[ovecCount], 0, ovectorPtr, ovecCount);
			Marshal.WriteInt32(ovectorPtr, 0, startMatch);
			Marshal.WriteInt32(ovectorPtr, sizeof(int), currentPosition);

			Marshal.WriteInt32(block, 0, 2);                            // version (2 => mark field present)
			Marshal.WriteInt32(block, 4, number);                       // callout_number
			Marshal.WriteIntPtr(block, offOffsetVector, ovectorPtr);
			Marshal.WriteIntPtr(block, offSubject, subjectPtr);
			Marshal.WriteInt32(block, offSubjectLength, subject.Length);
			Marshal.WriteInt32(block, offStartMatch, startMatch);
			Marshal.WriteInt32(block, offCurrentPosition, currentPosition);
			Marshal.WriteInt32(block, offCaptureTop, captureTop);
			Marshal.WriteInt32(block, offCaptureLast, captureLast);
			Marshal.WriteIntPtr(block, offCalloutData, 0);              // callout_data (unused, NULL)
			Marshal.WriteInt32(block, offPatternPosition, patternPosition);
			Marshal.WriteInt32(block, offNextItemLength, nextItemLength);
			Marshal.WriteIntPtr(block, offMark, 0);                     // mark (NULL)

			built = true;
			return block;
		}

		private static int Align(int n, int a) => (n + a - 1) & ~(a - 1);

		public void Dispose()
		{
			if (!built)
				return;

			if (block != 0) { Marshal.FreeHGlobal(block); block = 0; }
			if (subjectPtr != 0) { Marshal.FreeHGlobal(subjectPtr); subjectPtr = 0; }
			if (ovectorPtr != 0) { Marshal.FreeHGlobal(ovectorPtr); ovectorPtr = 0; }
			built = false;
		}
	}
}
