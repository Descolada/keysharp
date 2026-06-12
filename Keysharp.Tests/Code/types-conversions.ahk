#NoTrayIcon

; Throwing numeric conversions (AHK v2 TypeError parity).

caught := false
try
	Abs("xyz")
catch TypeError
	caught := true

if (caught)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

caught := false
try
	Round("abc")
catch TypeError
	caught := true

if (caught)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

caught := false
try
	Mod("abc", 2)
catch TypeError
	caught := true

if (caught)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

caught := false
try
	Integer("abc")
catch TypeError
	caught := true

if (caught)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

caught := false
try
	Float("x")
catch TypeError
	caught := true

if (caught)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

; A hex string without the 0x prefix is not a number.
caught := false
try
	Number("beef")
catch TypeError
	caught := true

if (caught)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

; Floats coerce (truncate toward zero) where AHK allows them.

if (Integer("3.9") = 3)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (Integer(3.5) = 3)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (Integer(-3.5) = -3)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (Number("1e5") = 100000.0)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (Number("0x10") = 16)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (Mod(7.5, 2) = 1.5)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (Floor(7 / 2) = 3)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (SubStr("ABCDEFGH", 6 / 2) = "CDEFGH")
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (Round(3.567, 1) = 3.6)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

; Float-to-string formatting: whole-valued Floats keep a trailing .0, Integers do not.

if (String(760 / 2) == "380.0")
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (String(0.0) == "0.0")
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (String(0 * 5) == "0")
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if ("" (1280 / 3) == "426.6666666666667")
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

; Fractional Floats stay truthy.

x := 0.5

if (x)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

; Numeric property setters validate their input and truncate Floats.

caught := false
try
	A_SendLevel := "abc"
catch TypeError
	caught := true

if (caught)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

A_SendLevel := 5.0

if (A_SendLevel = 5)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

A_SendLevel := 0
