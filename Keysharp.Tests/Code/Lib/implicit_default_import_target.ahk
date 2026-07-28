#NoTrayIcon

; The same-named class is the implicit default even when the module later gains helpers.
class implicit_default_import_target {
	static Value => 43
}

HelperFunction() => "helper"
helperGlobal := "helper"
