#NoTrayIcon

; =========================
; module-scoped-import.ahk
; #import scoped to a function body / class body (Keysharp extension), plus laziness and write-through.
; Each check prints "pass" on success; anything else fails HasPassed.
; =========================

Ok(cond) => FileAppend(cond ? "pass" : "fail", "*")

; A bare module-scope import binds the module NAME to a Module object, so a method call dispatches via IMetaObject.
#import KS
Ok(Ks.Cosh(0) == 1)

; ---- 1. Function-scoped built-in import: Cosh visible inside, resolves correctly
FnBuiltin() {
    #import KS { Cosh }
    return Cosh(0)   ; cosh(0) = 1
}
Ok(FnBuiltin() == 1)

; ---- 1b. Function-scoped bare import: the module object dispatches a method call
FnModuleObject() {
    #import KS
    return Ks.Cosh(0)
}
Ok(FnModuleObject() == 1)

; ---- 2. Function-scoped file import: a separate module's export, bound only inside the function
FnFile() {
    #import "module_scoped_import_helper" { HelperFn }
    return HelperFn()
}
Ok(FnFile() == 42)

; ---- 3. Function-scoped wildcard import, only referenced names materialize
FnWild() {
    #import KS { * }
    return Cosh(0)
}
Ok(FnWild() == 1)

; ---- 4. Aliased import inside a function
FnAlias() {
    #import KS { Cosh as C }
    return C(0)
}
Ok(FnAlias() == 1)

; ---- 5. Closure sees the enclosing function's import
FnClosure() {
    #import KS { Cosh }
    f := () => Cosh(0)
    return f()
}
Ok(FnClosure() == 1)

; ---- 6. Write-through: assigning an imported script VARIABLE propagates to the source module
FnWrite() {
    #import "module_scoped_import_helper" { helperVar, GetHelperVar }
    helperVar := 7
    return GetHelperVar()   ; reads the module's own helperVar
}
Ok(FnWrite() == 7)

; ---- 8. %name% dynamic deref of a scoped import resolves in-scope
FnDeref() {
    #import KS { Cosh }
    n := "Cosh"
    return %n%(0)
}
Ok(FnDeref() == 1)

; ---- 9. Class-body import visible in a method
class WithImport {
    #import KS { Cosh }
    Compute() => Cosh(0)
}
Ok(WithImport().Compute() == 1)

; ---- 10. Class-body import visible in a nested class's method
class Outer {
    #import KS { Cosh }
    class Inner {
        Compute() => Cosh(0)
    }
}
Ok(Outer.Inner().Compute() == 1)

; ---- 11. A local declared in the function shadows an import of the same name
FnShadow() {
    #import KS { Cosh }
    local Cosh := 5
    return Cosh
}
Ok(FnShadow() == 5)

; ---- 12. An import in one function does not leak into another: the SAME alias bound to DIFFERENT
;          functions in each frame must resolve independently (Cosh(0)=1, Sinh(0)=0).
FnA() {
    #import KS { Cosh as Shared }
    return Shared(0)
}
FnB() {
    #import KS { Sinh as Shared }
    return Shared(0)
}
Ok(FnA() == 1 && FnB() == 0)
