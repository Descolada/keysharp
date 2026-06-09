#NoTrayIcon

val := DriveGetStatusCD("C:\\")
			
if (val == "error")
 	FileAppend "pass", "*"
else
  	FileAppend "fail", "*"