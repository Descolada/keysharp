
Eq(a, b)
{
	return Round(a, 12) == Round(b, 12)
}

if (Eq(-0.7853981633974483, ATan(-1)))
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (Eq(-0.4636476090008061, ATan(-0.5)))
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (Eq(0, ATan(0)))
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (Eq(0.4636476090008061, ATan(0.5)))
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (Eq(0.7853981633974483, ATan(1)))
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (Eq(0.5937496667107711, ATan(0.675)))
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"
