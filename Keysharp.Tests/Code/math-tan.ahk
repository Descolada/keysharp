#NoTrayIcon

PI := 3.1415926535897931

if (1.2246467991473532E-16 == Tan(-1 * PI))
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (-16331239353195370 == Tan(-0.5 * PI))
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (0 == Tan(0))
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (0 == Tan(-0))
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (16331239353195370 == Tan(0.5 * PI))
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"
	
if (-1.2246467991473532E-16 == Tan(1 * PI))
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

; The x64 and ARM64 C runtimes disagree by one ulp here (-1.63185168712879 vs
; -1.6318516871287898), so compare within a tolerance instead of bit-for-bit.
if (Abs(Tan(0.675 * PI) + 1.63185168712879) < 1E-14)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"