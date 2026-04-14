#import "Ks" { RealThread, LockRun }
lockit := ""
tharr := []
tharr.Length := 100
tot := 0

rtAddTot(o)
{
	global tot
	tot += o
}

rtfunc1(obj)
{
	LockRun(lockit, (o) => rtAddTot(o), obj)
}

fo := Func("rtfunc1")

Loop 100
{
	tharr[A_Index] := RealThread(fo, A_Index).ContinueWith(fo, 1)
}

Loop 100
{
	tharr[A_Index].Wait()
}

tharr.Length := 0

If tot == 5150
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

tharr := []
tharr.Length := 100
tot := 0

rtSumTot()
{
	ct := 0
    
	Loop 100
	{
		ct++
	}

	return ct
}

rtfunc2()
{
	return rtSumTot()
}

fo := Func("rtfunc2")

Loop 100
{
	tharr[A_Index] := RealThread(fo)
}

Loop 100
{
	tot += tharr[A_Index].Wait()
}

tharr.Length := 0

If tot == 10000
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

CoordMode "Mouse", "Screen"

RealThread(RealThreadEntry)

cb2 := CallbackCreate(SetCoordModeMouseClient)
Loop 10000 {
	DllCall(cb2)
}

if A_CoordModeMouse = "Screen"
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

workerReady := false
workerStop := false
workerCallback := 0
workerThread := RealThread(WorkerCallbackOwner)

while !workerReady
	Sleep 10

CoordMode "Mouse", "Client"
ret1 := DllCall(workerCallback)
ret2 := DllCall(workerCallback)
workerStop := true
workerThread.Wait()

if ret1 = 101 && ret2 = 202 && A_CoordModeMouse = "Client"
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

RealThreadEntry() {
	CoordMode "Mouse", "Screen"
	cb1 := CallbackCreate(SetCoordModeMouseWindow)

	Loop 10000 {
		DllCall(cb1)
	}
}

SetCoordModeMouseWindow() {
	if A_CoordModeMouse = "Client" {
		FileAppend "fail", "*"
		ExitApp()
	}

	CoordMode "Mouse", "Window"
}

SetCoordModeMouseClient() {
	if A_CoordModeMouse = "Window" {
		FileAppend "fail", "*"
		ExitApp()
	}

	CoordMode "Mouse", "Client"
}

WorkerCallbackOwner() {
	global workerReady, workerStop, workerCallback

	CoordMode "Mouse", "Screen"
	workerCallback := CallbackCreate(CheckWorkerCoordModeAffinity, "Fast")
	workerReady := true

	while !workerStop
		Sleep 10

	CallbackFree(workerCallback)
}

CheckWorkerCoordModeAffinity() {
	static callCount := 0
	callCount++

	if callCount = 1 {
		if A_CoordModeMouse != "Screen"
			return -1

		CoordMode "Mouse", "Window"
		return 101
	}
	else if callCount = 2 {
		if A_CoordModeMouse != "Window"
			return -2

		return 202
	}

	return -3
}
