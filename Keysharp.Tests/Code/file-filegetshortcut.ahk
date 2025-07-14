; #Include %A_ScriptDir%/header.ahk

if (DirExist("./FileGetShortcut"))
	DirDelete("./FileGetShortcut", true)

if (FileExist("./testshortcut.lnk"))
	FileDelete("./testshortcut.lnk")

path := "../../../Keysharp.Tests/Code/"
dir := path . "DirCopy"
DirCopy(dir, "./FileGetShortcut/")
fullpath := FileDirName("./FileGetShortcut/file1.txt")

#if LINUX
	FileCreateShortcut("./FileGetShortcut/file1.txt", "./testshortcut.lnk")
	FileGetShortcut("./testshortcut.lnk",
												&outTarget,
												&outDir,
												&outArgs,
												&outDescription,
												&outIcon,
												&outIconNum,
												&outRunState)

if (StrLower(FileFullPath("./FileGetShortcut/file1.txt")) == StrLower(outTarget))
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (fullpath == outDir)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (outDescription == "" &&
	outArgs == "" &&
	outIcon == "" &&
	outIconNum == "" &&
	outRunState == ""
)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (FileExist("./testshortcut.lnk"))
	FileDelete("./testshortcut.lnk")

#endif

#if WINDOWS
	FileCreateShortcut("./FileGetShortcut/file1.txt", "./testshortcut.lnk", fullpath, "", "TestDescription", "../../../Keysharp.ico", "")
#else
	FileCreateShortcut("./FileGetShortcut/file1.txt", "./testshortcut.lnk", fullpath, "", "TestDescription", "../../../Keysharp.ico", 2)
#endif

if (FileExist("./testshortcut.lnk"))
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

outTarget :=
outDir :=
outArgs :=
outDescription :=
outIcon :=
outIconNum :=
outRunState := ""
FileGetShortcut("./testshortcut.lnk",
	&outTarget,
	&outDir,
	&outArgs,
	&outDescription,
	&outIcon,
	&outIconNum,
	&outRunState)

if (StrLower(FileFullPath("./FileGetShortcut/file1.txt")) == StrLower(outTarget))
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (fullpath == outDir)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if ("TestDescription" == outDescription)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if ("" == outArgs)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (FileFullPath("../../../Keysharp.ico") == outIcon)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

#if WINDOWS
	if ("0" == outIconNum)
		FileAppend "pass", "*"
	else
		FileAppend "fail", "*"

	if ("1" == outRunState)
		FileAppend "pass", "*"
	else
		FileAppend "fail", "*"
#else
	if ("Link" == outIconNum)
		FileAppend "pass", "*"
	else
		FileAppend "fail", "*"

	if ("" == outRunState)
		FileAppend "pass", "*"
	else
		FileAppend "fail", "*"
#endif

if (DirExist("./FileGetShortcut"))
	DirDelete("./FileGetShortcut", true)

if (FileExist("./testshortcut.lnk"))
	FileDelete("./testshortcut.lnk")