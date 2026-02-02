; =========================
; module-import.ahk
; Import forms + precedence rules
; =========================

import { Calculate as CalculateX } from X
import * from Y

import * from A
import * from B

import { Foo as FooFromA } from A

; ---- Local Calculate must take precedence over wildcard imports regardless of order
Calculate() => 1

a := Calculate()
if (a == 1)
    FileAppend "pass", "*"
else
    FileAppend "fail", "*"

a := CalculateX()
if (a == 2)
    FileAppend "pass", "*"
else
    FileAppend "fail", "*"

a := Check(3)
if (a == true)
    FileAppend "pass", "*"
else
    FileAppend "fail", "*"

; ---- "from X" should NOT add X to namespace unless alias is specified
a := IsSet(X)
if (a == false)
    FileAppend "pass", "*"
else
    FileAppend "fail", "*"

; ---- wildcard import conflict: last import wins (B after A)
a := Bar()
if (a == "B")
    FileAppend "pass", "*"
else
    FileAppend "fail", "*"

; ---- explicit import alias remains available and unaffected
a := FooFromA()
if (a == "A")
    FileAppend "pass", "*"
else
    FileAppend "fail", "*"

; ---- local declaration overrides wildcard import even if declared after imports
Foo() => "local"
a := Foo()
if (a == "local")
    FileAppend "pass", "*"
else
    FileAppend "fail", "*"


#Module X
export Calculate() => 2

#Module Y
export Calculate() => 3
export Check(n) => (n = Calculate())

#Module A
export Foo() => "A"
export Bar() => "A"

#Module B
export Foo() => "B"
export Bar() => "B"