#NoTrayIcon

; Helper module for module-scoped-import.ahk: a function export, a variable export (for write-through),
; and accessors so the importer can observe the module's own view of the variable.
export HelperFn() => 42
export helperVar := 100
export GetHelperVar() => helperVar
