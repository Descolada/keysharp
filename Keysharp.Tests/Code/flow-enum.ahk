; Return true to continue enumerating, false to stop
EnumFunc(&x) {
    static i := 0
    x := ++i
    if (i == 4)
        i := 0
    return i != 0
}

a := 0
; An enumerator is a function which can just be called
EnumFunc(&a)
if (a == 1)
    FileAppend "pass", "*"
else
    FileAppend "fail", "*"

; Reset the internal counter to 0
EnumFunc(&a)
EnumFunc(&a)
EnumFunc(&a)

a := 0
; Iterable objects have __Enum(NumOfArgs) defined, which can be called to get an enumerator with a certain number of arguments
arr := [2,3,4]
e := arr.__Enum(2)

while (e(&i, &j))
    a += j

if (a == 9)
    FileAppend "pass", "*"
else
    FileAppend "fail", "*"

; A for loop checks whether the passed object has __Enum, and calls it with the number of requested arguments.
; For example `for i, j, k in e` calls `e.__Enum(3)` to get the enumerator function (if __Enum is defined) 
; If an object doesn't contain __Enum, then the object/function is assumed to be the enumerator
a := 0
for i in EnumFunc {
    a += i
}

if (a == 6)
    FileAppend "pass", "*"
else
    FileAppend "fail", "*"

Enum1(&x) {
    static i := 0
    x := Mod(++i, 4) * 2
    return x // 2
}

Enum2(&x, &y) {
    static i := 0
    x := Mod(++i, 4), y := x * 2
    return x
}

o := {}.DefineProp("__Enum", {call: (this, num) => num == 1 ? Enum1 : Enum2})

a := 0
; In this case __Enum will be called with the number of requested paramenters, so it is equivalent to `for i in o.__Enum(1)`
for i in o {
    a += i
}

if (a == 12)
    FileAppend "pass", "*"
else
    FileAppend "fail", "*"

a := 0, b := 0
; And this one is equivalent to `for i in o.__Enum(2)`
for i, j in o {
    a += i
    b += j
}

if (a == 6)
    FileAppend "pass", "*"
else
    FileAppend "fail", "*"

if (b == 12)
    FileAppend "pass", "*"
else
    FileAppend "fail", "*"

a := 0
; Here `o.__Enum(1)` is called
a := [o*]

if (a[3] == 6)
    FileAppend "pass", "*"
else
    FileAppend "fail", "*"

a := 0
; Using Bind we can ignore certain arguments, here the second argument is ignored
a := [o.__Enum(2).Bind(, &_)*] ; This is also testing a difficult to parse statement that ends in the * spread operator inside of an array literal.

if (a[3] == 3)
    FileAppend "pass", "*"
else
    FileAppend "fail", "*"
    /*
a := 0
; And now the first argument is ignored
bfo := o.__Enum(2).Bind(&_)
a := [bfo*]

if (a[3] == 6)
    FileAppend "pass", "*"
else
    FileAppend "fail", "*"
    */