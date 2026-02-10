optfunc1(a?)
{
	return a
}

val := optfunc1() ?? 1

if (val == 1)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

val := ""
fo := Func("optfunc1")
val := fo() ?? 2

if (val == 2)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

optfunc2(a, b?)
{
	return b
}

val := optfunc2(123) ?? 3

if (val == 3)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

val := ""
fo := Func("optfunc2")
val := fo(123) ?? 4

if (val == 4)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

ga :=
gb :=
gc := "" 

optfunc3(a, b, c?)
{
	global
	ga := a
	gb := b
	gc := c
	return c
}

val := optfunc3(123, 456) ?? 5

if (ga == 123)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (gb == 456)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"
	
if (gc is unset)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (val == 5)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

ga :=
gb :=
gc :=
val := ""
fo := Func("optfunc3")
val := fo(123, 456) ?? 6

if (ga == 123)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (gb == 456)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"
	
if (gc is unset)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (val == 6)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

ga :=
gb :=
gc :=
val := ""
val := optfunc3(123, 456, 789) ?? 7

if (ga == 123)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (gb == 456)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"
	
if (gc == 789)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (val == 789)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

ga :=
gb :=
gc :=
val := ""
fo := Func("optfunc3")
val := fo(123, 456, 789)

if (ga == 123)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (gb == 456)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"
	
if (gc == 789)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (val == 789)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

class optfuncclass
{
	f1(a?)
	{
		return a
	}

	f2(a, b?)
	{
		return b
	}
	
	f3(a, b, c?)
	{
		global
		ga := a
		gb := b
		gc := c
		return c
	}
}

classobj := optfuncclass()

val := classobj.f1() ?? 1

if (val == 1)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

val := classobj.f2(123) ?? 2

if (val == 2)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

ga :=
gb :=
gc := "" 
val := ""
val := classobj.f3(123, 456) ?? 3

if (ga == 123)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (gb == 456)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"
	
if (gc is unset)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (val == 3)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

ga :=
gb :=
gc :=
val := ""
val := classobj.f3(123, 456, 789) ?? 4

if (ga == 123)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (gb == 456)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"
	
if (gc == 789)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (val == 789)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"
	
optreffunc(a?, &b)
{
	b := a
}

val1 := ""
val2 := ""
b := unset
optreffunc(,&val2)

if (b is unset)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"
