#NoTrayIcon

#if WINDOWS
	val := DriveGetSerial("C:\")

	if (val > 1)
#elif OSX
	val := DriveGetSerial("/")

	if (val >= 0)
#else
	val := DriveGetSerial("/dev/sda")

	if (val >= 0)
#endif
 	FileAppend "pass", "*"
else
  	FileAppend "fail", "*"