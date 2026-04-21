; Tests for AHK v2.1 PropRef / __Ref.

check(cond) {
    FileAppend cond ? "pass" : "fail", "*"
}

; --- 1. Basic PropRef via &obj.prop --------------------------------
m := { a: 1, b: "x" }
r := &m.a
check(r is PropRef)
check(r is VarRef)                 ; PropRef is a VarRef-compatible virtual reference.
check(r.__Value == 1)
r.__Value := 42
check(m.a == 42)

; --- 2. PropRef directly ------------------------------------------
r2 := PropRef(m, "b")
check(r2.__Value == "x")
r2.__Value := "y"
check(m.b == "y")

; --- 3. &arr[i] lowers to arr.__Ref("__Item", i) ------------------
arr := [10, 20, 30]
rix := &arr[2]
check(rix is PropRef)
check(rix.__Value == 20)
rix.__Value := 200
check(arr[2] == 200)

; --- 4. &map["key"] ------------------------------------------------
mp := Map("one", 1, "two", 2)
rk := &mp["one"]
check(rk.__Value == 1)
rk.__Value := 99
check(mp["one"] == 99)

; --- 5. &obj.prop[i] follows GetPropertyValue fallback rules ---------
nested := { g: [100, 200, 300] }
rn := &nested.g[2]
check(rn.__Value == 200)
rn.__Value := 222
check(nested.g[2] == 222)

; For a property without property parameters, the ref binds to the current
; property's __Item target at creation time.
holder := { g: [1, 2, 3] }
rOrig := &holder.g[1]
origArr := holder.g
holder.g := [9, 9, 9]              ; swap in a fresh array
rOrig.__Value := 500               ; writes into the original array
check(origArr[1] == 500)
check(holder.g[1] == 9)            ; new array unchanged

; If the property itself accepts parameters, the ref binds to the property.
class ParamProp {
    __New(values) {
        this.store := values
    }
    data[i] {
        get => this.store[i]
        set => this.store[i] := value
    }
}
p := ParamProp([4, 5, 6])
rParam := &p.data[2]
check(rParam.__Value == 5)
p.store := [7, 8, 9]
check(rParam.__Value == 8)
rParam.__Value := 88
check(p.store[2] == 88)

; Direct PropRef construction follows the same resolution rules.
holder2 := { g: [4, 5, 6] }
rDirect := PropRef(holder2, "g", 2)
origArr2 := holder2.g
holder2.g := [7, 8, 9]
check(rDirect.__Value == 5)
rDirect.__Value := 88
check(origArr2[2] == 88)
check(holder2.g[2] == 8)

; --- 6. ByRef parameter receives PropRef ---------------------------
bump(&p) => p += 1
o := { n: 10 }
bump(&o.n)
check(o.n == 11)
a := [5, 6, 7]
bump(&a[3])
check(a[3] == 8)

; --- 7. User override of __Ref -------------------------------------
class CustomRef {
    store := Map()
    __Ref(name, args*) {
        this.store[name] := args
        return PropRef(this, name, args*)
    }
}
c := CustomRef()
_ := &c.foo                         ; triggers __Ref("foo")
check(c.store["foo"].Length == 0)

_ := &c.bar[1, 2]                   ; -> c.__Ref("bar", 1, 2)
check(c.store["bar"].Length == 2)
check(c.store["bar"][1] == 1 && c.store["bar"][2] == 2)

; --- 8. Normal __Value property is not mistaken for a ByRef ref ----
boxed := { __Value: 7 }
rBoxed := &boxed.__Value
check(rBoxed.__Value == 7)
rBoxed.__Value := 70
check(boxed.__Value == 70)
