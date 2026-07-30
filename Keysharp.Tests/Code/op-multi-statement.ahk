#NoTrayIcon

x := 10
y := 20
z := 30

x++, y++, z++

if (x = 11)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (y = 21)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (z = 31)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

; Only the last item is the sequence's value; the others are evaluated for their side effects, so a
; call which returns no value is simply discarded rather than raising.
sideCount := 0

NoValue() {
}

Side() {
	global sideCount += 1
}

v := (NoValue(), 5)

if (v = 5)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

Side(), NoValue(), Side()

if (sideCount = 2)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

a := 1, NoValue(), b := 2

if (a = 1 && b = 2)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

; The distinction only becomes observable in v2.1 mode, where a call with no return value yields
; unset rather than blank: a discarded one is still fine, but the consumed final item raises.
DiscardedUnset21() {
	#Requires AutoHotkey v2.1-alpha
	NoValue21() {
	}

	return (NoValue21(), 5)
}

if (DiscardedUnset21() = 5)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

ConsumedUnset21() {
	#Requires AutoHotkey v2.1-alpha
	NoValue21() {
	}

	return (5, NoValue21())
}

threw := false
try
	v := ConsumedUnset21()
catch UnsetError
	threw := true

if threw
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"
