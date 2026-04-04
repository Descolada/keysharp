namespace Keysharp.Internals.Input.Keyboard
{
	// Fail = 0 to remind that Fail should have the value zero instead of something arbitrary
	// because some callers may simply evaluate the return result as true or false
	// (and false is a failure).
	internal enum ResultType
	{
		Fail = 0,
		Ok,
		Warn = Ok,
		CriticalError,
		ConditionTrue,
		ConditionFalse,
		LoopBreak,
		LoopContinue,
		EarlyReturn,
		EarlyExit,
		FailOrOk
	}

	internal enum SendModes
	{
		Event,
		Input,
		Play,
		InputThenPlay,
		Invalid
	}

	internal enum SendRawModes
	{
		NotRaw,
		Raw,
		RawText
	}
}
