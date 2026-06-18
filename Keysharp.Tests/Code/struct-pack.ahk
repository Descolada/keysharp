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

; ObjSetDataPtr rebinds the struct to an external address (like Struct.At, in place).
buf := Buffer(8, 0)
pt := AB1()
ObjSetDataPtr(pt, buf.Ptr)
if pt.Ptr != buf.Ptr
    FileAppend "fail setdataptr", "*"

FileAppend "pass", "*"
