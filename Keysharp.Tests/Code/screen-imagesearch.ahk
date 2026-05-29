#import "Ks" { ImageCapture }
x :=
y := 0 
CoordMode("Pixel", "Screen")
ImageCapture(100, 100, 500, 500, "./imagesearch.bmp")

l :=
t :=
r :=
b := 0
monget := MonitorGetWorkArea(, &l, &t, &r, &b)
ImageSearch(&x, &y, 0, 0, r, b, "./imagesearch.bmp")

if (x == 100 && y == 100)
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"