; =========================
; module-basic.ahk
; Basic module isolation + alias + built-in shadowing via AHK module
; =========================

import Other
import Other as O
import AHK

; Our own global var + function in __Main
MyVar := 1
ShowVar() => MyVar

; ---- Test: main has its own globals
a := ShowVar()
if (a == 1)
    FileAppend "pass", "*"
else
    FileAppend "fail", "*"

; ---- Test: Other has its own globals
a := Other.ShowVar()
if (a == 2)
    FileAppend "pass", "*"
else
    FileAppend "fail", "*"

; ---- Test: alias refers to default export (module object)
a := O.ShowVar()
if (a == 2)
    FileAppend "pass", "*"
else
    FileAppend "fail", "*"

; ---- Test: non-exported module global var should be inaccessible (per docs example)
a := ""
try a := Other.MyVar
catch
    a := "inaccessible"

if (a == "inaccessible")
    FileAppend "pass", "*"
else
    FileAppend "fail", "*"

; ---- Test: shadow built-in function; access built-in via AHK module
Abs(x) => "mine"

a := Abs(5)
if (a == "mine")
    FileAppend "pass", "*"
else
    FileAppend "fail", "*"

a := AHK.Abs(-5)
if (a == 5)
    FileAppend "pass", "*"
else
    FileAppend "fail", "*"


#Module Other
MyVar := 2
export ShowVar() => MyVar