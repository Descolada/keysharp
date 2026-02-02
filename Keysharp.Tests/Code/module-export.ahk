; =========================
; module-export.ahk
; Default export, named exports, exported variable assignment, quoted import behavior
; =========================

import D as DDefault
import { Named as DNamed } from D

import V as ModV

; quoted module import should not add module name unless alias is given
import "Q"

; ---- default export callable via alias
a := DDefault()
if (a == 123)
    FileAppend "pass", "*"
else
    FileAppend "fail", "*"

; ---- named export callable via explicit import
a := DNamed()
if (a == 5)
    FileAppend "pass", "*"
else
    FileAppend "fail", "*"

; ---- importing via "from D" should NOT add D unless aliased
a := IsSet(D)
if (a == false)
    FileAppend "pass", "*"
else
    FileAppend "fail", "*"

; ---- quoted import "Q" should NOT add Q to namespace
a := IsSet(Q)
if (a == false)
    FileAppend "pass", "*"
else
    FileAppend "fail", "*"

; ---- exported variables can be assigned by other modules
a := ModV.Var
if (a == 1)
    FileAppend "pass", "*"
else
    FileAppend "fail", "*"

ModV.Var := 7
a := ModV.Var
if (a == 7)
    FileAppend "pass", "*"
else
    FileAppend "fail", "*"


#Module D
export default DefaultFunc() => 123
export Named() => 5

#Module V
export Var := 1

#Module Q
; empty on purpose
