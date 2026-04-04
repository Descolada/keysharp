
Eq(a, b)
{
	return Round(a, 12) == Round(b, 12)
}

if (Eq(3.1415926535897931, ACos(-1)))
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (Eq(2.0943951023931957, ACos(-0.5)))
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (Eq(1.5707963267948966, ACos(0)))
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (Eq(1.0471975511965979, ACos(0.5)))
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (Eq(0, ACos(1)))
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (Eq(0.8298316245918765, ACos(0.675)))
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"
