#NoTrayIcon

#if WINDOWS
	val := DriveGetType("C:\")
#elif OSX
	val := DriveGetType("/")
#else
	val := DriveGetType("/dev/sda")
#endif

if (val == "Fixed" || val == "RAMDisk")
 	FileAppend "pass", "*"
else
  	FileAppend "fail", "*"