obj := { a: 1, b: 2 }

cap0 := ObjGetCapacity(obj)
if (cap0 >= 2)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

cap1 := ObjSetCapacity(obj, 64)
if (cap1 >= 64 && ObjGetCapacity(obj) >= 64)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

count := 0
for name, value in ObjOwnProps(obj)
{
	if ((name = "a" && value = 1) || (name = "b" && value = 2))
		count += 1
}

if (count = 2)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (obj.OwnPropCount() == 2)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

baseObj := { c: 3 }
ObjSetBase(obj, baseObj)

if (HasBase(obj, baseObj))
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

gotBase := ObjGetBase(obj)
if (gotBase.c = 3)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"

if (obj.c = 3)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"
