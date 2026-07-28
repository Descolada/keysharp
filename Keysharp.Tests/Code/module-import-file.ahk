#NoTrayIcon

#import "module_import_file_target" { * }
#import Lib/module_import_path_target
Success()
module_import_path_target.PathSuccess()

if (ImportedClass.Value == 42)
	FileAppend("pass", "*")
else
	FileAppend("fail", "*")
