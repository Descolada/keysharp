#NoTrayIcon

#Requires AutoHotkey v2.1

struct X {
    y : Int32[10]
}

struct XX {
    items : X[2]
}

; A struct field of array type contributes its full array size, and exposes Length/element access.
z := X()
if z.Size != 40 || z.y.Size != 40 || z.y.Length != 10
    FileAppend "fail field array", "*"

z.y[1] := 11
z.y[10] := 99
if z.y[1] != 11 || z.y[10] != 99
    FileAppend "fail field elem", "*"

if !(z.y is Int32[10]) || (z.y is Int32[100])
    FileAppend "fail field is", "*"

; Nested array of structs: XX has 2 X's, each 40 bytes => 80.
zz := XX()
if zz.Size != 80
    FileAppend "fail nested array size", "*"

; Class-level indexing of a struct class yields a fixed-size structured-array class.
arrCls := Int32[10]
inst := arrCls()

if inst.Size != 40
    FileAppend "fail size", "*"

if inst.Length != 10
    FileAppend "fail length", "*"

inst[1] := 100
inst[10] := 200
inst[-1] := 250   ; negative index counts from the end, so -1 is element 10

if inst[1] != 100
    FileAppend "fail elem1", "*"

if inst[10] != 250 || inst[-1] != 250
    FileAppend "fail neg", "*"

; The array class has stable identity (cached), and differs by element type and length.
if !(inst is Int32[10])
    FileAppend "fail is", "*"

if (inst is Int32[5])
    FileAppend "fail is-len", "*"

if (inst is Float32[10])
    FileAppend "fail is-type", "*"

; Out-of-bounds indices throw IndexError.
threw := false
try
    _ := inst[11]
catch IndexError
    threw := true
if !threw
    FileAppend "fail oob", "*"

threw := false
try
    _ := inst[0]
catch IndexError
    threw := true
if !threw
    FileAppend "fail zero", "*"

FileAppend "pass", "*"
