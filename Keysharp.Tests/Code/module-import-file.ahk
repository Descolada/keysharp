#NoTrayIcon

#import "module_import_file_target" { * }
#import Lib/module_import_path_target
#import Lib/implicit_default_import_target
#import Lib/implicit_default_import_target as implicit_default_alias
Success()
module_import_path_target.PathSuccess()

if (ImportedClass.Value == 42)
	FileAppend("pass", "*")
else
	FileAppend("fail", "*")

if (implicit_default_import_target.Value == 43 && implicit_default_alias.Value == 43)
	FileAppend("pass", "*")
else
	FileAppend("fail", "*")

ScopedImplicitDefault() {
	#import Lib/implicit_default_import_target
	return implicit_default_import_target.Value
}

if (ScopedImplicitDefault() == 43)
	FileAppend("pass", "*")
else
	FileAppend("fail", "*")
