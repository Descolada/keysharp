#NoTrayIcon

#if WINDOWS
	val := DriveGetCapacity("C:\")
#elif OSX
	val := DriveGetCapacity("/")
#else
	val := DriveGetCapacity("/dev/sda")
#endif
			
if (val > 1000)
 	FileAppend "pass", "*"
else
  	FileAppend "fail", "*"