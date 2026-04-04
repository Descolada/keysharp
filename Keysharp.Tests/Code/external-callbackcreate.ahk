import { RealThread } from Ks

val := ""
#if WINDOWS
callback := CallbackCreate("TheFunc", "&")
DllCall(callback, "float", 10.5, "int64", 42)
#elif LINUX || OSX
callback := CallbackCreate("TheFunc", "&", 2)
DllCall(callback, "ptr", 10, "ptr", 42)
#endif

TheFunc(args)
{
	global val
#if WINDOWS
	val := NumGet(args, 0, "float") + NumGet(args, A_PtrSize, "int64")
#elif LINUX || OSX
	val := NumGet(args, 0, "ptr") + NumGet(args, A_PtrSize, "ptr")
#endif
}

#if WINDOWS
if (val == 52.5)
#elif LINUX || OSX
if (val == 52)
#endif
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

val := ""
CallbackFree(callback)
callback := CallbackCreate("FuncNoParams")
DllCall(callback)

FuncNoParams()
{
	global val
	val := 123
}

if (val == 123)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

CallbackFree(callback)

#if WINDOWS
EnumAddress := CallbackCreate("EnumWindowsProc")
DetectHiddenWindows(True)
ct := 0
DllCall("EnumWindows", "Ptr", EnumAddress, "Ptr", 0)

EnumWindowsProc(hwnd, lParam, *)
{
	global ct
	win_title := WinGetTitle(hwnd)
	win_class := WinGetClass(hwnd)
	ct++

	if (ct < 5) ; go through the first five windows
		return true
	else
		return false
}

if (ct == 5)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

CallbackFree(EnumAddress)
#elif LINUX || OSX
; On Unix, verify that a callback created on a RealThread is marshalled back to
; that same owner thread when invoked from the main thread.
workerReady := false
workerStop := false
workerCallback := 0
worker := RealThread(WorkerCallbackOwner)

while !workerReady
	Sleep 10

CoordMode "Mouse", "Client"
ret1 := DllCall(workerCallback)
ret2 := DllCall(workerCallback)
workerStop := true
worker.Wait()

if (ret1 = 101 && ret2 = 202 && A_CoordModeMouse = "Client")
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"
#endif

args := []
Loop 32 {
	i := A_Index - 1, ret := -1
	if (i > 0) {
		args.Push("ptr", i)
	}
	cb := CallbackCreate(Variadic,, i)
	ret := DllCall(cb, args*)
	if (ret == i)
		FileAppend "pass", "*"
	else
		FileAppend "fail", "*"
	CallbackFree(cb)
}

Variadic(args*) => args.Length ? args[args.Length] : 0

WorkerCallbackOwner() {
	global workerReady, workerStop, workerCallback

	CoordMode "Mouse", "Screen"
	; Fast avoids creating a fresh pseudo-thread for each invocation, so the test
	; observes the owner thread's actual thread-local state.
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
		; The first call should see the RealThread's original CoordMode, then mutate
		; it so the second call proves state is preserved on that same owner thread.
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
