#NoTrayIcon

x := 123
y := x ?? ""

if (y = 123)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

nafunc(p?)
{
	if ((p ?? 456) == 456)
		FileAppend "pass", "*"
	else
		FileAppend "fail", "*"
}

nafunc(unset)

z :=
y := z ?? 456

if (y = 456)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

tot := 0

nafunc2(a, b, c)
{
	global tot
	tot += a(1)
	tot += b()
	tot += c(3)
}

nafunc2((o) => o ?? 11, (o?) => o ?? 22, (o?) => o ?? 33)

If (tot == 26)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

x := 123
yy := unset
m := { one : x ?? 456,  two : yy ?? 789}

if (m.one = 123)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (m.two = 789)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

x := 123

if ((x ?? 456) == 123)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

x := unset
x ??= Array()

if (x is Array)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

; Optional chaining tests
optObj := unset
optVal := optObj?.prop
if !IsSet(optVal)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

optObj := { prop: { inner: 7 } }
optVal := optObj?.prop.inner
if (optVal = 7)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

optObj := { prop: unset }
optVal := optObj?.prop?.inner
if !IsSet(optVal)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

; v2.1-alpha.30 removed `a?.[i]` and `a?.()` in favour of `(a?)[i]` and `(a?)()`.
optArr := [10, 20]
optVal := (optArr?)[2]
if (optVal = 20)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

optArr := unset
optVal := (optArr?)[1]
if !IsSet(optVal)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

optCount := 0
Bump()
{
	global optCount
	optCount += 1
	return 42
}

class OptClass
{
	M(x)
	{
		return x + 1
	}
}

optObj := unset
optVal := optObj?.M(Bump())
if (optCount = 0 && !IsSet(optVal))
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

optObj := OptClass()
optVal := optObj?.M(Bump())
if (optCount = 1 && optVal = 43)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

optObj := unset
optVal := optObj?.prop ?? 55
if (optVal = 55)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

; Optional chaining with ternary + coalesce (both branches)
optObj := unset
dummyObj := { prop: 1 }
optVal := (true ? optObj?.prop : dummyObj?.prop) ?? 66
if (optVal = 66)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

optVal := (false ? dummyObj?.prop : optObj?.prop) ?? 77
if (optVal = 77)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

; v2.1-alpha.29: the maybe operator short-circuits most operators, so an unset operand
; propagates out of the whole expression without evaluating the rest of it.
sideEffects := 0

Bump2()
{
	global sideEffects
	return ++sideEffects
}

missing := unset

optVal := missing?.value + Bump2()
if (!IsSet(optVal) && sideEffects = 0)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

optVal := -(missing?) + Bump2()
if (!IsSet(optVal) && sideEffects = 0)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

optVal := (missing?) ? Bump2() : Bump2()
if (!IsSet(optVal) && sideEffects = 0)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

optCallable := unset
optVal := (optCallable?)(Bump2())
if (!IsSet(optVal) && sideEffects = 0)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

optIndexable := unset
optVal := (optIndexable?)[Bump2()]
if (!IsSet(optVal) && sideEffects = 0)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

; An assignment through an unset target is skipped entirely, value expression included.
missing?.value := Bump2()
if (sideEffects = 0)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

; v2.1-alpha.30: `x?.%y%` is valid syntax, not a load-time error.
memberName := "value"
optVal := missing?.%memberName%
if !IsSet(optVal)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

; Short-circuiting must not swallow a set operand: the same forms still compute normally.
present := { value: 5 }
optVal := present?.value + Bump2()
if (optVal = 6 && sideEffects = 1)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"
