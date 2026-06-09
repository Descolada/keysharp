#NoTrayIcon

#if WINDOWS
	val := DriveGetStatus("C:\")
#elif OSX
	val := DriveGetStatus("/")
#else
	val := DriveGetStatus("/dev") ; /dev seems to work better than /dev/sda on VMs.
#endif
			
if (val == "Ready")
 	FileAppend "pass", "*"
else
  	FileAppend "fail", "*"