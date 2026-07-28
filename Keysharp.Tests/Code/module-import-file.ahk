#NoTrayIcon

#import "module_import_file_target" { * }
Success()

if (ImportedClass.Value == 42)
	FileAppend("pass", "*")
else
	FileAppend("fail", "*")
