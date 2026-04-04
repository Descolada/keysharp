fired := false
firedDuringSleep := false

SetTimer(Outer, -1)
Sleep(250)

if (firedDuringSleep)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

ExitApp

Outer() {
	global fired, firedDuringSleep

	Inner() {
		global fired
		fired := true
	}

	SetTimer(Inner, -20)
	Sleep(120)
	firedDuringSleep := fired
}
