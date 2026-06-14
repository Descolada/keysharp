#NoTrayIcon

#import "Ks" { ImageCapture }
x :=
y := 0
CoordMode("Pixel", "Screen")
hbitmap := ImageCapture(100, 100, 500, 500)

l :=
t :=
r :=
b := 0
monget := MonitorGetWorkArea(, &l, &t, &r, &b)
ImageSearch(&x, &y, 0, 0, r, b, "HBITMAP:" hbitmap)

if (x == 100 && y == 100)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"
