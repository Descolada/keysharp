#NoTrayIcon

#Requires AutoHotkey v2.1

struct AB1 {
    #StructPack 1
    a : Int8
    b : Int32
}

struct AB2 {
    a : Int8
    b : Int32
}

; #StructPack 1 disables padding, so AB1 is 1 + 4 = 5 bytes.
ab := AB1()
if ab.Size != 5
    FileAppend "fail pack size", "*"

; Without #StructPack, b is aligned to offset 4, so AB2 is 8 bytes.
if AB2().Size != 8
    FileAppend "fail default size", "*"

; ObjGetDataPtr / ObjGetDataSize mirror the struct's Ptr / Size.
if ObjGetDataPtr(ab) != ab.Ptr
    FileAppend "fail dataptr", "*"

if ObjGetDataSize(ab) != 5
    FileAppend "fail datasize", "*"

; Since alpha.27, only a boxed pointer created by Struct.At can be rebound.
buf1 := Buffer(8, 0)
buf2 := Buffer(8, 0)
pt := AB1.At(buf1.Ptr)
ObjSetDataPtr(pt, buf2.Ptr)
if pt.Ptr != buf2.Ptr
    FileAppend "fail setdataptr", "*"

try ObjSetDataPtr(AB1(), buf1.Ptr)
catch Error
    normalStructFailed := true

if !IsSet(normalStructFailed)
    FileAppend "fail setdataptr owned", "*"

FileAppend "pass", "*"
