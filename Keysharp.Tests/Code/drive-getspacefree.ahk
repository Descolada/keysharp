#NoTrayIcon

#if WINDOWS
	val := DriveGetSpaceFree("C:\")
#elif OSX
	val := DriveGetSpaceFree("/")
#else
	val := DriveGetSpaceFree("/dev/sda")
#endif
			
if (val > 10)
 	FileAppend "pass", "*"
else
  	FileAppend "fail", "*"