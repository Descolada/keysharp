#NoTrayIcon

#Requires AutoHotkey v2.1-alpha
#import Compat21 { NoReturn21, EmptyReturn21, ReturnNoReturn21, ReturnMaybeNoReturn21, PropertyNoReturn21, PropertyMaybeNoReturn21, NestedDefault20, NestedDefault21, NestedRestore21, RuntimeDefault20 }
#import Compat20 { NoReturn20 }
#import InheritMain { NoReturnInherited }
#import ClassMode { ClassNoReturn }

threw := false
try
	x := NoReturn21()
catch UnsetError
	threw := true

if threw
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

threw := false
try
	x := ClassNoReturn()
catch UnsetError
	threw := true

if threw
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

x := (NoReturn21()?)
if !IsSet(x)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

x := NoReturn21()?
if !IsSet(x)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

x := (EmptyReturn21()?)
if !IsSet(x)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

x := NoReturn20()
if (IsSet(x) && x == "")
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

threw := false
try
	x := NoReturnInherited()
catch UnsetError
	threw := true

if threw
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

threw := false
try
	ReturnNoReturn21()
catch UnsetError
	threw := true

if threw
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

threw := false
try
	x := ReturnNoReturn21()
catch UnsetError
	threw := true

if threw
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

ReturnMaybeNoReturn21()
FileAppend "pass", "*"

threw := false
try
	x := ReturnMaybeNoReturn21()
catch UnsetError
	threw := true

if threw
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

x := NestedDefault20()
if (IsSet(x) && x == "")
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

threw := false
try
	x := RuntimeDefault20()
catch UnsetError
	threw := true

if (!threw && IsSet(x) && x == "")
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

threw := false
try
	x := NestedDefault21()
catch UnsetError
	threw := true

if threw
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

threw := false
try
	x := NestedRestore21()
catch UnsetError
	threw := true

if threw
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

threw := false
try
	PropertyNoReturn21()
catch UnsetError
	threw := true

if threw
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

threw := false
try
	x := PropertyNoReturn21()
catch UnsetError
	threw := true

if threw
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

PropertyMaybeNoReturn21()
FileAppend "pass", "*"

threw := false
try
	x := PropertyMaybeNoReturn21()
catch UnsetError
	threw := true

if threw
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

lambda := () => NoReturn21()
threw := false
try
	lambda()
catch UnsetError
	threw := true

if !threw
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

threw := false
try
	x := lambda()
catch UnsetError
	threw := true

if threw
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

lambda := () => (NoReturn21()?)
lambda()
FileAppend "pass", "*"

threw := false
try
	x := lambda()
catch UnsetError
	threw := true

if threw
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

; v2.1 mode: absent item/member accessors return unset instead of throwing
; (Array.Get unset element, Map.__Item/Get/Delete, Object.GetMethod/GetOwnPropDesc).
arr := [1, , 3]
x := (arr.Get(2)?)
if !IsSet(x)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

; An out-of-range index still throws IndexError in both modes (only absent *items* become unset).
threw := false
try
	x := arr[99]
catch IndexError
	threw := true
if threw
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

m := Map()
x := (m["nope"]?)
if !IsSet(x)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

x := (m.Get("nope")?)
if !IsSet(x)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

x := (m.Delete("nope")?)
if !IsSet(x)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

obj := {}
x := (obj.GetMethod("nope")?)
if !IsSet(x)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

x := (obj.GetOwnPropDesc("nope")?)
if !IsSet(x)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

; ObjGetBase/Any.Prototype.Base returns unset when there is no base. [v2.1-alpha.29]
x := (ObjGetBase(Any.Prototype)?)
if !IsSet(x)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

x := (Any.Prototype.Base?)
if !IsSet(x)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

; RegExMatch leaves its output var unset when there is no match. [v2.1-alpha.29]
match := "sentinel"
RegExMatch("abc", "z", &match)
if !IsSet(match)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

; A match still assigns a RegExMatchInfo.
RegExMatch("abc", "b", &match)
if IsSet(match) && match[0] = "b"
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

; String(x) passes through ToString()'s return value, including no value at all. [v2.1-alpha.30]
class NoToStringValue {
	ToString() {
	}
}

x := (String(NoToStringValue())?)
if !IsSet(x)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

; Non-final items of a sequence are evaluated for their side effects only, so a no-value result there is
; discarded rather than raising. The final item's value is consumed, so that one still raises.
NoValueItem() {
}

x := (NoValueItem(), 5)
if x = 5
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

threw := false
try
	x := (5, NoValueItem())
catch UnsetError
	threw := true

if threw
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

; A statement-level comma list discards every item, so it never raises.
sideCount := 0
CountSide() {
	global sideCount += 1
}

CountSide(), NoValueItem(), CountSide()
if sideCount = 2
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

; A "void" return type yields no value. [v2.1-alpha.30]
VoidTarget(value) {
}

voidCb := CallbackCreate(VoidTarget, "Fast", [Int32, "void"])
x := (DllCall(voidCb, "int", 5, "void")?)
CallbackFree(voidCb)

if !IsSet(x)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

#Module Compat21
#Requires AutoHotkey v2.1-alpha
export NoReturn21() {
}
export EmptyReturn21() {
	return
}
export ReturnNoReturn21() {
	return NoReturn21()
}
export ReturnMaybeNoReturn21() {
	return NoReturn21()?
}
export PropertyNoReturn21() {
	c := Getter21()
	return c.Prop
}
export PropertyMaybeNoReturn21() {
	c := GetterMaybe21()
	return (c.Prop?)
}
class Getter21 {
	Prop => NoReturn21()
}
class GetterMaybe21 {
	Prop => (NoReturn21()?)
}
export NestedDefault20() {
	#Requires AutoHotkey v2.0
	Inner() {
	}
	return Inner()
}
export RuntimeDefault20() {
	#Requires AutoHotkey v2.0
	return A_HotIf
}
export NestedDefault21() {
	Inner() {
	}
	return Inner()
}
export NestedRestore21() {
	#Requires AutoHotkey v2.1-alpha
	Middle() {
		#Requires AutoHotkey v2.0
		Inner() {
		}
		return Inner()
	}
	Middle()
	After() {
	}
	return After()
}

#Module Compat20
#Requires AutoHotkey v2.1-alpha
#Requires AutoHotkey v2.0
export NoReturn20() {
}

#Module InheritMain
export NoReturnInherited() {
}

#Module ClassMode
#Requires AutoHotkey v2.0
class CompatClass {
	#Requires AutoHotkey v2.1-alpha
	NoReturn() {
	}
}
export ClassNoReturn() {
	c := CompatClass()
	return c.NoReturn()
}
