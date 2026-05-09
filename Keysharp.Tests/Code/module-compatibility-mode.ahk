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
	return ""
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
