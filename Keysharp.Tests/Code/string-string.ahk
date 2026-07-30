#NoTrayIcon

x := 123
y := String(x)

if (y = "123")
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

x := "123"
y := String(x)

if (y = "123")
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

x := 1.234
y := String(x)

if (y = "1.234")
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

; String(x) returns whatever x.ToString() returned, so a ToString() with no return value makes
; String() return no value too, rather than raising. [v2.1-alpha.30]
ToStringNoValue(this) {
}

ToStringValue(this) => "stringified"

; In v2.0 mode "no value" is blank, so the observable part is that this does not raise.
; The v2.1 counterpart, where it yields unset, is covered by module-compatibility-mode.
noStringResult := {}
noStringResult.DefineProp("ToString", {call: ToStringNoValue})
y := String(noStringResult)

if (y = "")
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

stringResult := {}
stringResult.DefineProp("ToString", {call: ToStringValue})
y := String(stringResult)

if (y = "stringified")
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"
