
Eq(a, b)
{
	return Round(a, 12) == Round(b, 12)
}

if (Eq(-1.5707963267948966, ASin(-1)))
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (Eq(-0.5235987755982989, ASin(-0.5)))
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (Eq(0, ASin(0)))
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (Eq(0.5235987755982989, ASin(0.5)))
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (Eq(1.5707963267948966, ASin(1)))
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (Eq(0.74096470220302, ASin(0.675)))
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"
