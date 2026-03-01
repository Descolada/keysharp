import { RunScript } from Ks

#if WINDOWS
	hostBinary := "Keysharp.exe"
	headlessDirectives := ""
#else
	hostBinary := "./Keysharp"
	headlessDirectives := "#NoMainWindow`n#NoTrayIcon`n"
#endif

info := RunScript(headlessDirectives . "ExitApp(0)",,, hostBinary)
if (info.ExitCode == 0)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

info := ""
info := RunScript(headlessDirectives . "ExitApp(1)",,, hostBinary)
if (info.ExitCode == 1)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

AsyncCallback(callbackinfo) {
	global result := callbackinfo.ExitCode
}
info := "", result := ""
info := RunScript(headlessDirectives . "ExitApp(3)", AsyncCallback,, hostBinary)

while (!info.HasExited)
	Sleep 10

if (info.ExitCode == 3)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

loops := 0
while (result == "" && loops < 200)
{
	Sleep 10
	loops++
}

if (result == 3)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

info := ""
script := headlessDirectives . "
(
	stdout := FileOpen("*", "r")
	stdin := FileOpen("*", "w")
	str := stdout.ReadLine()
	stdin.WriteLine(str str)
)"
info := RunScript(script, 1,, hostBinary)
info.StdIn.WriteLine("a")
while (!info.HasExited)
	Sleep 10

if (info.StdOut.Read(2) == "aa")
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"
