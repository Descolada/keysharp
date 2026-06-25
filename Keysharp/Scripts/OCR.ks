/*
    OCR library for AHK v2 / Keysharp (cross-platform).

    Purpose:
        Recognizes text in an image and returns a structured result (lines + words, each with its
        text and bounding box / position on screen), plus convenience helpers for highlighting,
        clicking, searching and sorting results. This is a bare-bones port of the UWP-based OCR.ahk
        by Descolada, re-architected so the recognition *engine* is pluggable. The image is supplied
        by the cross-platform KS Image class, so OCR itself does no screen capture.

    Engines:
        OCR is engine-agnostic. The active engine is OCR.Engine; if left unset it defaults to an
        OCR.TesseractEngine instance (Tesseract running on Windows, Linux and macOS). To use a
        different backend (a cloud OCR, Windows.Media.Ocr, PaddleOCR, ...) just assign your own:

            OCR.Engine := MyEngine()

        An engine is ANY object implementing:

            Recognize(image, options) => Array of lines
                image   : a KS Image (already prepared). Read pixels via image.GetPixelData(1|4),
                          and image.Width / image.Height.
                options : { lang, datapath } (engine-specific hints; ignore what you don't need).
                returns : an Array where each element is one text line: an Array of word descriptors,
                          each an object { Text, x, y, w, h } in image-pixel coordinates.

        OCR turns that raw geometry into OCR.Result / OCR.Line / OCR.Word objects, normalizes the
        coordinates (capture scale + screen offset) and adds all the convenience helpers. So the
        three core features the framework guarantees regardless of engine are: the full text, the
        text line-by-line, and word-by-word with each word's position.

    Tesseract requirements (default engine):
        - Tesseract installed and its shared library reachable:
            Windows : libtesseract-5.dll (e.g. the UB-Mannheim installer's C:\Program Files\Tesseract-OCR)
            Linux   : libtesseract.so.5  (e.g. apt install tesseract-ocr)
            macOS   : libtesseract.dylib  (e.g. brew install tesseract)
          If not auto-detected, set the library path:  OCR.Engine.Library := "<full path>"
        - A language data file (e.g. eng.traineddata). OCR.Engine.DataPath can point at the tessdata
          folder; OCR.Language selects the language (default "eng").
          Note: on Windows keep tessdata at an ASCII path — Tesseract's own file I/O there cannot open
          non-ASCII paths (a Tesseract limitation; on Linux/macOS UTF-8 paths work fine).

    Basic usage:
        #include <OCR>
        result := OCR(Image.FromRect(100, 100, 400, 200), {x: 100, y: 100})
        MsgBox(result.Text)
        for word in result.Words
            word.Highlight(-1)
        result.FindString("Save").Click()

    OCR(Bitmap, Options?) returns an OCR.Result:
        result.Text         => all recognized text
        result.Lines        => array of OCR.Line objects
        result.Words        => array of OCR.Word objects
        result.ImageWidth/Height
        result.FindString(Needle, Options?) / FindStrings / Filter(cb) / Crop(x1,y1,x2,y2)

    OCR.Line / OCR.Word:  .Text, .x, .y, .w, .h, .BoundingRect (Word also has .Conf 0-100; Line also has .Words)
    Common methods (Result/Line/Word): .Highlight(showTime?, color:="Red", d:=2), .ClearHighlight(),
        .Click(WhichButton?, ClickCount?, DownOrUp?)

    Static helpers: OCR.GetVersion(), OCR.GetAvailableLanguages(), OCR.WaitText(...),
        OCR.WordsBoundingRect(words*), OCR.ClearAllHighlights(), OCR.Cluster(...),
        OCR.SortArray/ReverseArray/UniqueArray/FlattenArray.

    Options object for OCR(Bitmap, Options) (all optional): {lang, datapath, x, y}
        lang, datapath : forwarded to the engine.
        x, y           : screen offset added to result coordinates (use the capture rectangle's
                         top-left so Highlight/Click land on screen). Coordinates are also divided by
                         the image's capture scale, so on a HiDPI display they map back to logical units.
*/

#Requires AutoHotkey v2

#import KS { Image }

class OCR {
    static Version => "1.0.0"

    ; --- Generic configuration ---
    static Language := "eng"   ; default OCR language, forwarded to the engine (override per call via Options.lang)
    static Engine := ""        ; active recognition engine ("" = lazily create an OCR.TesseractEngine)

    static __HighlightGuis := Map()   ; object -> array of border Gui strips

    /**
     * Recognizes text in an image and returns an OCR.Result.
     * @param Bitmap A KS Image object, or anything Image.FromBitmap accepts (a bitmap handle / file).
     * @param Options Optional {lang, datapath, x, y}. See file header.
     * @returns {OCR.Result}
     */
    static Call(Bitmap, Options := 0) {
        ; Note: the local is named "img", not "image" — AHK variable names are case-insensitive, so a
        ; local "image" would shadow the imported Image class and break "Image.FromBitmap" below.
        local img := (Bitmap is Image) ? Bitmap : Image.FromBitmap(Bitmap)
        local lang := OCR.__GetOpt(Options, "lang", OCR.__GetOpt(Options, "language", OCR.Language))
        local datapath := OCR.__GetOpt(Options, "datapath", "")
        local ox := OCR.__GetOpt(Options, "x", 0), oy := OCR.__GetOpt(Options, "y", 0)

        local rawLines := OCR.__GetEngine().Recognize(img, {lang: lang, datapath: datapath})
        local result := OCR.__BuildResult(rawLines)
        result.DefineProp("ImageWidth", {value: img.Width})
        result.DefineProp("ImageHeight", {value: img.Height})
        OCR.__FinalizeResult(result, img.ScaleX, img.ScaleY, ox, oy)
        return result
    }

    ; Returns the active engine, creating the default Tesseract engine on first use.
    static __GetEngine() {
        if !OCR.Engine
            OCR.Engine := OCR.TesseractEngine()
        return OCR.Engine
    }

    ; Returns the engine's version/info string (if the engine exposes GetVersion), else "".
    static GetVersion() {
        local eng := OCR.__GetEngine()
        return eng.HasMethod("GetVersion") ? eng.GetVersion() : ""
    }

    ; Returns the languages the engine can use (if it exposes GetAvailableLanguages), else an empty array.
    static GetAvailableLanguages() {
        local eng := OCR.__GetEngine()
        return eng.HasMethod("GetAvailableLanguages") ? eng.GetAvailableLanguages() : []
    }

    ; Removes all highlights created by Highlight().
    static ClearAllHighlights() {
        local key, strips, g
        for key, strips in OCR.__HighlightGuis
            for g in strips
                try g.Destroy()
        OCR.__HighlightGuis := Map()
        return OCR
    }

    /**
     * Returns a bounding rectangle {x,y,w,h,x2,y2} enclosing the provided Word/Line objects.
     * @param words One or more objects with x,y,w,h. Requires at least one argument.
     */
    static WordsBoundingRect(words*) {
        if !words.Length
            throw ValueError("This function requires at least one argument", -1)
        local x1 := 100000000, y1 := 100000000, x2 := -100000000, y2 := -100000000, word
        for word in words
            x1 := Min(word.x, x1), y1 := Min(word.y, y1), x2 := Max(word.x + word.w, x2), y2 := Max(word.y + word.h, y2)
        return {x: x1, y: y1, w: x2 - x1, h: y2 - y1, x2: x2, y2: y2}
    }

    /**
     * Repeatedly captures + OCRs until the needle text appears or the timeout elapses.
     * @param needle The searched text.
     * @param timeout Milliseconds; <= 0 waits indefinitely (default -1).
     * @param func A function returning an OCR.Result. Default OCRs the whole desktop.
     * @param casesense Case-sensitivity for the default comparison.
     * @param comparefunc Custom (haystack, needle) search; if given, casesense is ignored.
     * @returns {OCR.Result|""}
     */
    static WaitText(needle, timeout := -1, func?, casesense := false, comparefunc?) {
        local endTime := A_TickCount + timeout, result, line, total
        if !IsSet(func)
            func := () => OCR(Image.FromDesktop())
        if !IsSet(comparefunc)
            comparefunc := InStr.Bind(, , casesense)
        while (timeout > 0 ? (A_TickCount < endTime) : 1) {
            result := func(), total := ""
            for line in result.Lines
                total .= line.Text "`n"
            if comparefunc(Trim(total, "`n"), needle)
                return result
        }
        return ""
    }

    class Common {
        x {
            get => this.BoundingRect.x
        }
        y {
            get => this.BoundingRect.y
        }
        w {
            get => this.BoundingRect.w
        }
        h {
            get => this.BoundingRect.h
        }

        /**
         * Highlights the object on the screen with a colored border made of four thin GUI strips.
         * @param showTime Default 2000ms.
         *   Unset       - if already highlighted, removes it; otherwise highlights for 2 seconds.
         *   0           - indefinite highlight.
         *   positive ms - highlight and block for that long, then clear.
         *   negative ms - highlight for that long without blocking (auto-clears via a timer).
         *   "clear"     - remove this object's highlight.
         *   "clearall"  - remove every OCR highlight.
         * @param color Border color (default "Red").
         * @param d Border thickness in pixels (default 2).
         * @returns {this}
         */
        Highlight(showTime?, color := "Red", d := 2) {
            local x, y, w, h, i, x1, y1, w1, h1, g, strips
            if IsSet(showTime) {
                if (showTime = "clearall") {
                    OCR.ClearAllHighlights()
                    return this
                }
                if (showTime = "clear") {
                    if OCR.__HighlightGuis.Has(this) {
                        for g in OCR.__HighlightGuis[this]
                            try g.Destroy()
                        OCR.__HighlightGuis.Delete(this)
                    }
                    return this
                }
            }
            if !IsSet(showTime) {
                if OCR.__HighlightGuis.Has(this) {
                    for g in OCR.__HighlightGuis[this]
                        try g.Destroy()
                    OCR.__HighlightGuis.Delete(this)
                    return this
                }
                showTime := 2000
            }

            x := this.x, y := this.y, w := this.w, h := this.h
            if this.HasProp("Relative") {
                x += this.Relative.HasProp("x") ? this.Relative.x : 0
                y += this.Relative.HasProp("y") ? this.Relative.y : 0
            }
            if (w < 1 || h < 1)
                return this

            strips := []
            Loop 4
                strips.Push(Gui("+AlwaysOnTop -Caption +ToolWindow -DPIScale +E0x08000000 +ClickThrough"))
            Loop 4 {
                i := A_Index
                x1 := (i = 2 ? x + w : x - d)
                y1 := (i = 3 ? y + h : y - d)
                w1 := (i = 1 or i = 3 ? w + 2 * d : d)
                h1 := (i = 2 or i = 4 ? h + 2 * d : d)
                strips[i].BackColor := color
                strips[i].Show("NA x" x1 " y" y1 " w" w1 " h" h1)
            }
            OCR.__HighlightGuis[this] := strips

            if (showTime > 0) {
                Sleep(showTime)
                this.Highlight()
            } else if (showTime < 0)
                SetTimer(ObjBindMethod(this, "Highlight", "clear"), -Abs(showTime))
            return this
        }

        ClearHighlight() => this.Highlight("clear")

        /**
         * Clicks the center of the object (in screen CoordMode). If the object has a Relative property
         * with x/y, those are added as an offset.
         */
        Click(WhichButton := "left", ClickCount := 1, DownOrUp := "") {
            local x := this.x, y := this.y, w := this.w, h := this.h, cx, cy, saveCoordMode
            if this.HasProp("Relative") {
                x += this.Relative.HasProp("x") ? this.Relative.x : 0
                y += this.Relative.HasProp("y") ? this.Relative.y : 0
            }
            cx := x + w // 2, cy := y + h // 2
            saveCoordMode := A_CoordModeMouse
            CoordMode("Mouse", "Screen")
            Click(cx " " cy " " WhichButton (ClickCount != "" ? " " ClickCount : "") (DownOrUp ? " " DownOrUp : ""))
            CoordMode("Mouse", saveCoordMode)
            return this
        }

        /**
         * Adds an offset to every Word's coordinates (and the object's own if it has Words). Useful to
         * convert from image-relative to screen coordinates after the fact.
         */
        OffsetCoordinates(offsetX, offsetY) {
            local word
            if (offsetX = 0 && offsetY = 0)
                return this
            if this.HasProp("Words")
                for word in this.Words
                    OCR.__SetRect(word, word.x + offsetX, word.y + offsetY, word.w, word.h)
            return this
        }
    }

    class Result extends OCR.Common {
        /**
         * Finds the first occurrence of Needle and returns a new OCR.Result containing only the match.
         * Partial matches return the whole word ("wo" in "hello world" -> "world").
         * @param Needle The string to find.
         * @param Options {CaseSense:false, IgnoreLinebreaks:false, AllowOverlap:false, i:1, x, y, w, h, SearchFunc}
         */
        FindString(Needle, Options := "") => this.__FindString(Needle, Options, false)

        /**
         * Finds all occurrences of Needle and returns an array of OCR.Result objects.
         */
        FindStrings(Needle, Options := "") => this.__FindString(Needle, Options, true)

        __FindString(Needle, Options, All) {
            local CaseSense := false, IgnoreLinebreaks := false, AllowOverlap := false, i := 1, SearchFunc, x, y, w, h
            local fullHaystackLinebreaks := "`n", offset := 0, line, counter := 0, x1, y1, x2, y2, result, results := [], word
            local tokenizedHaystack, fullHaystackNoLinebreaks, fullHaystack, fullFirst, fullLast, currentHaystack
            local loc, foundNeedle, foundLen, tokenizedNeedle, wsNeedle, wsSplit, lbNeedle, lbSplit, preceding, startingWord
            local foundWords, foundLines

            if !(Needle is String)
                throw TypeError("Needle is required to be a string, not type " Type(Needle), -1)
            if (Trim(Needle, " `t`n`r") == "")
                throw ValueError("Needle cannot be an empty string", -1)

            CaseSense := OCR.__GetOpt(Options, "CaseSense", CaseSense)
            IgnoreLinebreaks := OCR.__GetOpt(Options, "IgnoreLinebreaks", IgnoreLinebreaks)
            AllowOverlap := OCR.__GetOpt(Options, "AllowOverlap", AllowOverlap)
            i := OCR.__GetOpt(Options, "i", i)
            if OCR.__HasOpt(Options, "SearchFunc")
                SearchFunc := Options.SearchFunc
            if OCR.__HasOpt(Options, "x")
                x := Options.x
            if OCR.__HasOpt(Options, "y")
                y := Options.y
            if OCR.__HasOpt(Options, "w")
                w := Options.w
            if OCR.__HasOpt(Options, "h")
                h := Options.h

            if !IsSet(SearchFunc)
                SearchFunc := (haystack, needle, &foundstr) => (pos := InStr(haystack, needle, CaseSense), foundstr := SubStr(haystack, pos, StrLen(needle)), pos)

            if (IsSet(x) || IsSet(y) || IsSet(w) || IsSet(h))
                x1 := x ?? -100000, y1 := y ?? -100000, x2 := IsSet(w) ? x + w : 100000, y2 := IsSet(h) ? y + h : 100000

            tokenizedHaystack := [IgnoreLinebreaks ? " " : "`n"]
            for line in this.Lines {
                fullHaystackLinebreaks .= line.Text "`n"
                for word in line.Words
                    tokenizedHaystack.Push(word, " ")
                tokenizedHaystack.Pop()
                tokenizedHaystack.Push(IgnoreLinebreaks ? " " : "`n")
            }

            fullHaystackNoLinebreaks := StrReplace(fullHaystackLinebreaks, "`n", " ")
            fullHaystack := IgnoreLinebreaks ? fullHaystackNoLinebreaks : fullHaystackLinebreaks

            Needle := RegExReplace(StrReplace(Needle, "`t", " "), " +", " ")
            fullFirst := SubStr(Needle, 1, 1) ~= "[ \n]", fullLast := SubStr(Needle, -1, 1) ~= "[ \n]"

            currentHaystack := fullHaystack
            Loop {
                if !(loc := SearchFunc(currentHaystack, Needle, &foundNeedle))
                    break
                if IsObject(foundNeedle)
                    foundNeedle := foundNeedle[]

                foundLen := AllowOverlap ? 1 : StrLen(foundNeedle)
                currentHaystack := SubStr(currentHaystack, loc + foundLen)
                offset += loc + foundLen - 1

                if (++counter < i)
                    continue

                tokenizedNeedle := []
                for wsNeedle in wsSplit := StrSplit(foundNeedle, " ") {
                    for lbNeedle in lbSplit := StrSplit(wsNeedle, "`n")
                        tokenizedNeedle.Push(lbNeedle, "`n")
                    if lbSplit.Length
                        tokenizedNeedle.Pop()
                    tokenizedNeedle.Push(" ")
                }
                tokenizedNeedle.Pop()

                preceding := SubStr(fullHaystackNoLinebreaks, 1, offset - foundLen)
                StrReplace(preceding, " ", , , &startingWord := 0)
                startingWord := startingWord * 2 + fullFirst - 1

                foundNeedle := "", foundWords := [], foundLines := [], line := OCR.Line()
                line.DefineProp("Words", {value: []}), line.DefineProp("Text", {value: ""})
                Loop tokenizedNeedle.Length {
                    word := tokenizedHaystack[startingWord + A_Index]
                    if (word == "`n") {
                        foundNeedle .= line.Text
                        line.DefineProp("Text", {value: RTrim(line.Text)})
                        if line.Words.Length
                            OCR.__SetRect(line, OCR.WordsBoundingRect(line.Words*))
                        foundLines.Push(line)
                        line := OCR.Line()
                        line.DefineProp("Words", {value: []}), line.DefineProp("Text", {value: ""})
                    }
                    if !IsObject(word)
                        continue
                    if (IsSet(x1) && (word.x < x1 || word.y < y1 || word.x + word.w > x2 || word.y + word.h > y2)) {
                        counter--
                        continue 2
                    }
                    line.Words.Push(word), line.DefineProp("Text", {value: line.Text word.Text " "})
                }
                if (line.Text != "") {
                    foundNeedle .= line.Text
                    line.DefineProp("Text", {value: RTrim(line.Text)})
                    if line.Words.Length
                        OCR.__SetRect(line, OCR.WordsBoundingRect(line.Words*))
                    foundLines.Push(line)
                }

                result := OCR.Result()
                result.DefineProp("ImageWidth", {value: this.ImageWidth})
                result.DefineProp("ImageHeight", {value: this.ImageHeight})
                result.DefineProp("Lines", {value: foundLines})
                result.DefineProp("Words", {value: foundWords := this.__CollectWords(foundLines)})
                result.DefineProp("Text", {value: foundNeedle})
                if foundWords.Length
                    OCR.__SetRect(result, OCR.WordsBoundingRect(foundWords*))
                else
                    OCR.__SetRect(result, {x: 0, y: 0, w: 0, h: 0})

                if All
                    results.Push(result)
                else
                    return result
            }
            if All
                return results
            throw TargetError('The target string "' Needle '" was not found', -1)
        }

        /**
         * Returns a new OCR.Result containing only the words for which callback(word) is truthy.
         */
        Filter(callback) {
            if !HasMethod(callback)
                throw ValueError("Filter callback must be a function", -1)
            local line, word, croppedLines := [], croppedWords, lineText, allWords := [], nl, txt := "", result
            for line in this.Lines {
                croppedWords := [], lineText := ""
                for word in line.Words
                    if callback(word)
                        croppedWords.Push(word), allWords.Push(word), lineText .= word.Text " "
                if croppedWords.Length {
                    nl := OCR.Line()
                    nl.DefineProp("Words", {value: croppedWords})
                    nl.DefineProp("Text", {value: Trim(lineText)})
                    OCR.__SetRect(nl, OCR.WordsBoundingRect(croppedWords*))
                    croppedLines.Push(nl)
                }
            }
            result := OCR.Result()
            result.DefineProp("ImageWidth", {value: this.ImageWidth})
            result.DefineProp("ImageHeight", {value: this.ImageHeight})
            result.DefineProp("Lines", {value: croppedLines})
            result.DefineProp("Words", {value: allWords})
            for line in croppedLines
                txt .= line.Text "`n"
            result.DefineProp("Text", {value: RTrim(txt, "`n")})
            if allWords.Length
                OCR.__SetRect(result, OCR.WordsBoundingRect(allWords*))
            else
                OCR.__SetRect(result, {x: 0, y: 0, w: 0, h: 0})
            return result
        }

        /**
         * Crops the result to words fully inside the rectangle defined by points (x1,y1) and (x2,y2).
         * Coordinates are relative to the result object (same space as the words).
         */
        Crop(x1 := -100000, y1 := -100000, x2 := 100000, y2 := 100000)
            => this.Filter((word) => word.x >= x1 && word.y >= y1 && (word.x + word.w) <= x2 && (word.y + word.h) <= y2)

        __CollectWords(lines) {
            local words := [], line, word
            for line in lines
                for word in line.Words
                    words.Push(word)
            return words
        }
    }

    class Line extends OCR.Common {
    }

    class Word extends OCR.Common {
    }

    ;; ---------------------------------------------------------------------------------------------
    ;; Sorting / clustering helpers (ported from OCR.ahk)
    ;; ---------------------------------------------------------------------------------------------

    /**
     * Clusters objects (Words/Lines) into lines using a 2D DBSCAN. Returns an array of objects with
     * {x,y,w,h,Text,Words}. See OCR.ahk for full parameter docs.
     */
    static Cluster(objs, eps_x := -1, eps_y := -1, minPts := 1, compareFunc?, &noise?) {
        local clusters := [], cluster, word, point, br, sum := 0, t
        local visited := Map(), clustered := Map(), C := [], c_n := 0, neighbourPts := []
        noise := IsSet(noise) && (noise is Array) ? noise : []
        if !IsObject(objs) || !(objs is Array)
            throw ValueError("objs argument must be an Array", -1)
        if !objs.Length
            return []
        if (IsSet(compareFunc) && !HasMethod(compareFunc))
            throw ValueError("compareFunc must be a valid function", -1)

        if !IsSet(compareFunc) {
            if (eps_y < 0) {
                for point in objs
                    sum += point.h
                eps_y := (sum // objs.Length) // 2
            }
            compareFunc := (p1, p2) => Abs(p1.y + p1.h // 2 - p2.y - p2.h // 2) < eps_y && (eps_x < 0 || (Abs(p1.x + p1.w - p2.x) < eps_x || Abs(p1.x - p2.x - p2.w) < eps_x))
        }

        for point in objs {
            visited[point] := 1, neighbourPts := [], RegionQuery(point)
            if !clustered.Has(point) {
                C.Push([]), c_n += 1, C[c_n].Push(point), clustered[point] := 1
                ExpandCluster(point)
            }
            if (C[c_n].Length < minPts)
                noise.Push(C[c_n]), C.RemoveAt(c_n), c_n--
        }

        for cluster in C {
            OCR.SortArray(cluster, , "x")
            br := OCR.Common()
            br.DefineProp("BoundingRect", {value: OCR.WordsBoundingRect(cluster*)})
            br.DefineProp("Words", {value: cluster})
            t := ""
            for word in cluster
                t .= word.Text " "
            br.DefineProp("Text", {value: RTrim(t)})
            clusters.Push(br)
        }
        OCR.SortArray(clusters, , "y")
        return clusters

        ExpandCluster(P) {
            local point
            for point in neighbourPts {
                if !visited.Has(point) {
                    visited[point] := 1, RegionQuery(point)
                    if !clustered.Has(point)
                        C[c_n].Push(point), clustered[point] := 1
                }
            }
        }
        RegionQuery(P) {
            local point
            for point in objs
                if !visited.Has(point)
                    if compareFunc(P, point)
                        neighbourPts.Push(point)
        }
    }

    /**
     * Sorts an array in-place. optionsOrCallback: "N" numeric (default), "C"/"C1"/"COn" case-sensitive,
     * "C0"/"COff" case-insensitive, "Random", or a custom comparator. key optionally sorts by obj.%key%.
     */
    static SortArray(arr, optionsOrCallback := "N", key?) {
        local compareFunc, len, i, j, tmp
        if (arr.Length < 2)
            return arr
        if HasMethod(optionsOrCallback)
            compareFunc := optionsOrCallback, optionsOrCallback := ""
        else {
            if InStr(optionsOrCallback, "N")
                compareFunc := IsSet(key) ? NumericCompareKey.Bind(key) : NumericCompare
            if RegExMatch(optionsOrCallback, "i)C(?!0)|C1|COn")
                compareFunc := IsSet(key) ? StringCompareKey.Bind(key, , true) : StringCompare.Bind(, , true)
            if RegExMatch(optionsOrCallback, "i)C0|COff")
                compareFunc := IsSet(key) ? StringCompareKey.Bind(key) : StringCompare
            if InStr(optionsOrCallback, "Random") {
                len := arr.Length
                Loop len - 1 {
                    i := len + 1 - A_Index
                    j := Random(1, i)
                    if (j != i)
                        tmp := arr[i], arr[i] := arr[j], arr[j] := tmp
                }
                return arr
            }
            if !IsSet(compareFunc)
                throw ValueError("No valid options provided!", -1)
        }
        QuickSort(1, arr.Length)
        if RegExMatch(optionsOrCallback, "i)R(?!a)")
            OCR.ReverseArray(arr)
        if InStr(optionsOrCallback, "U")
            arr := OCR.UniqueArray(arr)
        return arr

        NumericCompare(left, right) => (left > right) - (left < right)
        NumericCompareKey(key, left, right) => ((f1 := left.HasProp("__Item") ? left[key] : left.%key%), (f2 := right.HasProp("__Item") ? right[key] : right.%key%), (f1 > f2) - (f1 < f2))
        StringCompare(left, right, casesense := false) => StrCompare(left "", right "", casesense)
        StringCompareKey(key, left, right, casesense := false) => StrCompare((left.HasProp("__Item") ? left[key] : left.%key%) "", (right.HasProp("__Item") ? right[key] : right.%key%) "", casesense)

        QuickSort(left, right) {
            local i := left, j := right, pivot := arr[(left + right) // 2], temp
            while (i <= j) {
                while (compareFunc(arr[i], pivot) < 0)
                    i++
                while (compareFunc(arr[j], pivot) > 0)
                    j--
                if (i <= j) {
                    temp := arr[i], arr[i] := arr[j], arr[j] := temp
                    i++, j--
                }
            }
            if (left < j)
                QuickSort(left, j)
            if (i < right)
                QuickSort(i, right)
        }
    }

    ; Reverses an array in-place.
    static ReverseArray(arr) {
        local len := arr.Length + 1, max := (len // 2), i := 0, temp
        while (++i <= max)
            temp := arr[len - i], arr[len - i] := arr[i], arr[i] := temp
        return arr
    }

    ; Returns a new array with only unique values.
    static UniqueArray(arr) {
        local unique := Map(), v
        for v in arr
            unique[v] := 1
        return [unique*]
    }

    ; Flattens a (possibly nested) array into a one-dimensional array.
    static FlattenArray(arr) {
        local r := [], v
        for v in arr {
            if (v is Array)
                r.Push(OCR.FlattenArray(v)*)
            else
                r.Push(v)
        }
        return r
    }

    ;; ---------------------------------------------------------------------------------------------
    ;; Internal: engine-agnostic result construction
    ;; ---------------------------------------------------------------------------------------------

    ; Builds an OCR.Result from an engine's raw output (an array of lines, each an array of word
    ; descriptors {Text, x, y, w, h} in image-pixel coordinates).
    static __BuildResult(rawLines) {
        local lineObjs := [], allWords := [], fullText := "", rawWords, rw, words, word, text, line, result
        for rawWords in rawLines {
            if !(rawWords is Array) || !rawWords.Length
                continue
            words := [], text := ""
            for rw in rawWords {
                word := OCR.Word()
                word.DefineProp("Text", {value: rw.Text})
                OCR.__SetRect(word, rw.x, rw.y, rw.w, rw.h)
                ; Conf is 0-100 recognition confidence (Tesseract); "" for engines that don't report it.
                word.DefineProp("Conf", {value: rw.HasProp("Conf") ? rw.Conf : ""})
                words.Push(word), allWords.Push(word)
                text .= rw.Text " "
            }
            text := RTrim(text)
            line := OCR.Line()
            line.DefineProp("Words", {value: words})
            line.DefineProp("Text", {value: text})
            lineObjs.Push(line)
            fullText .= text "`n"
        }

        result := OCR.Result()
        result.DefineProp("Lines", {value: lineObjs})
        result.DefineProp("Words", {value: allWords})
        result.DefineProp("Text", {value: RTrim(fullText, "`n")})
        return result
    }

    ; Normalizes word coordinates (physical image pixels -> logical units + screen offset), then
    ; computes line/result bounding rects from the final word coordinates.
    static __FinalizeResult(result, sx, sy, ox, oy) {
        local word, line
        if (sx != 1 || sy != 1 || ox != 0 || oy != 0)
            for word in result.Words
                OCR.__SetRect(word, Integer(word.x / sx) + ox, Integer(word.y / sy) + oy, Integer(word.w / sx), Integer(word.h / sy))
        for line in result.Lines
            OCR.__SetRect(line, line.Words.Length ? OCR.WordsBoundingRect(line.Words*) : {x: 0, y: 0, w: 0, h: 0})
        OCR.__SetRect(result, result.Words.Length ? OCR.WordsBoundingRect(result.Words*) : {x: 0, y: 0, w: 0, h: 0})
        return result
    }

    ; Sets x/y/w/h and BoundingRect on an object. Accepts either (obj, x, y, w, h) or (obj, rectObject).
    static __SetRect(obj, x, y?, w?, h?) {
        local rect
        if !IsSet(y) {
            rect := x
            x := rect.x, y := rect.y, w := rect.w, h := rect.h
        }
        obj.DefineProp("x", {value: x})
        obj.DefineProp("y", {value: y})
        obj.DefineProp("w", {value: w})
        obj.DefineProp("h", {value: h})
        obj.DefineProp("BoundingRect", {value: {x: x, y: y, w: w, h: h}})
        return obj
    }

    ; Whether an options object carries a given named property.
    static __HasOpt(obj, name) => IsObject(obj) && (Type(obj) = "Object") && obj.HasProp(name)

    ; Returns obj.%name% if present, otherwise the supplied default.
    static __GetOpt(obj, name, default) => OCR.__HasOpt(obj, name) ? obj.%name% : default

    ;; ---------------------------------------------------------------------------------------------
    ;; Tesseract engine (the default OCR.Engine). All Tesseract/DllCall specifics live here so the
    ;; backend can be swapped by assigning OCR.Engine to any object with a matching Recognize().
    ;; ---------------------------------------------------------------------------------------------

    class TesseractEngine {
        Library := ""           ; full path/name of the Tesseract shared library ("" = auto-detect)
        DataPath := ""          ; tessdata folder ("" = derive from the library location / TESSDATA_PREFIX)
        SourceResolution := 70  ; DPI hint passed to Tesseract (only silences a resolution warning)

        __LibName := ""         ; resolved library path/name used as the DllCall prefix
        __LibHandle := 0        ; retained module handle (keeps the library pinned across calls)
        __DefaultDataPath := "" ; directory derived from the resolved library, used to find tessdata

        __New(library := "", datapath := "") {
            if (library != "")
                this.Library := library
            if (datapath != "")
                this.DataPath := datapath
        }

        /**
         * Engine entry point. Recognizes text in an image and returns the raw lines/words geometry.
         * @param image A KS Image.
         * @param options {lang, datapath}.
         * @returns {Array} Array of lines, each an array of {Text, x, y, w, h} (image-pixel coords).
         */
        Recognize(image, options := 0) {
            local lang := "eng", datapath := this.DataPath, w := image.Width, h := image.Height, buf, handle, lines
            if IsObject(options) {
                if (options.HasProp("lang") && options.lang != "")
                    lang := options.lang
                if (options.HasProp("datapath") && options.datapath != "")
                    datapath := options.datapath
            }
            if (w < 1 || h < 1)
                throw ValueError("The image has no pixels to OCR.", -1)

            ; 8-bit grayscale, tightly packed and top-down: exactly what Tesseract wants for
            ; bytes_per_pixel=1. The buffer stays in scope until SetImage has copied it.
            buf := image.GetPixelData(1)
            this.__EnsureLoaded()
            handle := DllCall(this.__Sym("TessBaseAPICreate"), "Cdecl Ptr")
            if !handle
                throw Error("Failed to create a Tesseract engine instance.", -1)
            try {
                this.__InitLanguage(handle, datapath, lang)
                DllCall(this.__Sym("TessBaseAPISetImage"), "Ptr", handle, "Ptr", buf.Ptr, "Int", w, "Int", h, "Int", 1, "Int", w, "Cdecl")
                DllCall(this.__Sym("TessBaseAPISetSourceResolution"), "Ptr", handle, "Int", this.SourceResolution, "Cdecl")
                if (DllCall(this.__Sym("TessBaseAPIRecognize"), "Ptr", handle, "Ptr", 0, "Cdecl Int") != 0)
                    throw Error("Tesseract failed to recognize the image.", -1)
                lines := this.__BuildLines(handle)
            } finally {
                DllCall(this.__Sym("TessBaseAPIEnd"), "Ptr", handle, "Cdecl")
                DllCall(this.__Sym("TessBaseAPIDelete"), "Ptr", handle, "Cdecl")
            }
            return lines
        }

        ; Returns the Tesseract version string (also a quick way to confirm the library loaded).
        GetVersion() {
            this.__EnsureLoaded()
            local p := DllCall(this.__Sym("TessVersion"), "Cdecl Ptr")
            return p ? StrGet(p, "UTF-8") : ""
        }

        ; Returns an array of installed language codes (the tessdata\*.traineddata basenames), so a
        ; caller can discover what OCR.Language values are usable.
        GetAvailableLanguages() {
            this.__EnsureLoaded()
            local dir := this.__TessdataDir(), langs := []
            if (dir != "")
                Loop Files, dir "/" "*.traineddata"
                    langs.Push(StrReplace(A_LoopFileName, ".traineddata", ""))
            return langs
        }

        ; Resolves the tessdata directory (the first datapath candidate that actually holds *.traineddata).
        __TessdataDir() {
            local cands := [], d
            if (this.DataPath != "")
                cands.Push(this.DataPath)
            if (this.__DefaultDataPath != "") {
                cands.Push(this.__DefaultDataPath "/" "tessdata")
                cands.Push(this.__DefaultDataPath)
            }
            for d in cands
                if (d != "")
                    Loop Files, d "/" "*.traineddata"
                        return d   ; first candidate that contains at least one traineddata file
            return ""
        }

        ; Iterates the recognized page, grouping words (RIL_WORD = 3) into text lines (RIL_TEXTLINE = 2).
        ; Each word also carries Conf (0-100 recognition confidence). The iterator is deleted in a finally
        ; so it can't leak if a read throws.
        __BuildLines(handle) {
            local lines := [], curWords := "", pText, wordText, l, t, r, b, conf, pageIt
            local it := DllCall(this.__Sym("TessBaseAPIGetIterator"), "Ptr", handle, "Cdecl Ptr")
            if !it
                return lines
            try {
                pageIt := DllCall(this.__Sym("TessResultIteratorGetPageIterator"), "Ptr", it, "Cdecl Ptr")
                Loop {
                    if DllCall(this.__Sym("TessPageIteratorIsAtBeginningOf"), "Ptr", pageIt, "Int", 2, "Cdecl Int") {
                        curWords := []
                        lines.Push(curWords)
                    }
                    pText := DllCall(this.__Sym("TessResultIteratorGetUTF8Text"), "Ptr", it, "Int", 3, "Cdecl Ptr")
                    wordText := ""
                    if pText {
                        wordText := StrGet(pText, "UTF-8")
                        DllCall(this.__Sym("TessDeleteText"), "Ptr", pText, "Cdecl")
                    }
                    wordText := Trim(wordText, " `t`r`n")
                    if (wordText != "" && IsObject(curWords)) {
                        l := 0, t := 0, r := 0, b := 0
                        if DllCall(this.__Sym("TessPageIteratorBoundingBox"), "Ptr", pageIt, "Int", 3, "Int*", &l, "Int*", &t, "Int*", &r, "Int*", &b, "Cdecl Int") {
                            conf := DllCall(this.__Sym("TessResultIteratorConfidence"), "Ptr", it, "Int", 3, "Cdecl Float")
                            curWords.Push({Text: wordText, x: l, y: t, w: r - l, h: b - t, Conf: Round(conf, 2)})
                        }
                    }
                    if !DllCall(this.__Sym("TessResultIteratorNext"), "Ptr", it, "Int", 3, "Cdecl Int")
                        break
                }
            } finally {
                DllCall(this.__Sym("TessResultIteratorDelete"), "Ptr", it, "Cdecl")
            }
            return lines
        }

        ; Builds the "lib/function" string for DllCall (DllCall accepts "/" as the separator on every platform).
        __Sym(func) => this.__LibName "/" func

        ; UTF-8-encodes a string into a NUL-terminated Buffer for passing to a char* API. Tesseract's
        ; datapath/language are UTF-8; DllCall's "AStr" would encode as ASCII (mangling any non-ASCII
        ; byte to '?'), so non-ASCII tessdata paths or language codes need this instead.
        __Utf8(s) {
            local b := Buffer(StrPut(s, "UTF-8"))   ; size includes the trailing NUL
            StrPut(s, b, "UTF-8")
            return b
        }

        ; Ensures the Tesseract library is loaded and pinned.
        __EnsureLoaded() {
            local cand, handle, ok
            if this.__LibHandle
                return this.__LibName
            for cand in this.__LibraryCandidates() {
                if (cand = "")
                    continue
                handle := this.__PlatformLoad(cand)
                if !handle
                    continue
                ok := false
                try ok := DllCall(cand "/" "TessVersion", "Cdecl Ptr") != 0
                if ok {
                    this.__LibHandle := handle
                    this.__LibName := cand
                    this.__DefaultDataPath := this.__DirOf(cand)
                    return cand
                }
                this.__PlatformFree(handle)
            }
            ; Plain Error (not OSError): the actionable message is the payload, and OSError would overwrite
            ; it on Windows with the formatted last-OS-error string ("The operation completed successfully.").
            throw Error("Could not load the Tesseract library. Install Tesseract OCR and, if needed, set OCR.Engine.Library to the full path of its shared library (e.g. 'C:\Program Files\Tesseract-OCR\libtesseract-5.dll').", -1)
        }

        ; Initializes the engine for a language, trying a few datapath candidates until one succeeds.
        ; datapath/language are passed as UTF-8 (Tesseract's char* encoding); the encoded buffers are
        ; held in locals so they stay alive across the DllCall.
        __InitLanguage(handle, datapath, lang) {
            local dp, ret, candidates := [], langBuf := this.__Utf8(lang), dpBuf
            if (datapath != "")
                candidates.Push(datapath)
            else if (this.__DefaultDataPath != "") {
                candidates.Push(this.__DefaultDataPath "/" "tessdata")
                candidates.Push(this.__DefaultDataPath)
            }
            candidates.Push("")   ; "" -> NULL: fall back to TESSDATA_PREFIX / built-in default
            for dp in candidates {
                if (dp = "")
                    ret := DllCall(this.__Sym("TessBaseAPIInit3"), "Ptr", handle, "Ptr", 0, "Ptr", langBuf.Ptr, "Cdecl Int")
                else {
                    dpBuf := this.__Utf8(dp)
                    ret := DllCall(this.__Sym("TessBaseAPIInit3"), "Ptr", handle, "Ptr", dpBuf.Ptr, "Ptr", langBuf.Ptr, "Cdecl Int")
                }
                if (ret = 0)
                    return
            }
            throw Error("Tesseract could not initialize language '" lang "'. Ensure '" lang ".traineddata' is installed (set OCR.Engine.DataPath to its tessdata folder, or OCR.Language to an installed language).", -1)
        }

        ; Returns the directory part of a path ("" if none).
        __DirOf(path) {
            local p := Max(InStr(path, "\", , -1), InStr(path, "/", , -1))
            return p ? SubStr(path, 1, p - 1) : ""
        }

#if WINDOWS
        __PlatformLoad(path) {
            ; LOAD_WITH_ALTERED_SEARCH_PATH (0x8) resolves sibling dependency DLLs (leptonica, etc.)
            ; from the library's own folder. The returned handle is retained for the process lifetime,
            ; keeping the module pinned (DllCall reloads+frees by name each call; the retained ref
            ; prevents an unload that would dangle the TessBaseAPI handle).
            local h := DllCall("kernel32/LoadLibraryExW", "WStr", path, "Ptr", 0, "UInt", 0x8, "Ptr")
            if !h
                h := DllCall("kernel32/LoadLibraryW", "WStr", path, "Ptr")
            return h
        }
        __PlatformFree(handle) => DllCall("kernel32/FreeLibrary", "Ptr", handle)

        __LibraryCandidates() {
            local c := []
            if (this.Library != "")
                c.Push(this.Library)
            c.Push(A_ProgramFiles "/Tesseract-OCR/libtesseract-5.dll")
            c.Push("C:/Program Files/Tesseract-OCR/libtesseract-5.dll")
            c.Push("C:/Program Files (x86)/Tesseract-OCR/libtesseract-5.dll")
            c.Push("libtesseract-5.dll", "libtesseract-5")
            return c
        }
#endif

#if !WINDOWS
        __PlatformLoad(path) => this.__Dlopen(path)
        __PlatformFree(handle) {
            local dl
            for dl in this.__DlLibs()
                try return DllCall(dl "/" "dlclose", "Ptr", handle, "Cdecl Int")
            return 0
        }
        __Dlopen(path) {
            local dl, h
            ; RTLD_NOW (0x2) | RTLD_GLOBAL (0x100)
            for dl in this.__DlLibs() {
                try {
                    h := DllCall(dl "/" "dlopen", "AStr", path, "Int", 0x102, "Cdecl Ptr")
                    if h
                        return h
                }
            }
            return 0
        }
#endif

#if LINUX
        __DlLibs() => ["libdl.so.2", "libc.so.6", "libdl.so"]
        __LibraryCandidates() {
            local c := []
            if (this.Library != "")
                c.Push(this.Library)
            c.Push("libtesseract.so.5", "libtesseract.so.4", "libtesseract.so", "tesseract")
            return c
        }
#endif

#if OSX
        __DlLibs() => ["/usr/lib/libSystem.B.dylib"]
        __LibraryCandidates() {
            local c := []
            if (this.Library != "")
                c.Push(this.Library)
            c.Push("libtesseract.5.dylib", "libtesseract.dylib")
            c.Push("/opt/homebrew/lib/libtesseract.dylib", "/usr/local/lib/libtesseract.dylib")
            return c
        }
#endif
    }
}
