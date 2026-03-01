#if WINDOWS
	val := DriveGetFileSystem("C:\")
#else
	val := DriveGetFileSystem("/")
#endif
			
if (
#if WINDOWS
	val == "NTFS" || val == "FAT32" || val == "FAT" || val == "CDFS" || val == "UDF"
#else
	val != ""
#endif
)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"
