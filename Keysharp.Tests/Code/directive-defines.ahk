#NoTrayIcon

#define SOMETHING
#define SOMETHING_UNDERSCORE

x := 10

#if WINDOWS
	x *= 2
#endif

#if WINDOWS
	if (x == 20)
#elif LINUX || OSX
	if (x == 10)
#endif
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

x := 10

#if LINUX || OSX
	x *= 2
#endif

#if WINDOWS
	if (x == 10)
#elif LINUX || OSX
	if (x == 20)
#endif
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

x := 10

#if 1
	x := 100
#else
	x := 200
#endif

if (x == 100)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

x := 10

#if 0
	x := 100
#else
	x := 200
#endif

if (x == 200)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

x := 10

#if (((WINDOWS || LINUX || OSX) && 0))
	x *= 2
#endif

if (x == 10)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

x := 10

#if (((WINDOWS || LINUX || OSX) && 1))
	x *= 2
#endif

if (x == 20)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

; False outer with true inner.
x := 10

#if LINUX
	#if LINUX
		x := 20
	#else
		x := 1
	#endif
#endif

#if WINDOWS
	if (x == 10)
#elif LINUX
	if (x == 20)
#elif OSX
	if (x == 10)
#endif
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

; True outer with false inner.
x := 10

#if WINDOWS
	#if LINUX
		x := 20
	#else
		x := 1
	#endif
#endif

#if WINDOWS
	if (x == 1)
#elif LINUX
	if (x == 10)
#elif OSX
	if (x == 10)
#endif
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

str := ""

#if WINDOWS
	#if WINDOWS
		str .= "windows"
	#elif LINUX
		str .= "linux"
	#elif OSX
		str .= "osx"
	#else
		str .= "unknown"
	#endif
#elif LINUX
	str .= "linux"
#elif OSX
	str .= "osx"
#else
	str .= "unknown"
#endif

#if WINDOWS
	if (str == "windows")
#elif LINUX
	if (str == "linux")
#elif OSX
	if (str == "osx")
#endif
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

str := ""

#if !WINDOWS
    str := "not windows"
#elif !LINUX
    str := "not linux"
#else
	str := "not unknown"
#endif

#if WINDOWS
	if (str == "not linux")
#else
	if (str == "not windows")
#endif
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

x := 10

#if (SOMETHING)
	x *= 2
#endif

if (x == 20)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

x := 10

#if !(SOMETHING)
	x *= 2
#endif

if (x == 10)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"
	
x := 10

#if SOMETHING_UNDERSCORE
	x *= 2
#endif

if (x == 20)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

; Test undefining something that has been predefined.
x := false

#undef SOMETHING

#if SOMETHING
	x := true
#endif

if (!x)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

x := false

#define SOMETHING

#if SOMETHING
	x := true
#endif

if (x)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

; true and false are value keywords inside a condition too, not merely undefined symbols: #if true must
; keep its block, and #if false must drop it (the usual way to comment out a whole region).
x := false

#if true
	x := true
#endif

if (x)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

x := true

#if false
	x := false
#endif

if (x)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

; A defined symbol still wins over the literal. Symbol names are case-insensitive, so #define FALSE names the same
; thing as the literal false; if the literal won, this branch would silently stop being taken.
x := false

#define FALSE

#if FALSE
	x := true
#endif

if (x)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

#undef FALSE

; Every spelling of zero is false, not just "0" and "0.0".
x := true

#if 0x0
	x := false
#endif

#if 00
	x := false
#endif

#if 0.00
	x := false
#endif

if (x)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

; ...and a nonzero hex literal is still true.
x := false

#if 0x1
	x := true
#endif

if (x)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"
