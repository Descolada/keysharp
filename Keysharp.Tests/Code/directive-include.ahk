include_again_counter := 0

#include directive-include-target.ahk
#includeagain "directive-include-target.ahk"

class IncludedClass
{
	#include "directive-include-target-class.ahk"
}

obj := {
	#include
(
	directive-include-target-object.ahk
)
}

inst := IncludedClass()

if (include_value == 123)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (include_again_counter == 2)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (inst.IncludedMethod() == 42)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (obj.Alpha == 10)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (obj.Beta == 20)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

ExitApp()
