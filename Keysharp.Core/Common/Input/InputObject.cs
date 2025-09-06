﻿namespace Keysharp.Core.Common.Input
{
	public class InputObject : KeysharpObject
	{
		internal InputType input;
		private IFuncObj onChar, onEnd, onKeyDown, onKeyUp;

		public object BackspaceIsUndo
		{
			get => (LongPrimitive)input.backspaceIsUndo;
			set => input.backspaceIsUndo = value.Ab();
		}

		public object BeforeHotkeys
		{
			get => (LongPrimitive)input.beforeHotkeys;
			set => input.beforeHotkeys = value.Ab();
		}

		public object BufferLengthMax
		{
			get => (LongPrimitive)input.bufferLengthMax;
			set => input.bufferLengthMax = value.Ai();
		}

		public object CaseSensitive
		{
			get => (LongPrimitive)input.caseSensitive;
			set => input.caseSensitive = value.Ab();
		}

		public object EndCharMode
		{
			get => (LongPrimitive)input.endCharMode;
			set => input.endCharMode = value.Ab();
		}

		public StringPrimitive EndKey
		{
			get
			{
				if (input.status == InputStatusType.TerminatedByEndKey)
				{
					var str = "";
					_ = input.GetEndReason(ref str);
					return str;
				}

				return DefaultObject;
			}
		}

		public StringPrimitive EndMods
		{
			get
			{
				var sb = new StringBuilder(8);

				for (var i = 0; i < 8; ++i)
					if ((input.endingMods & (1 << i)) != 0)
					{
						_ = sb.Append(KeyboardMouseSender.ModLRString[i * 2]);
						_ = sb.Append(KeyboardMouseSender.ModLRString[(i * 2) + 1]);
					}

				return sb.ToString();
			}
		}

		public StringPrimitive EndReason
		{
			get
			{
				string str = null;
				return input.GetEndReason(ref str);
			}
		}

		public object FindAnywhere
		{
			get => (LongPrimitive)input.findAnywhere;
			set => input.findAnywhere = value.Ab();
		}

		public LongPrimitive InProgress => input.InProgress();

		public StringPrimitive Input => input.buffer;

		public StringPrimitive Match => input.status == InputStatusType.TerminatedByMatch && input.endingMatchIndex < input.match.Count
		? input.match[input.endingMatchIndex]
		: "";

		public object MinSendLevel
		{
			get => (LongPrimitive)input.minSendLevel;

			set
			{
				var val = value.Al();

				if (val < 0 || val > 101)
				{
					_ = Errors.ValueErrorOccurred($"Cannot set InputObject.MinSendLevel to a value outside of the range 0 - 101 ({value}).");
					return;
				}

				input.minSendLevel = (uint)val;
			}
		}

		public object NotifyNonText
		{
			get => (LongPrimitive)input.notifyNonText;
			set => input.notifyNonText = value.Ab();
		}

		public object OnChar
		{
			get => onChar;
			set => onChar = Functions.GetFuncObj(value, null, true);
		}

		public object OnEnd
		{
			get => onEnd;
			set => onEnd = Functions.GetFuncObj(value, null, true);
		}

		public object OnKeyDown
		{
			get => onKeyDown;
			set => onKeyDown = Functions.GetFuncObj(value, null, true);
		}

		public object OnKeyUp
		{
			get => onKeyUp;
			set => onKeyUp = Functions.GetFuncObj(value, null, true);
		}

		public object Timeout
		{
			get => (DoublePrimitive)(input.timeout / 1000.0);

			set
			{
				input.timeout = (int)(value.ParseDouble() * 1000);

				if (input.InProgress() && input.timeout > 0)
					input.SetTimeoutTimer();
			}
		}

		public object TranscribeModifiedKeys
		{
			get => (LongPrimitive)input.transcribeModifiedKeys;
			set => input.transcribeModifiedKeys = value.Ab();
		}

		public object VisibleNonText
		{
			get => (LongPrimitive)input.visibleNonText;
			set => input.visibleNonText = value.Ab();
		}

		public object VisibleText
		{
			get => (LongPrimitive)input.visibleText;
			set => input.visibleText = value.Ab();
		}

		public InputObject(params object[] args) : base(args) { }

		public override object __New(params object[] args)
		{
			var options = args[0].ToString();
			var endKeys = args[1].ToString();
			var matchList = args[2].ToString();
			input = new InputType(this, options, endKeys, matchList);
			return DefaultObject;
		}

		public object KeyOpt(object obj0, object obj1)
		{
			var keys = obj0.As();
			var options = obj1.As();
			var adding = true;
			uint flag = 0U, addFlags = 0u, removeFlags = 0u;

			for (var i = 0; i < options.Length; ++i)
			{
				switch (char.ToUpper(options[i]))
				{
					case '+': adding = true; continue;

					case '-': adding = false; continue;

					case ' ': case '\t': continue;

					case 'E': flag = HookThread.END_KEY_ENABLED; break;

					case 'I': flag = HookThread.INPUT_KEY_IGNORE_TEXT; break;

					case 'N': flag = HookThread.INPUT_KEY_NOTIFY; break;

					case 'S':
						flag = HookThread.INPUT_KEY_SUPPRESS;

						if (adding)
							removeFlags |= HookThread.INPUT_KEY_VISIBLE;

						break;

					case 'V':
						flag = HookThread.INPUT_KEY_VISIBLE;

						if (adding)
							removeFlags |= HookThread.INPUT_KEY_SUPPRESS;

						break;

					case 'Z': // Zero (reset)
						addFlags = 0;
						removeFlags = HookThread.INPUT_KEY_OPTION_MASK;
						continue;

					default:
						_ = Errors.ValueErrorOccurred($"Invalid option.", options);
						return DefaultObject;
				}

				if (adding)
					addFlags |= flag; // Add takes precedence over remove, so remove_flags isn't changed.
				else
				{
					removeFlags |= flag;
					addFlags &= ~flag; // Override any previous add.
				}
			}

			if (string.Compare(keys, "{All}", true) == 0)
			{
				// Could optimize by using memset() when remove_flags == 0xFF, but that doesn't seem
				// worthwhile since this mode is already faster than SetKeyFlags() with a single key.
				for (var i = 0; i < input.keyVK.Length; ++i)
					input.keyVK[i] = (input.keyVK[i] & ~removeFlags) | addFlags;

				for (var i = 0; i < input.keySC.Length; ++i)
					input.keySC[i] = (input.keySC[i] & ~removeFlags) | addFlags;
			}

			input.SetKeyFlags(keys, false, removeFlags, addFlags);

			return DefaultObject;
		}

		public object Start()
		{
			if (!input.InProgress())
			{
				input.buffer = "";
				input.InputStart();
			}
			return DefaultObject;
		}

		public object Stop()
		{
			if (input.InProgress())
				input.Stop();
			return DefaultObject;
		}

		public object Wait(object obj)
		{
			var ms = obj.Ad(double.MaxValue) * 1000.0;
			var tickStart = DateTime.UtcNow;

			while (input.InProgress() && (DateTime.UtcNow - tickStart).TotalMilliseconds < ms)
				_ = Flow.Sleep(20);
			return DefaultObject;
		}
	}
}