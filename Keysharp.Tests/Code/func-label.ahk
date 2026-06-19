#NoTrayIcon

x := 1
goto labelz
y := 2

labelz:
z := 3

If (x == 1)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

If (y == unset)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

If (z == 3)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"