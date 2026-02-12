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

; ---- explicit imports with alternative syntax (as opposed to "import { a } from Test")
import Test { Hello as ReturnHello }
import Test { Hello }

v := ReturnHello()
if (v == "Hello")
    FileAppend "pass", "*"
else
    FileAppend "fail", "*"

v := Hello()
if (v == "Hello")
    FileAppend "pass", "*"
else
    FileAppend "fail", "*"

; ---- non-exported members should still be importable, but must be explicitly imported (not via wildcard)

import { hiddenFn as h1, hiddenVar as hv1 } from Mixed
import Mixed { hiddenFn as h2, hiddenVar as hv2 }

if (h1() == "hidden" && h2() == "hidden")
    FileAppend "pass", "*"
else
    FileAppend "fail", "*"

if (hv1 == 42 && hv2 == 42)
    FileAppend "pass", "*"
else
    FileAppend "fail", "*"

import { noExportFn as n1, noExportVar as nv1 } from NoExports
import * from NoExports

if (n1() == "noexp" && noExportFn() == "noexp")
    FileAppend "pass", "*"
else
    FileAppend "fail", "*"

if (nv1 == 7 && noExportVar == 7)
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

#Module Test
export Hello() => "Hello"

#Module Mixed
export pubFn() => "pub"
hiddenFn() => "hidden"
hiddenVar := 42

#Module NoExports
noExportFn() => "noexp"
noExportVar := 7