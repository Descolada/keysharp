s := "This is a test STRING"

if (s.EndsWith(" STRING", true))
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"
	
if (!s.EndsWith(" string", true))
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"
	
if (s.EndsWith(" string", false))
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"
	
if (s.StartsWith("This ", true))
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"
	
if (!s.StartsWith("this ", true))
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"
	
if (s.StartsWith("tHiS ", false))
	FileAppend "pass", "*"
else
	FileAppend "fail", "*"
	