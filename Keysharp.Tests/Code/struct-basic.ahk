struct POINT {
    x : Int32
    y : Int32
}

struct BOX {
    pt : POINT
}

struct BASE_POINT {
    x : Int32
}

struct CHILD_POINT extends BASE_POINT {
    y : Int32
}

struct EARLY_POINT {
    x : Int32
    y : Int32
}

struct LATE_POINT {
    x : Int32
    y : Int32
}

struct DWORD {
    v : UInt32
    __Value {
        get => this.v
        set => this.v := value
    }
}

struct PACKED {
    a : Int8
}

struct UNION {
    a : Int32
}

struct DYN_BASE {
}

struct PTR_HOLDER {
}

struct PACK_BASE {
    x : Int32
}

struct PACK_CHILD extends PACK_BASE {
    y : Int32
}

struct LOCK_BASE {
    x : Int32
}

struct LOCK_CHILD extends LOCK_BASE {
    y : Int32
}

struct FWD_BOX {
    pt : FWD_POINT
}

struct FWD_POINT {
    x : Int32
    y : Int32
}

struct INIT_BASE {
    x : Int32
}

struct INIT_CHILD extends INIT_BASE {
    __Init() {
        this.x := 42
    }
}

struct MY_INT extends Int32 {
}

class CLASS_GET_BASE {
    a {
        get {
            throw Error("getter called")
        }
    }
}

class CLASS_FIELD_ASSIGN extends CLASS_GET_BASE {
    a := 1
}

PropIsSet(obj, name) {
    try {
        _ := obj.%name%
        return true
    } catch as e {
        if e is UnsetError
            return false
        throw e
    }
}

dp := Object.DefineProp
dp(EARLY_POINT.Prototype, "z", {type:Int32})
dp(PACKED.Prototype, "b", {type:Int32, pack:1})
dp(UNION.Prototype, "b", {type:Int32, offset:"a"})
dp(PTR_HOLDER.Prototype, "pt", {type:POINT.Ptr})

pt := POINT()

if pt.Size != 8
    FileAppend "fail size", "*"

if !HasProp(pt, "x") || !HasProp(pt, "y")
    FileAppend "fail hasprop", "*"

pt.x := 10
pt.y := 20

if pt.x != 10 || pt.y != 20
    FileAppend "fail fields", "*"

holder := BOX()
holder.pt.x := 10
holder.pt.y := 20

if holder.pt.x != 10 || holder.pt.y != 20
    FileAppend "fail nested", "*"

threw := false
try
    holder.pt := pt
catch as e
    threw := e is Error && e.Message == "Assignment to struct is not supported."

if !threw
    FileAppend "fail nested assignment", "*"

pt2 := POINT.At(pt.Ptr)

if pt2.x != 10 || pt2.y != 20
    FileAppend "fail at", "*"

num := Int32()
num.__Value := 42

if num.Size != 4 || num.__Value != 42
    FileAppend "fail primitive", "*"

if !HasProp(num, "__Value")
    FileAppend "fail primitive hasprop", "*"

pointPtrClass := POINT.Ptr

if !(pointPtrClass is Class)
    FileAppend "fail ptrclass", "*"

if ObjHasOwnProp(POINT, "Ptr")
    FileAppend "fail ptr ownprop", "*"

if !HasBase(CHILD_POINT.Ptr, BASE_POINT.Ptr) || !HasBase(CHILD_POINT.Ptr.Prototype, BASE_POINT.Ptr.Prototype)
    FileAppend "fail ptr relation", "*"

ptrnum := Int32.Ptr()
ptrtarget := Int32()
ptrtarget.__Value := 123
ptrnum.__Value := ptrtarget

if ptrnum.Size != A_PtrSize || ptrnum.__Value.__Value != 123
    FileAppend "fail intptr", "*"

iptr := IntPtr()
iptr.__Value := 456

if iptr.Size != A_PtrSize || iptr.__Value != 456
    FileAppend "fail IntPtr", "*"

unusedPtrClass := EARLY_POINT.Ptr

early := EARLY_POINT()
early.x := 1
early.y := 2
early.z := 3

if early.Size != 12 || early.z != 3
    FileAppend "fail early", "*"

late := LATE_POINT()

threw := false
try
    dp(LATE_POINT.Prototype, "z", {type:Int32})
catch
    threw := true

if !threw
    FileAppend "fail lock", "*"

packedInst := PACKED()
packedInst.b := 0x11223344

if packedInst.Size != 5 || packedInst.b != 0x11223344
    FileAppend "fail pack", "*"

unionInst := UNION()
unionInst.a := 1
unionInst.b := 2

if unionInst.Size != 4 || unionInst.a != 2
    FileAppend "fail offset", "*"

ptrHolder := PTR_HOLDER()

if PropIsSet(ptrHolder, "pt")
    FileAppend "fail null pointer field", "*"

ptrHolder.pt := pt

if !(ptrHolder.pt is POINT) || ptrHolder.pt.Ptr != pt.Ptr
    FileAppend "fail pointer field", "*"

ptrHolder.pt := unset

if PropIsSet(ptrHolder, "pt")
    FileAppend "fail pointer unset", "*"

DynPoint := Class("DynPoint", DYN_BASE)
dp(DynPoint.Prototype, "x", {type:Int32})
dp(DynPoint.Prototype, "y", {type:Int32})

DynChild := Class("DynChild", DynPoint)
dp(DynChild.Prototype, "z", {type:Int32})

dynPoint := DynPoint()
dynPoint.x := 10
dynPoint.y := 20

dynChild := DynChild()
dynChild.x := 1
dynChild.y := 2
dynChild.z := 3

if dynPoint.Size != 8
    || dynPoint.x != 10
    || dynPoint.y != 20
    || dynChild.Size != 12
    || dynChild.x != 1
    || dynChild.y != 2
    || dynChild.z != 3
    FileAppend "fail dynamic", "*"

buf := Buffer(4)
atLock := POINT.At(buf.Ptr)

threw := false
try
    dp(POINT.Prototype, "z", {type:Int32})
catch
    threw := true

if atLock.Size != 8 || !threw
    FileAppend "fail at lock", "*"

threw := false
try
    dp(PACK_BASE.Prototype, "z", {type:Int32})
catch
    threw := true

packChild := PACK_CHILD()
packChild.x := 1
packChild.y := 3

if packChild.Size != 8 || packChild.x != 1 || packChild.y != 3 || !threw
    FileAppend "fail base extend", "*"

fwdBox := FWD_BOX()
fwdPoint := FWD_POINT()
fwdBox.pt.x := 10
fwdBox.pt.y := 20

if fwdBox.Size != 8 || fwdBox.pt.x != 10 || fwdBox.pt.y != 20
    FileAppend "fail forward", "*"

initChild := INIT_CHILD()

if initChild.Ptr == 0 || initChild.Size != 4 || initChild.x != 42
    FileAppend "fail inherited init", "*"

myInt := MY_INT()
myInt.__Value := 123

if myInt.Ptr == 0 || myInt.Size != 4 || myInt.__Value != 123
    FileAppend "fail primitive extend", "*"

threw := false
try CLASS_FIELD_ASSIGN()
catch
    threw := true

if !threw
    FileAppend "fail field assignment", "*"

pt3 := POINT()
if !DllCall("GetCursorPos", POINT.Ptr, pt3)
    FileAppend "fail getcursor", "*"

if !IsNumber(pt3.x) || !IsNumber(pt3.y)
    FileAppend "fail point", "*"

hwnd := DllCall("WindowFromPoint", POINT, pt3, "ptr")

pt4 := unset
if !DllCall("GetCursorPos", POINT.Ptr, &pt4)
    FileAppend "fail getcursor varref", "*"

if !(pt4 is POINT)
    FileAppend "fail varref", "*"

if DllCall("IsWindow", POINT.Ptr, unset) != 0
    FileAppend "fail unset pointer", "*"

pp := POINT.Ptr()
pp.__Value := pt3

if !DllCall("GetCursorPos", POINT.Ptr, pp)
    FileAppend "fail pointer instance", "*"

threw := false
try
    DllCall("IsWindow", POINT.Ptr, 123)
catch as e
    threw := e is TypeError

if !threw
    FileAppend "fail missing value typeerror", "*"

hwnd := DllCall("GetDesktopWindow", "ptr")
pid := unset
tid := DllCall("GetWindowThreadProcessId", "ptr", hwnd, DWORD.Ptr, &pid, "uint")

if !IsNumber(pid) || pid == 0
    FileAppend "fail custom value output", "*"

threw := false
try
    nullPtr := DllCall("GetModuleHandle", "str", "__keysharp_missing_module__", POINT.Ptr)
catch as e
    threw := e is UnsetError

if !threw
    FileAppend "fail null pointer return", "*"

kernel32 := DllCall("GetModuleHandle", "str", "kernel32", Int32.Ptr)

if !IsNumber(kernel32) || kernel32 == 0
    FileAppend "fail numeric pointer return", "*"

FileAppend "pass", "*"
