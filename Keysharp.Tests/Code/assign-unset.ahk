#NoTrayIcon

x := ""

If (x != "")
	FileAppend "fail", "*"
else
	FileAppend "pass", "*"
	
If (x = "")
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

x := 123
x := unset

if (x is unset)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (x = unset)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (x == unset)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (unset = x)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"
	
if (unset == x)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (x != unset)
	FileAppend "fail", "*"
else
	FileAppend "pass", "*"

if (x !== unset)
	FileAppend "fail", "*"
else
	FileAppend "pass", "*"

if (unset != x)
	FileAppend "fail", "*"
else
	FileAppend "pass", "*"
	
if (unset !== x)
	FileAppend "fail", "*"
else
	FileAppend "pass", "*"
