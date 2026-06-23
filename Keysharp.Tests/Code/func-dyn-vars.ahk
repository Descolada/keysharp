#NoTrayIcon

x := 1
y := "x"

func()
{
	global x
	%y% := 123
}

func()

If (x == 123)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

If (y == "x")
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

x := 11
y11 := 123

func2()
{
	global y11
	y%x% := 222
}

func2()

If (x == 11)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

If (y11 == 222)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"
	
x := "unc"
y := 0

myfunc()
{
	global y := 999
}

myf%x%()

If (y == 999)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

x := "unc2"
y := 0

myfunc2(funcparam)
{
	global y := funcparam
}

myf%x%(123)

If (y == 123)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

x := "myfunc"
y := 0

%x%()

If (y == 999)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

x := "myfunc2"
y := 0

%x%(123)

If (y == 123)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

x := 1
y := "x"

localfunc()
{
	x := 2
	%y% := 123
	If (x == 123)
		FileAppend "pass", "*"
	else
		FileAppend "fail", "*"
}

localfunc()

If (x == 1)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

x := 1
y := "x"

staticfunc()
{
	static x := 2
	%y% := 123
	If (x == 123)
		FileAppend "pass", "*"
	else
		FileAppend "fail", "*"
}

staticfunc()

If (x == 1)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

x := 1
y := "x"

; Regression (Lowerer.AnyStmt): a %name% deref confined to a loop's ELSE clause must still bind to the
; function's local scope. The lowering walks the Else body (LoopFinally), so scope detection (BodyHas) must
; too — otherwise the write mislowers to the global store and the local is never set.
loopelsederef()
{
	x := 2
	y := "x"
	loop 0          ; body runs zero times, so the else clause runs
	{
		x := 9
	}
	else
	{
		%y% := 123   ; deref-write appearing only inside the else
	}
	If (x == 123)   ; the write landed in the function's local x
		FileAppend "pass", "*"
	else
		FileAppend "fail", "*"
}

loopelsederef()

If (x == 1)         ; ...and did not leak to the global x
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"