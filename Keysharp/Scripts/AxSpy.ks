/*
    macOS Accessibility spy/library for AHK v2 / Keysharp.

    Purpose:
        Wraps and inspects the macOS Accessibility API (AXUIElement / AXObserver),
        CoreFoundation and CoreGraphics to expose accessibility trees and
        UI automation helpers, analogous to Acc.ahk on Windows and AtSpi.ks
        on Linux.

    Requirements:
        - Uses Keysharp capabilities instead of local trust-request helpers.
          The #Requires directive below lets Keysharp request/query the needed
          macOS Accessibility/Input Monitoring trust where supported.
        - Target applications may expose incomplete AX trees; sandboxed or
          privileged applications can deny some attributes/actions.

    Overview:
        Entry points (AxSpy.*):
            GetRootElement(), GetFocusedElement(), GetFocusedWindow(),
            ElementFromPoint(x, y),
            ElementFromPid(pid), ElementFromApp(nameOrPid),
            ElementFromWindowTitle(title := ""), WindowList(), Applications(),
            Observe(element, notifications, callback), ClearAllHighlights(), Viewer().

        Element properties:
            Name, Title, Description, Role/RoleName/Subrole, Value, Enabled,
            Focused, Selected, Parent, Children, Windows, Application, Window,
            Location/Position/Size, Pid, Attributes, Actions,
            ParameterizedAttributes, SelectedText, SelectedTextRange,
            NumberOfCharacters, URL, Identifier, PlaceholderValue, etc.

        Element methods:
            Attribute/SetAttribute, AttributeRaw, IsAttributeSettable,
            DoAction/DoDefaultAction/Press/ShowMenu/Increment/Decrement,
            Focus/ScrollTo/Click, text range helpers, FindElement/FindElements,
            WaitElement, ElementFromPath, Dump/DumpAll, Highlight.

        Running this file directly opens AxSpy.Viewer for inspection.
*/

#Requires capability AccessibilityAutomation, InputMonitoring

#DllLoad /System/Library/Frameworks/ApplicationServices.framework/ApplicationServices
#DllLoad /System/Library/Frameworks/CoreFoundation.framework/CoreFoundation
#DllLoad /System/Library/Frameworks/CoreGraphics.framework/CoreGraphics
#DllLoad /usr/lib/libSystem.B.dylib

if (!A_IsCompiled and A_LineFile = A_ScriptFullPath)
    AxSpy.Viewer()

class AxSpy {
    class Enumeration {
        __Item[param] {
            get {
                local k, v
                if !this.HasOwnProp("__CachedValues") {
                    this.__CachedValues := Map()
                    for k, v in this.OwnProps()
                        this.__CachedValues[v] := k
                }
                if this.__CachedValues.Has(param)
                    return this.__CachedValues[param]
                throw UnsetItemError("Property item `"" param "`" not found!", -2)
            }
        }
    }

    static LibAX := "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices"
    static LibCF := "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation"
    static LibCG := "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics"
    static LibSystem := "/usr/lib/libSystem.B.dylib"
    static SymDelim := "/"

    static kCFStringEncodingUTF8 := 0x08000100
    static kCFNumberSInt32Type := 3
    static kCFNumberSInt64Type := 4
    static kCFNumberDoubleType := 13
    static kCFNumberCFIndexType := 14
    static kCFNumberCGFloatType := 16

    static kAXValueCGPointType := 1
    static kAXValueCGSizeType := 2
    static kAXValueCGRectType := 3
    static kAXValueCFRangeType := 4
    static kAXValueAXErrorType := 5

    static CGWindowListOptionAll := 0
    static CGWindowListOptionOnScreenOnly := 1
    static CGWindowListOptionOnScreenBelowWindow := 4
    static CGWindowListExcludeDesktopElements := 16

    static Error := {
        Success:0,
        Failure:-25200,
        IllegalArgument:-25201,
        InvalidUIElement:-25202,
        InvalidUIElementObserver:-25203,
        CannotComplete:-25204,
        AttributeUnsupported:-25205,
        ActionUnsupported:-25206,
        NotificationUnsupported:-25207,
        NotImplemented:-25208,
        NotificationAlreadyRegistered:-25209,
        NotificationNotRegistered:-25210,
        APIDisabled:-25211,
        NoValue:-25212,
        ParameterizedAttributeUnsupported:-25213,
        NotEnoughPrecision:-25214,
        base:this.Enumeration.Prototype
    }

    static MatchMode := {
        RegEx:0,
        StartsWith:1,
        Substring:2,
        Exact:3,
        base:this.Enumeration.Prototype
    }

    static TreeScope := {
        Element:1,
        Children:2,
        Descendants:4,
        Family:3,
        Subtree:5,
        SubTree:5,
        base:this.Enumeration.Prototype
    }

    static TreeTraversalOptions := {
        Default:0,
        PostOrder:1,
        LastToFirst:2,
        base:this.Enumeration.Prototype
    }

    static Attribute := {
        Role:"AXRole",
        Subrole:"AXSubrole",
        RoleDescription:"AXRoleDescription",
        Help:"AXHelp",
        Title:"AXTitle",
        Description:"AXDescription",
        Identifier:"AXIdentifier",
        Parent:"AXParent",
        Children:"AXChildren",
        SelectedChildren:"AXSelectedChildren",
        VisibleChildren:"AXVisibleChildren",
        ChildrenInNavigationOrder:"AXChildrenInNavigationOrder",
        Window:"AXWindow",
        TopLevelUIElement:"AXTopLevelUIElement",
        TitleUIElement:"AXTitleUIElement",
        ServesAsTitleForUIElements:"AXServesAsTitleForUIElements",
        LinkedUIElements:"AXLinkedUIElements",
        SharedFocusElements:"AXSharedFocusElements",
        Enabled:"AXEnabled",
        ElementBusy:"AXElementBusy",
        Focused:"AXFocused",
        Position:"AXPosition",
        Size:"AXSize",
        Frame:"AXFrame",
        Value:"AXValue",
        ValueDescription:"AXValueDescription",
        MinValue:"AXMinValue",
        MaxValue:"AXMaxValue",
        ValueIncrement:"AXValueIncrement",
        ValueWraps:"AXValueWraps",
        AllowedValues:"AXAllowedValues",
        PlaceholderValue:"AXPlaceholderValue",
        SelectedText:"AXSelectedText",
        SelectedTextRange:"AXSelectedTextRange",
        SelectedTextRanges:"AXSelectedTextRanges",
        VisibleCharacterRange:"AXVisibleCharacterRange",
        NumberOfCharacters:"AXNumberOfCharacters",
        SharedTextUIElements:"AXSharedTextUIElements",
        SharedCharacterRange:"AXSharedCharacterRange",
        InsertionPointLineNumber:"AXInsertionPointLineNumber",
        Main:"AXMain",
        Minimized:"AXMinimized",
        CloseButton:"AXCloseButton",
        ZoomButton:"AXZoomButton",
        MinimizeButton:"AXMinimizeButton",
        ToolbarButton:"AXToolbarButton",
        FullScreenButton:"AXFullScreenButton",
        Proxy:"AXProxy",
        GrowArea:"AXGrowArea",
        Modal:"AXModal",
        DefaultButton:"AXDefaultButton",
        CancelButton:"AXCancelButton",
        MenuItemCmdChar:"AXMenuItemCmdChar",
        MenuItemCmdVirtualKey:"AXMenuItemCmdVirtualKey",
        MenuItemCmdGlyph:"AXMenuItemCmdGlyph",
        MenuItemCmdModifiers:"AXMenuItemCmdModifiers",
        MenuItemMarkChar:"AXMenuItemMarkChar",
        MenuItemPrimaryUIElement:"AXMenuItemPrimaryUIElement",
        ShownMenuUIElement:"AXShownMenuUIElement",
        MenuBar:"AXMenuBar",
        ExtrasMenuBar:"AXExtrasMenuBar",
        Windows:"AXWindows",
        Frontmost:"AXFrontmost",
        Hidden:"AXHidden",
        MainWindow:"AXMainWindow",
        FocusedWindow:"AXFocusedWindow",
        FocusedUIElement:"AXFocusedUIElement",
        FocusedApplication:"AXFocusedApplication",
        IsApplicationRunning:"AXIsApplicationRunning",
        HorizontalScrollBar:"AXHorizontalScrollBar",
        VerticalScrollBar:"AXVerticalScrollBar",
        Orientation:"AXOrientation",
        Header:"AXHeader",
        Edited:"AXEdited",
        Tabs:"AXTabs",
        OverflowButton:"AXOverflowButton",
        Filename:"AXFilename",
        Expanded:"AXExpanded",
        Selected:"AXSelected",
        Splitters:"AXSplitters",
        Contents:"AXContents",
        NextContents:"AXNextContents",
        PreviousContents:"AXPreviousContents",
        Document:"AXDocument",
        Incrementor:"AXIncrementor",
        DecrementButton:"AXDecrementButton",
        IncrementButton:"AXIncrementButton",
        ColumnTitle:"AXColumnTitles",
        ColumnTitles:"AXColumnTitles",
        URL:"AXURL",
        LabelUIElements:"AXLabelUIElements",
        LabelValue:"AXLabelValue",
        SearchButton:"AXSearchButton",
        ClearButton:"AXClearButton",
        AlternateUIVisible:"AXAlternateUIVisible",
        Rows:"AXRows",
        VisibleRows:"AXVisibleRows",
        SelectedRows:"AXSelectedRows",
        Columns:"AXColumns",
        VisibleColumns:"AXVisibleColumns",
        SelectedColumns:"AXSelectedColumns",
        SortDirection:"AXSortDirection",
        Index:"AXIndex",
        Disclosing:"AXDisclosing",
        DisclosedRows:"AXDisclosedRows",
        DisclosedByRow:"AXDisclosedByRow",
        DisclosureLevel:"AXDisclosureLevel",
        RowCount:"AXRowCount",
        ColumnCount:"AXColumnCount",
        OrderedByRow:"AXOrderedByRow",
        SelectedCells:"AXSelectedCells",
        VisibleCells:"AXVisibleCells",
        RowHeaderUIElements:"AXRowHeaderUIElements",
        ColumnHeaderUIElements:"AXColumnHeaderUIElements",
        RowIndexRange:"AXRowIndexRange",
        ColumnIndexRange:"AXColumnIndexRange",
        WarningValue:"AXWarningValue",
        CriticalValue:"AXCriticalValue",
        MatteHole:"AXMatteHole",
        MatteContentUIElement:"AXMatteContentUIElement",
        MarkerUIElements:"AXMarkerUIElements",
        Units:"AXUnits",
        UnitDescription:"AXUnitDescription",
        MarkerType:"AXMarkerType",
        MarkerTypeDescription:"AXMarkerTypeDescription",
        HorizontalUnits:"AXHorizontalUnits",
        VerticalUnits:"AXVerticalUnits",
        HorizontalUnitDescription:"AXHorizontalUnitDescription",
        VerticalUnitDescription:"AXVerticalUnitDescription",
        Handles:"AXHandles",
        Text:"AXText",
        VisibleText:"AXVisibleText",
        IsEditable:"AXIsEditable",
        DOMIdentifier:"AXDOMIdentifier",
        DOMClassList:"AXDOMClassList",
        ARIALandmarkRole:"AXARIALandmarkRole",
        ARIACurrent:"AXARIACurrent",
        ARIAAtomic:"AXARIAAtomic",
        ARIABusy:"AXARIABusy",
        ARIARelevant:"AXARIARelevant",
        ARIAPosInSet:"AXARIAPosInSet",
        ARIASetSize:"AXARIASetSize",
        Visited:"AXVisited",
        Loaded:"AXLoaded",
        LoadingProgress:"AXLoadingProgress",
        Required:"AXRequired",
        Invalid:"AXInvalid",
        Grabbed:"AXGrabbed",
        Owns:"AXOwns",
        DropsEffects:"AXDropsEffects",
        base:this.Enumeration.Prototype
    }

    static ParameterizedAttribute := {
        LineForIndex:"AXLineForIndex",
        RangeForLine:"AXRangeForLine",
        StringForRange:"AXStringForRange",
        RangeForPosition:"AXRangeForPosition",
        RangeForIndex:"AXRangeForIndex",
        BoundsForRange:"AXBoundsForRange",
        RTFForRange:"AXRTFForRange",
        AttributedStringForRange:"AXAttributedStringForRange",
        StyleRangeForIndex:"AXStyleRangeForIndex",
        CellForColumnAndRow:"AXCellForColumnAndRow",
        LayoutPointForScreenPoint:"AXLayoutPointForScreenPoint",
        LayoutSizeForScreenSize:"AXLayoutSizeForScreenSize",
        ScreenPointForLayoutPoint:"AXScreenPointForLayoutPoint",
        ScreenSizeForLayoutSize:"AXScreenSizeForLayoutSize",
        base:this.Enumeration.Prototype
    }

    static Action := {
        Press:"AXPress",
        Increment:"AXIncrement",
        Decrement:"AXDecrement",
        Confirm:"AXConfirm",
        Cancel:"AXCancel",
        Raise:"AXRaise",
        ShowMenu:"AXShowMenu",
        Pick:"AXPick",
        ScrollToVisible:"AXScrollToVisible",
        ShowDefaultUI:"AXShowDefaultUI",
        ShowAlternateUI:"AXShowAlternateUI",
        base:this.Enumeration.Prototype
    }

    static Notification := {
        MainWindowChanged:"AXMainWindowChanged",
        FocusedWindowChanged:"AXFocusedWindowChanged",
        FocusedUIElementChanged:"AXFocusedUIElementChanged",
        ApplicationActivated:"AXApplicationActivated",
        ApplicationDeactivated:"AXApplicationDeactivated",
        ApplicationHidden:"AXApplicationHidden",
        ApplicationShown:"AXApplicationShown",
        WindowCreated:"AXWindowCreated",
        WindowMoved:"AXWindowMoved",
        WindowResized:"AXWindowResized",
        WindowMiniaturized:"AXWindowMiniaturized",
        WindowDeminiaturized:"AXWindowDeminiaturized",
        DrawerCreated:"AXDrawerCreated",
        SheetCreated:"AXSheetCreated",
        UIElementDestroyed:"AXUIElementDestroyed",
        ValueChanged:"AXValueChanged",
        TitleChanged:"AXTitleChanged",
        Resized:"AXResized",
        Moved:"AXMoved",
        Created:"AXCreated",
        SelectedChildrenChanged:"AXSelectedChildrenChanged",
        SelectedRowsChanged:"AXSelectedRowsChanged",
        SelectedColumnsChanged:"AXSelectedColumnsChanged",
        SelectedTextChanged:"AXSelectedTextChanged",
        SelectedTextSelectionChanged:"AXSelectedTextSelectionChanged",
        RowCountChanged:"AXRowCountChanged",
        RowExpanded:"AXRowExpanded",
        RowCollapsed:"AXRowCollapsed",
        SelectedCellsChanged:"AXSelectedCellsChanged",
        UnitsChanged:"AXUnitsChanged",
        SelectedChildrenMoved:"AXSelectedChildrenMoved",
        SelectedMoved:"AXSelectedMoved",
        LayoutChanged:"AXLayoutChanged",
        LoadComplete:"AXLoadComplete",
        HelpTagCreated:"AXHelpTagCreated",
        MenuOpened:"AXMenuOpened",
        MenuClosed:"AXMenuClosed",
        MenuItemSelected:"AXMenuItemSelected",
        AnnouncementRequested:"AXAnnouncementRequested",
        base:this.Enumeration.Prototype
    }

    static Role := {
        Application:"AXApplication",
        SystemWide:"AXSystemWide",
        Window:"AXWindow",
        Sheet:"AXSheet",
        Drawer:"AXDrawer",
        Button:"AXButton",
        RadioButton:"AXRadioButton",
        CheckBox:"AXCheckBox",
        PopUpButton:"AXPopUpButton",
        MenuButton:"AXMenuButton",
        TabGroup:"AXTabGroup",
        TabButton:"AXTabButton",
        Table:"AXTable",
        Column:"AXColumn",
        Row:"AXRow",
        Outline:"AXOutline",
        Browser:"AXBrowser",
        ScrollArea:"AXScrollArea",
        ScrollBar:"AXScrollBar",
        RadioGroup:"AXRadioGroup",
        List:"AXList",
        Group:"AXGroup",
        ValueIndicator:"AXValueIndicator",
        ComboBox:"AXComboBox",
        Slider:"AXSlider",
        Incrementor:"AXIncrementor",
        BusyIndicator:"AXBusyIndicator",
        ProgressIndicator:"AXProgressIndicator",
        RelevanceIndicator:"AXRelevanceIndicator",
        LevelIndicator:"AXLevelIndicator",
        Toolbar:"AXToolbar",
        DisclosureTriangle:"AXDisclosureTriangle",
        TextField:"AXTextField",
        TextArea:"AXTextArea",
        StaticText:"AXStaticText",
        Heading:"AXHeading",
        Link:"AXLink",
        MenuBar:"AXMenuBar",
        MenuBarItem:"AXMenuBarItem",
        Menu:"AXMenu",
        MenuItem:"AXMenuItem",
        SplitGroup:"AXSplitGroup",
        Splitter:"AXSplitter",
        ColorWell:"AXColorWell",
        TimeField:"AXTimeField",
        DateField:"AXDateField",
        Image:"AXImage",
        GrowArea:"AXGrowArea",
        HelpTag:"AXHelpTag",
        Matte:"AXMatte",
        Ruler:"AXRuler",
        RulerMarker:"AXRulerMarker",
        LayoutArea:"AXLayoutArea",
        LayoutItem:"AXLayoutItem",
        Handle:"AXHandle",
        Cell:"AXCell",
        Grid:"AXGrid",
        WebArea:"AXWebArea",
        ScrollView:"AXScrollView",
        Popover:"AXPopover",
        Unknown:"AXUnknown",
        base:this.Enumeration.Prototype
    }

    static Subrole := {
        CloseButton:"AXCloseButton",
        ZoomButton:"AXZoomButton",
        MinimizeButton:"AXMinimizeButton",
        ToolbarButton:"AXToolbarButton",
        FullScreenButton:"AXFullScreenButton",
        SecureTextField:"AXSecureTextField",
        SearchField:"AXSearchField",
        TableRow:"AXTableRow",
        OutlineRow:"AXOutlineRow",
        StandardWindow:"AXStandardWindow",
        Dialog:"AXDialog",
        SystemDialog:"AXSystemDialog",
        FloatingWindow:"AXFloatingWindow",
        SystemFloatingWindow:"AXSystemFloatingWindow",
        IncrementArrow:"AXIncrementArrow",
        DecrementArrow:"AXDecrementArrow",
        IncrementPage:"AXIncrementPage",
        DecrementPage:"AXDecrementPage",
        SortButton:"AXSortButton",
        TextAttachment:"AXTextAttachment",
        TextLink:"AXTextLink",
        Timeline:"AXTimeline",
        RatingIndicator:"AXRatingIndicator",
        ContentList:"AXContentList",
        DefinitionList:"AXDefinitionList",
        DescriptionList:"AXDescriptionList",
        Toggle:"AXToggle",
        Switch:"AXSwitch",
        ApplicationDockItem:"AXApplicationDockItem",
        DocumentDockItem:"AXDocumentDockItem",
        FolderDockItem:"AXFolderDockItem",
        MinimizedWindowDockItem:"AXMinimizedWindowDockItem",
        URLDockItem:"AXURLDockItem",
        DockExtraDockItem:"AXDockExtraDockItem",
        TrashDockItem:"AXTrashDockItem",
        SeparatorDockItem:"AXSeparatorDockItem",
        ProcessSwitcherList:"AXProcessSwitcherList",
        LandmarkApplication:"AXLandmarkApplication",
        LandmarkBanner:"AXLandmarkBanner",
        LandmarkComplementary:"AXLandmarkComplementary",
        LandmarkContentInfo:"AXLandmarkContentInfo",
        LandmarkMain:"AXLandmarkMain",
        LandmarkNavigation:"AXLandmarkNavigation",
        LandmarkRegion:"AXLandmarkRegion",
        LandmarkSearch:"AXLandmarkSearch",
        Unknown:"AXUnknown",
        base:this.Enumeration.Prototype
    }

    static Orientation := {
        Unknown:"AXUnknownOrientation",
        Vertical:"AXVerticalOrientation",
        Horizontal:"AXHorizontalOrientation",
        base:this.Enumeration.Prototype
    }

    static SortDirection := {
        Unknown:"AXUnknownSortDirection",
        Ascending:"AXAscendingSortDirection",
        Descending:"AXDescendingSortDirection",
        base:this.Enumeration.Prototype
    }

    static RulerUnit := {
        Unknown:"AXUnknownUnits",
        Inches:"AXInches",
        Centimeters:"AXCentimeters",
        Points:"AXPoints",
        Picas:"AXPicas",
        base:this.Enumeration.Prototype
    }

    static MarkerType := {
        Unknown:"AXUnknownMarker",
        TabStop:"AXTabStopMarker",
        LeftTabStop:"AXLeftTabStopMarker",
        RightTabStop:"AXRightTabStopMarker",
        CenterTabStop:"AXCenterTabStopMarker",
        DecimalTabStop:"AXDecimalTabStopMarker",
        HeadIndent:"AXHeadIndentMarker",
        TailIndent:"AXTailIndentMarker",
        FirstLineIndent:"AXFirstLineIndentMarker",
        base:this.Enumeration.Prototype
    }

    static MenuItemModifier := {
        None:0,
        Shift:1 << 0,
        Option:1 << 1,
        Control:1 << 2,
        NoCommand:1 << 3,
        base:this.Enumeration.Prototype
    }

    static CopyMultipleAttributeOptions := {
        None:0,
        StopOnError:1,
        base:this.Enumeration.Prototype
    }

    static __CFStrCache := Map()
    static __TypeIdCache := Map()
    static __DlopenCache := Map()
    static __DataSymbolCache := Map()
    static __HighlightGuis := Map()
    static __Observers := Map()
    static __ObserverId := 0
    static __ObserverCallbackPtr := 0

    static __Sym(lib, sym) => lib . this.SymDelim . sym

    static __Dlopen(lib) {
        if this.__DlopenCache.Has(lib)
            return this.__DlopenCache[lib]
        h := DllCall(this.__Sym(this.LibSystem, "dlopen"), "AStr", lib, "Int", 0x2, "Ptr") ; RTLD_NOW
        if !h
            h := DllCall(this.__Sym(this.LibSystem, "dlopen"), "Ptr", 0, "Int", 0x2, "Ptr")
        this.__DlopenCache[lib] := h
        return h
    }

    static __DataSymbol(lib, name, dereference := true) {
        key := lib "|" name "|" dereference
        if this.__DataSymbolCache.Has(key)
            return this.__DataSymbolCache[key]
        h := this.__Dlopen(lib)
        pSym := h ? DllCall(this.__Sym(this.LibSystem, "dlsym"), "Ptr", h, "AStr", name, "Ptr") : 0
        value := pSym ? (dereference ? NumGet(pSym, 0, "Ptr") : pSym) : 0
        this.__DataSymbolCache[key] := value
        return value
    }

    static __CFTrue() {
        p := this.__DataSymbol(this.LibCF, "kCFBooleanTrue", true)
        if !p
            p := this.__DataSymbol(this.LibCF, "__kCFBooleanTrue", true)
        return p
    }

    static __CFFalse() {
        p := this.__DataSymbol(this.LibCF, "kCFBooleanFalse", true)
        if !p
            p := this.__DataSymbol(this.LibCF, "__kCFBooleanFalse", true)
        return p
    }

    static __CFNull() {
        p := this.__DataSymbol(this.LibCF, "kCFNull", true)
        if !p
            p := this.__DataSymbol(this.LibCF, "__kCFNull", true)
        return p
    }

    static __CFTypeArrayCallBacks() => this.__DataSymbol(this.LibCF, "kCFTypeArrayCallBacks", false)
    static __CFTypeDictionaryKeyCallBacks() => this.__DataSymbol(this.LibCF, "kCFTypeDictionaryKeyCallBacks", false)
    static __CFTypeDictionaryValueCallBacks() => this.__DataSymbol(this.LibCF, "kCFTypeDictionaryValueCallBacks", false)

    static __CFBoolean(value) => value ? this.__CFTrue() : this.__CFFalse()

    static __CFRunLoopCommonModes() {
        p := this.__DataSymbol(this.LibCF, "kCFRunLoopCommonModes", true)
        return p ? p : this.__CFStr("kCFRunLoopCommonModes")
    }

    static __CFStr(str) {
        if this.__CFStrCache.Has(str)
            return this.__CFStrCache[str]
        cb := StrPut(str, "UTF-8")
        buf := Buffer(cb, 0)
        StrPut(str, buf, "UTF-8")
        p := DllCall(this.__Sym(this.LibCF, "CFStringCreateWithCString"), "Ptr", 0, "Ptr", buf.Ptr, "UInt", this.kCFStringEncodingUTF8, "Ptr")
        if !p
            throw Error("CFStringCreateWithCString failed for " str)
        this.__CFStrCache[str] := p
        return p
    }

    static __NewCFString(str) {
        cb := StrPut(str, "UTF-8")
        buf := Buffer(cb, 0)
        StrPut(str, buf, "UTF-8")
        return DllCall(this.__Sym(this.LibCF, "CFStringCreateWithCString"), "Ptr", 0, "Ptr", buf.Ptr, "UInt", this.kCFStringEncodingUTF8, "Ptr")
    }

    static __Retain(pObj) => pObj ? DllCall(this.__Sym(this.LibCF, "CFRetain"), "Ptr", pObj, "Ptr") : 0
    static __Release(pObj) => pObj ? DllCall(this.__Sym(this.LibCF, "CFRelease"), "Ptr", pObj) : 0

    static __TypeId(name) {
        if this.__TypeIdCache.Has(name)
            return this.__TypeIdCache[name]
        sym := ""
        switch name {
            case "String": sym := "CFStringGetTypeID"
            case "Number": sym := "CFNumberGetTypeID"
            case "Boolean": sym := "CFBooleanGetTypeID"
            case "Array": sym := "CFArrayGetTypeID"
            case "Dictionary": sym := "CFDictionaryGetTypeID"
            case "Data": sym := "CFDataGetTypeID"
            case "URL": sym := "CFURLGetTypeID"
            case "AttributedString": sym := "CFAttributedStringGetTypeID"
            case "Null": sym := "CFNullGetTypeID"
            case "AXUIElement": sym := "AXUIElementGetTypeID"
            case "AXValue": sym := "AXValueGetTypeID"
        }
        tid := DllCall(this.__Sym((name ~= "^AX") ? this.LibAX : this.LibCF, sym), "Ptr")
        this.__TypeIdCache[name] := tid
        return tid
    }

    static __CFType(pObj) => pObj ? DllCall(this.__Sym(this.LibCF, "CFGetTypeID"), "Ptr", pObj, "Ptr") : 0

    static __CFStringToStr(pStr, release := false) {
        if !pStr
            return ""
        pUtf8 := DllCall(this.__Sym(this.LibCF, "CFStringGetCStringPtr"), "Ptr", pStr, "UInt", this.kCFStringEncodingUTF8, "Ptr")
        if pUtf8
            s := StrGet(pUtf8, "UTF-8")
        else {
            len := DllCall(this.__Sym(this.LibCF, "CFStringGetLength"), "Ptr", pStr, "Ptr")
            max := DllCall(this.__Sym(this.LibCF, "CFStringGetMaximumSizeForEncoding"), "Ptr", len, "UInt", this.kCFStringEncodingUTF8, "Ptr") + 1
            buf := Buffer(max, 0)
            ok := DllCall(this.__Sym(this.LibCF, "CFStringGetCString"), "Ptr", pStr, "Ptr", buf.Ptr, "Ptr", max, "UInt", this.kCFStringEncodingUTF8, "Int")
            s := ok ? StrGet(buf, "UTF-8") : ""
        }
        if release
            this.__Release(pStr)
        return s
    }

    static __CFNumberToValue(pNum, release := false) {
        if !pNum
            return 0
        d := 0.0
        ok := DllCall(this.__Sym(this.LibCF, "CFNumberGetValue"), "Ptr", pNum, "Int", this.kCFNumberDoubleType, "Double*", &d, "Int")
        if !ok {
            i64 := 0
            ok := DllCall(this.__Sym(this.LibCF, "CFNumberGetValue"), "Ptr", pNum, "Int", this.kCFNumberSInt64Type, "Int64*", &i64, "Int")
            val := ok ? i64 : 0
        } else {
            ri := Round(d)
            val := (Abs(d - ri) < 0.0000001) ? ri : d
        }
        if release
            this.__Release(pNum)
        return val
    }

    static __CFNumberCreate(value, numberType := unset) {
        if !IsSet(numberType)
            numberType := (value is Float) ? this.kCFNumberDoubleType : this.kCFNumberSInt64Type
        if (numberType = this.kCFNumberDoubleType || numberType = this.kCFNumberCGFloatType) {
            buf := Buffer(8, 0)
            NumPut("Double", value + 0.0, buf, 0)
            return DllCall(this.__Sym(this.LibCF, "CFNumberCreate"), "Ptr", 0, "Int", numberType, "Ptr", buf.Ptr, "Ptr")
        } else {
            buf := Buffer(8, 0)
            NumPut("Int64", Integer(value), buf, 0)
            return DllCall(this.__Sym(this.LibCF, "CFNumberCreate"), "Ptr", 0, "Int", numberType, "Ptr", buf.Ptr, "Ptr")
        }
    }

    static __CFURLToStr(pURL, release := false) {
        if !pURL
            return ""
        pStr := DllCall(this.__Sym(this.LibCF, "CFURLGetString"), "Ptr", pURL, "Ptr")
        s := pStr ? this.__CFStringToStr(pStr, false) : ""
        if release
            this.__Release(pURL)
        return s
    }

    static __CFAttributedStringToStr(pAttrStr, release := false) {
        if !pAttrStr
            return ""
        pStr := DllCall(this.__Sym(this.LibCF, "CFAttributedStringGetString"), "Ptr", pAttrStr, "Ptr")
        s := pStr ? this.__CFStringToStr(pStr, false) : ""
        if release
            this.__Release(pAttrStr)
        return s
    }

    static __CFDataToBuffer(pData, release := false) {
        if !pData
            return Buffer(0)
        len := DllCall(this.__Sym(this.LibCF, "CFDataGetLength"), "Ptr", pData, "Ptr")
        pBytes := DllCall(this.__Sym(this.LibCF, "CFDataGetBytePtr"), "Ptr", pData, "Ptr")
        out := Buffer(len, 0)
        if pBytes && len
            DllCall(this.__Sym(this.LibSystem, "memmove"), "Ptr", out.Ptr, "Ptr", pBytes, "Ptr", len, "Ptr")
        if release
            this.__Release(pData)
        return out
    }

    static __CFArrayCreate(values) {
        refs := [], ptrs := Buffer(values.Length * A_PtrSize, 0)
        for i, value in values {
            p := this.__AnyToCF(value)
            refs.Push(p)
            NumPut("Ptr", p, ptrs, (i - 1) * A_PtrSize)
        }
        pArr := DllCall(this.__Sym(this.LibCF, "CFArrayCreate"), "Ptr", 0, "Ptr", ptrs.Ptr, "Ptr", values.Length, "Ptr", this.__CFTypeArrayCallBacks(), "Ptr")
        for _, p in refs
            this.__Release(p)
        return pArr
    }

    static __CFDictionaryCreate(mapOrObj) {
        keys := [], vals := []
        if mapOrObj is Map {
            for k, v in mapOrObj
                keys.Push(k), vals.Push(v)
        } else {
            for k, v in mapOrObj.OwnProps()
                keys.Push(k), vals.Push(v)
        }
        keyPtrs := Buffer(keys.Length * A_PtrSize, 0), valPtrs := Buffer(vals.Length * A_PtrSize, 0)
        keyRefs := [], valRefs := []
        for i, k in keys {
            pk := this.__NewCFString(k ""), pv := this.__AnyToCF(vals[i])
            keyRefs.Push(pk), valRefs.Push(pv)
            NumPut("Ptr", pk, keyPtrs, (i - 1) * A_PtrSize)
            NumPut("Ptr", pv, valPtrs, (i - 1) * A_PtrSize)
        }
        pDict := DllCall(this.__Sym(this.LibCF, "CFDictionaryCreate"), "Ptr", 0, "Ptr", keyPtrs.Ptr, "Ptr", valPtrs.Ptr, "Ptr", keys.Length, "Ptr", this.__CFTypeDictionaryKeyCallBacks(), "Ptr", this.__CFTypeDictionaryValueCallBacks(), "Ptr")
        for _, p in keyRefs
            this.__Release(p)
        for _, p in valRefs
            this.__Release(p)
        return pDict
    }

    static __AnyToCF(value, attr := "") {
        if IsObject(value) {
            if value is Array
                return this.__CFArrayCreate(value)
            if value is Map
                return this.__CFDictionaryCreate(value)
            if value.HasOwnProp("Ptr")
                return this.__Retain(value.Ptr)
            if value.HasOwnProp("location") || value.HasOwnProp("start")
                return this.__AXValueCreate(this.kAXValueCFRangeType, value)
            if value.HasOwnProp("x") && value.HasOwnProp("y") && (value.HasOwnProp("w") || value.HasOwnProp("width") || value.HasOwnProp("h") || value.HasOwnProp("height"))
                return this.__AXValueCreate(this.kAXValueCGRectType, value)
            if value.HasOwnProp("x") && value.HasOwnProp("y")
                return this.__AXValueCreate(this.kAXValueCGPointType, value)
            if value.HasOwnProp("w") || value.HasOwnProp("width") || value.HasOwnProp("h") || value.HasOwnProp("height")
                return this.__AXValueCreate(this.kAXValueCGSizeType, value)
            return this.__CFDictionaryCreate(value)
        }
        if value is String
            return this.__NewCFString(value)
        if value is Float
            return this.__CFNumberCreate(value, this.kCFNumberDoubleType)
        if value is Integer {
            if attr && attr ~= "i)(Focused|Enabled|Selected|Main|Minimized|Frontmost|Hidden|Visible|Expanded|Modal|Edited|ElementBusy|AlternateUIVisible|Disclosing|OrderedByRow|IsEditable|Required|Invalid|Loaded|Visited)$"
                return this.__Retain(this.__CFBoolean(!!value))
            return this.__CFNumberCreate(value)
        }
        throw TypeError("Unsupported value type for CoreFoundation conversion", -2)
    }

    static __CFIndexGet(buf, offset := 0) => A_PtrSize = 8 ? NumGet(buf, offset, "Int64") : NumGet(buf, offset, "Int")
    static __CFIndexPut(value, buf, offset := 0) => A_PtrSize = 8 ? NumPut("Int64", Integer(value), buf, offset) : NumPut("Int", Integer(value), buf, offset)

    static __AXValueToObject(pVal, release := false) {
        vtype := DllCall(this.__Sym(this.LibAX, "AXValueGetType"), "Ptr", pVal, "Int")
        switch vtype {
            case this.kAXValueCGPointType:
                buf := Buffer(16, 0)
                DllCall(this.__Sym(this.LibAX, "AXValueGetValue"), "Ptr", pVal, "Int", vtype, "Ptr", buf.Ptr, "Int")
                out := {x:NumGet(buf, 0, "Double"), y:NumGet(buf, 8, "Double")}
            case this.kAXValueCGSizeType:
                buf := Buffer(16, 0)
                DllCall(this.__Sym(this.LibAX, "AXValueGetValue"), "Ptr", pVal, "Int", vtype, "Ptr", buf.Ptr, "Int")
                out := {w:NumGet(buf, 0, "Double"), h:NumGet(buf, 8, "Double")}
            case this.kAXValueCGRectType:
                buf := Buffer(32, 0)
                DllCall(this.__Sym(this.LibAX, "AXValueGetValue"), "Ptr", pVal, "Int", vtype, "Ptr", buf.Ptr, "Int")
                out := {x:NumGet(buf, 0, "Double"), y:NumGet(buf, 8, "Double"), w:NumGet(buf, 16, "Double"), h:NumGet(buf, 24, "Double")}
            case this.kAXValueCFRangeType:
                buf := Buffer(A_PtrSize * 2, 0)
                DllCall(this.__Sym(this.LibAX, "AXValueGetValue"), "Ptr", pVal, "Int", vtype, "Ptr", buf.Ptr, "Int")
                loc := this.__CFIndexGet(buf, 0), len := this.__CFIndexGet(buf, A_PtrSize)
                out := {location:loc, length:len, start:loc, end:loc + len}
            case this.kAXValueAXErrorType:
                buf := Buffer(4, 0)
                DllCall(this.__Sym(this.LibAX, "AXValueGetValue"), "Ptr", pVal, "Int", vtype, "Ptr", buf.Ptr, "Int")
                out := NumGet(buf, 0, "Int")
            default:
                out := ""
        }
        if release
            this.__Release(pVal)
        return out
    }

    static __AXValueCreate(valueType, value) {
        switch valueType {
            case this.kAXValueCGPointType:
                buf := Buffer(16, 0)
                NumPut("Double", value.x, buf, 0)
                NumPut("Double", value.y, buf, 8)
            case this.kAXValueCGSizeType:
                buf := Buffer(16, 0)
                NumPut("Double", value.HasOwnProp("w") ? value.w : value.width, buf, 0)
                NumPut("Double", value.HasOwnProp("h") ? value.h : value.height, buf, 8)
            case this.kAXValueCGRectType:
                buf := Buffer(32, 0)
                NumPut("Double", value.x, buf, 0)
                NumPut("Double", value.y, buf, 8)
                NumPut("Double", value.HasOwnProp("w") ? value.w : value.width, buf, 16)
                NumPut("Double", value.HasOwnProp("h") ? value.h : value.height, buf, 24)
            case this.kAXValueCFRangeType:
                buf := Buffer(A_PtrSize * 2, 0)
                loc := value.HasOwnProp("location") ? value.location : value.start
                len := value.HasOwnProp("length") ? value.length : value.end - loc
                this.__CFIndexPut(loc, buf, 0)
                this.__CFIndexPut(len, buf, A_PtrSize)
            default:
                throw Error("Unsupported AXValue type " valueType)
        }
        return DllCall(this.__Sym(this.LibAX, "AXValueCreate"), "Int", valueType, "Ptr", buf.Ptr, "Ptr")
    }

    static __CFArrayToArray(pArray, release := false) {
        arr := []
        if pArray {
            count := DllCall(this.__Sym(this.LibCF, "CFArrayGetCount"), "Ptr", pArray, "Ptr")
            Loop count {
                pItem := DllCall(this.__Sym(this.LibCF, "CFArrayGetValueAtIndex"), "Ptr", pArray, "Ptr", A_Index - 1, "Ptr")
                arr.Push(this.__CFToValue(pItem, false))
            }
        }
        if release
            this.__Release(pArray)
        return arr
    }

    static __CFDictionaryGetRaw(pDict, key) {
        return pDict ? DllCall(this.__Sym(this.LibCF, "CFDictionaryGetValue"), "Ptr", pDict, "Ptr", this.__CFStr(key), "Ptr") : 0
    }

    static __CFDictionaryGet(pDict, key, default := unset) {
        p := this.__CFDictionaryGetRaw(pDict, key)
        if !p
            return IsSet(default) ? default : ""
        return this.__CFToValue(p, false)
    }

    static __CFDictionaryToMap(pDict, release := false) {
        m := Map()
        if pDict {
            count := DllCall(this.__Sym(this.LibCF, "CFDictionaryGetCount"), "Ptr", pDict, "Ptr")
            keys := Buffer(count * A_PtrSize, 0), vals := Buffer(count * A_PtrSize, 0)
            DllCall(this.__Sym(this.LibCF, "CFDictionaryGetKeysAndValues"), "Ptr", pDict, "Ptr", keys.Ptr, "Ptr", vals.Ptr)
            Loop count {
                k := this.__CFToValue(NumGet(keys, (A_Index - 1) * A_PtrSize, "Ptr"), false)
                v := this.__CFToValue(NumGet(vals, (A_Index - 1) * A_PtrSize, "Ptr"), false)
                m[k] := v
            }
        }
        if release
            this.__Release(pDict)
        return m
    }

    static __CFToValue(pObj, release := false) {
        if !pObj
            return ""
        tid := this.__CFType(pObj)
        if tid = this.__TypeId("String")
            return this.__CFStringToStr(pObj, release)
        if tid = this.__TypeId("Number")
            return this.__CFNumberToValue(pObj, release)
        if tid = this.__TypeId("Boolean") {
            v := !!DllCall(this.__Sym(this.LibCF, "CFBooleanGetValue"), "Ptr", pObj, "Int")
            if release
                this.__Release(pObj)
            return v
        }
        if tid = this.__TypeId("Array")
            return this.__CFArrayToArray(pObj, release)
        if tid = this.__TypeId("Dictionary")
            return this.__CFDictionaryToMap(pObj, release)
        if tid = this.__TypeId("Data")
            return this.__CFDataToBuffer(pObj, release)
        if tid = this.__TypeId("URL")
            return this.__CFURLToStr(pObj, release)
        if tid = this.__TypeId("AttributedString")
            return this.__CFAttributedStringToStr(pObj, release)
        if tid = this.__TypeId("Null") {
            if release
                this.__Release(pObj)
            return ""
        }
        if tid = this.__TypeId("AXValue")
            return this.__AXValueToObject(pObj, release)
        if tid = this.__TypeId("AXUIElement") {
            pOwned := release ? pObj : this.__Retain(pObj)
            return this.Element(pOwned)
        }
        if release
            this.__Release(pObj)
        return pObj
    }

    static __NormalizeAttr(attr) => (SubStr(attr, 1, 2) = "AX") ? attr : (this.Attribute.HasOwnProp(attr) ? this.Attribute.%attr% : "AX" attr)
    static __NormalizeParamAttr(attr) => (SubStr(attr, 1, 2) = "AX") ? attr : (this.ParameterizedAttribute.HasOwnProp(attr) ? this.ParameterizedAttribute.%attr% : "AX" attr)
    static __NormalizeAction(action) => (SubStr(action, 1, 2) = "AX") ? action : (this.Action.HasOwnProp(action) ? this.Action.%action% : "AX" action)
    static __NormalizeNotification(notification) => (SubStr(notification, 1, 2) = "AX") ? notification : (this.Notification.HasOwnProp(notification) ? this.Notification.%notification% : "AX" notification)

    static __ErrorName(err) {
        try return this.Error[err]
        return "AXError(" err ")"
    }

    static __ThrowAX(err, what := "Accessibility call") {
        if err
            throw Error(what " failed: " this.__ErrorName(err), -2, err)
    }

    static __CopyAttributeValue(pElement, attr, throwOnError := false) {
        pValue := 0
        err := DllCall(this.__Sym(this.LibAX, "AXUIElementCopyAttributeValue"), "Ptr", pElement, "Ptr", this.__CFStr(this.__NormalizeAttr(attr)), "Ptr*", &pValue, "Int")
        if err {
            if throwOnError
                this.__ThrowAX(err, "AXUIElementCopyAttributeValue(" attr ")")
            return 0
        }
        return pValue
    }

    static __CopyAttributeValues(pElement, attr, start := 0, maxValues := 0) {
        attr := this.__NormalizeAttr(attr)
        if !maxValues {
            count := 0
            err := DllCall(this.__Sym(this.LibAX, "AXUIElementGetAttributeValueCount"), "Ptr", pElement, "Ptr", this.__CFStr(attr), "Ptr*", &count, "Int")
            if err
                return []
            maxValues := count
        }
        pValues := 0
        err := DllCall(this.__Sym(this.LibAX, "AXUIElementCopyAttributeValues"), "Ptr", pElement, "Ptr", this.__CFStr(attr), "Ptr", start, "Ptr", maxValues, "Ptr*", &pValues, "Int")
        return err ? [] : this.__CFArrayToArray(pValues, true)
    }

    static __CopyMultipleAttributeValues(pElement, attrs, options := 0) {
        if attrs is String
            attrs := [attrs]
        norm := []
        for _, attr in attrs
            norm.Push(this.__NormalizeAttr(attr))
        pAttrArray := this.__CFArrayCreate(norm)
        pValues := 0
        err := DllCall(this.__Sym(this.LibAX, "AXUIElementCopyMultipleAttributeValues"), "Ptr", pElement, "Ptr", pAttrArray, "UInt", options, "Ptr*", &pValues, "Int")
        this.__Release(pAttrArray)
        if err
            return Map()
        vals := this.__CFArrayToArray(pValues, true)
        out := Map()
        for i, attr in norm
            out[attr] := (i <= vals.Length) ? vals[i] : ""
        return out
    }

    static __SetAttributeValue(pElement, attr, pValue, releaseValue := false) {
        err := DllCall(this.__Sym(this.LibAX, "AXUIElementSetAttributeValue"), "Ptr", pElement, "Ptr", this.__CFStr(this.__NormalizeAttr(attr)), "Ptr", pValue, "Int")
        if releaseValue
            this.__Release(pValue)
        return err
    }

    static __ValueToCF(attr, value) {
        attr := this.__NormalizeAttr(attr)
        if IsObject(value) {
            if attr = "AXPosition"
                return this.__AXValueCreate(this.kAXValueCGPointType, value)
            if attr = "AXSize"
                return this.__AXValueCreate(this.kAXValueCGSizeType, value)
            if attr = "AXFrame"
                return this.__AXValueCreate(this.kAXValueCGRectType, value)
            if (attr = "AXSelectedTextRange" || attr ~= "Range$") && !(value is Array)
                return this.__AXValueCreate(this.kAXValueCFRangeType, value)
        }
        return this.__AnyToCF(value, attr)
    }

    static SetMessagingTimeout(elementOrSeconds, seconds := unset) {
        if IsSet(seconds) {
            pElement := (elementOrSeconds is AxSpy.Element) ? elementOrSeconds.Ptr : elementOrSeconds
            timeout := seconds + 0.0
        } else {
            root := this.GetRootElement()
            pElement := root.Ptr
            timeout := elementOrSeconds + 0.0
        }
        err := DllCall(this.__Sym(this.LibAX, "AXUIElementSetMessagingTimeout"), "Ptr", pElement, "Float", timeout, "Int")
        this.__ThrowAX(err, "AXUIElementSetMessagingTimeout")
        return true
    }

    static PostKeyboardEvent(charCode := 0, virtualKey := 0, keyDown := true, appOrElement := unset) {
        local pElement, root
        if IsSet(appOrElement)
            pElement := (appOrElement is AxSpy.Element) ? appOrElement.Ptr : appOrElement
        else {
            root := this.GetRootElement()
            pElement := root.Ptr
        }
        err := DllCall(this.__Sym(this.LibAX, "AXUIElementPostKeyboardEvent"), "Ptr", pElement, "UShort", charCode, "UShort", virtualKey, "Int", !!keyDown, "Int")
        this.__ThrowAX(err, "AXUIElementPostKeyboardEvent")
        return true
    }

    static GetRootElement() {
        p := DllCall(this.__Sym(this.LibAX, "AXUIElementCreateSystemWide"), "Ptr")
        if !p
            throw Error("AXUIElementCreateSystemWide failed")
        return this.Element(p, "SystemWide")
    }

    static ElementFromPoint(x := unset, y := unset) {
        if !IsSet(x) || !IsSet(y)
            MouseGetPos(&x, &y)
        pRoot := DllCall(this.__Sym(this.LibAX, "AXUIElementCreateSystemWide"), "Ptr")
        if !pRoot
            throw Error("AXUIElementCreateSystemWide failed")
        pElement := 0
        err := DllCall(this.__Sym(this.LibAX, "AXUIElementCopyElementAtPosition"), "Ptr", pRoot, "Float", x, "Float", y, "Ptr*", &pElement, "Int")
        this.__Release(pRoot)
        this.__ThrowAX(err, "AXUIElementCopyElementAtPosition")
        return this.Element(pElement)
    }
    static ObjectFromPoint(args*) => this.ElementFromPoint(args*)

    static ElementFromPid(pid) {
        p := DllCall(this.__Sym(this.LibAX, "AXUIElementCreateApplication"), "Int", pid, "Ptr")
        if !p
            throw Error("AXUIElementCreateApplication failed for pid " pid)
        return this.Element(p, "Application")
    }
    static ObjectFromPid(args*) => this.ElementFromPid(args*)

    static GetFocusedElement() {
        root := this.GetRootElement()
        return root.Attribute("AXFocusedUIElement")
    }

    static GetFocusedWindow() {
        root := this.GetRootElement()
        try return root.Attribute("AXFocusedWindow")
        catch {
            try return root.Attribute("AXFocusedApplication").Attribute("AXFocusedWindow")
        }
    }

    static GetFocusedApplication() {
        root := this.GetRootElement()
        return root.Attribute("AXFocusedApplication")
    }

    static Applications() {
        seen := Map(), apps := []
        for _, w in this.WindowList() {
            pid := w.OwnerPID
            if pid && !seen.Has(pid) {
                seen[pid] := true
                try apps.Push(this.ElementFromPid(pid))
            }
        }
        return apps
    }

    static ElementFromApp(nameOrPid := "") {
        if IsInteger(nameOrPid)
            return this.ElementFromPid(nameOrPid)
        if (nameOrPid = "") {
            try return this.GetFocusedApplication()
            catch
                return this.Applications()[1]
        }
        for _, w in this.WindowList() {
            if this.__MatchText(w.OwnerName, nameOrPid, this.MatchMode.Substring, false)
                return this.ElementFromPid(w.OwnerPID)
        }
        throw TargetError("Application not found: " nameOrPid, -1)
    }

    static ElementFromWindowTitle(title := "", matchMode := "Substring", caseSensitive := false) {
        if (title = "") {
            try return this.GetFocusedWindow()
            catch {
                wins := this.WindowList()
                if wins.Length
                    return this.__AXWindowFromWindowInfo(wins[1])
            }
        }
        mm := IsInteger(matchMode) ? matchMode : this.MatchMode.%matchMode%
        for _, w in this.WindowList() {
            if this.__MatchText(w.Name, title, mm, caseSensitive) || this.__MatchText(w.OwnerName, title, mm, caseSensitive) {
                try return this.__AXWindowFromWindowInfo(w, title, mm, caseSensitive)
                catch
                    return this.ElementFromPid(w.OwnerPID)
            }
        }
        throw TargetError("Window not found: " title, -1)
    }
    static ElementFromHandle(args*) => this.ElementFromWindowTitle(args*)
    static ObjectFromWindow(args*) => this.ElementFromWindowTitle(args*)

    static __AXWindowFromWindowInfo(w, title := "", mm := 2, caseSensitive := false) {
        app := this.ElementFromPid(w.OwnerPID)
        for _, axWin in app.Windows {
            if (title = "") || this.__MatchText(axWin.Name, title, mm, caseSensitive) || this.__MatchText(axWin.Title, title, mm, caseSensitive)
                return axWin
        }
        return app
    }

    static WindowList(options := unset, includeNonLayer0 := false) {
        if !IsSet(options)
            options := this.CGWindowListOptionAll
        pList := DllCall(this.__Sym(this.LibCG, "CGWindowListCopyWindowInfo"), "UInt", options, "UInt", 0, "Ptr")
        out := []
        if !pList
            return out
        count := DllCall(this.__Sym(this.LibCF, "CFArrayGetCount"), "Ptr", pList, "Ptr")
        Loop count {
            pDict := DllCall(this.__Sym(this.LibCF, "CFArrayGetValueAtIndex"), "Ptr", pList, "Ptr", A_Index - 1, "Ptr")
            layer := this.__CFDictionaryGet(pDict, "kCGWindowLayer", 0)
            if !includeNonLayer0 && layer != 0
                continue
            pBounds := this.__CFDictionaryGetRaw(pDict, "kCGWindowBounds")
            bounds := {x:0, y:0, w:0, h:0}
            if pBounds
                bounds := { x:this.__CFDictionaryGet(pBounds, "X", 0)
                          , y:this.__CFDictionaryGet(pBounds, "Y", 0)
                          , w:this.__CFDictionaryGet(pBounds, "Width", 0)
                          , h:this.__CFDictionaryGet(pBounds, "Height", 0) }
            out.Push({ Number:this.__CFDictionaryGet(pDict, "kCGWindowNumber", 0)
                     , OwnerPID:this.__CFDictionaryGet(pDict, "kCGWindowOwnerPID", 0)
                     , OwnerName:this.__CFDictionaryGet(pDict, "kCGWindowOwnerName", "")
                     , Name:this.__CFDictionaryGet(pDict, "kCGWindowName", "")
                     , Layer:layer
                     , IsOnscreen:this.__CFDictionaryGet(pDict, "kCGWindowIsOnscreen", false)
                     , Alpha:this.__CFDictionaryGet(pDict, "kCGWindowAlpha", 0)
                     , Bounds:bounds })
        }
        this.__Release(pList)
        return out
    }

    static ClearAllHighlights() {
        for _, p in this.__HighlightGuis {
            for _, r in p
                try r.Destroy()
        }
        this.__HighlightGuis := Map()
    }

    static Observe(element, notifications, callback) {
        if !(element is AxSpy.Element)
            throw TypeError("Observe expects a AxSpy.Element", -1)
        if notifications is String
            notifications := [notifications]
        if !(notifications is Array)
            throw TypeError("notifications must be a string or array", -1)
        pid := element.Pid
        if !pid
            throw Error("Cannot observe element because its pid is unavailable")
        pObs := 0
        cb := this.__GetObserverCallback()
        err := DllCall(this.__Sym(this.LibAX, "AXObserverCreate"), "Int", pid, "Ptr", cb, "Ptr*", &pObs, "Int")
        this.__ThrowAX(err, "AXObserverCreate")
        id := ++this.__ObserverId
        obs := this.Observer(pObs, element, id, callback)
        this.__Observers[id] := obs
        for _, note in notifications
            obs.Add(note)
        pSource := DllCall(this.__Sym(this.LibAX, "AXObserverGetRunLoopSource"), "Ptr", pObs, "Ptr")
        pLoop := DllCall(this.__Sym(this.LibCF, "CFRunLoopGetCurrent"), "Ptr")
        DllCall(this.__Sym(this.LibCF, "CFRunLoopAddSource"), "Ptr", pLoop, "Ptr", pSource, "Ptr", this.__CFRunLoopCommonModes())
        return obs
    }

    static __GetObserverCallback() {
        if !this.__ObserverCallbackPtr
            this.__ObserverCallbackPtr := CallbackCreate(this.GetMethod("__HandleObserver").Bind(this), "F", 4)
        return this.__ObserverCallbackPtr
    }

    static __HandleObserver(pObserver, pElement, pNotification, refcon) {
        try {
            if !this.__Observers.Has(refcon)
                return
            obs := this.__Observers[refcon]
            note := this.__CFStringToStr(pNotification, false)
            el := this.Element(this.__Retain(pElement))
            obs.Callback.Call(el, note, obs)
        }
    }

    static __MatchText(text, pattern, matchMode := 3, caseSensitive := true) {
        text := text "", pattern := pattern ""
        switch matchMode, 0 {
            case 2:
                return !!InStr(text, pattern, caseSensitive)
            case 1:
                return (caseSensitive && SubStr(text, 1, StrLen(pattern)) == pattern)
                    || (!caseSensitive && SubStr(text, 1, StrLen(pattern)) = pattern)
            case "Regex":
                return text ~= pattern
            default:
                return (caseSensitive && text == pattern) || (!caseSensitive && text = pattern)
        }
    }

    class Observer {
        __ptr := 0
        __element := 0
        __id := 0
        __callback := 0
        __notifications := []

        __New(pObserver, element, id, callback) {
            this.__ptr := pObserver
            this.__element := element
            this.__id := id
            this.__callback := callback
        }

        Ptr => this.__ptr
        Element => this.__element
        Id => this.__id
        Notifications => this.__notifications.Clone()
        Callback => this.__callback

        Add(notification) {
            notification := AxSpy.__NormalizeNotification(notification)
            err := DllCall(AxSpy.__Sym(AxSpy.LibAX, "AXObserverAddNotification"), "Ptr", this.__ptr, "Ptr", this.__element.Ptr, "Ptr", AxSpy.__CFStr(notification), "Ptr", this.__id, "Int")
            if err && err != AxSpy.Error.NotificationAlreadyRegistered
                AxSpy.__ThrowAX(err, "AXObserverAddNotification(" notification ")")
            this.__notifications.Push(notification)
            return this
        }

        Remove(notification) {
            notification := AxSpy.__NormalizeNotification(notification)
            DllCall(AxSpy.__Sym(AxSpy.LibAX, "AXObserverRemoveNotification"), "Ptr", this.__ptr, "Ptr", this.__element.Ptr, "Ptr", AxSpy.__CFStr(notification), "Int")
            for i, v in this.__notifications.Clone()
                if v = notification
                    this.__notifications.RemoveAt(i)
            return this
        }

        __Delete() {
            for _, n in this.__notifications.Clone()
                try this.Remove(n)
            if this.__ptr
                AxSpy.__Release(this.__ptr)
            try AxSpy.__Observers.Delete(this.__id)
        }
    }

    class Element {
        __ptr := 0
        __kind := ""

        __New(pElement, kind := "") {
            if !pElement
                throw Error("Null AXUIElement pointer")
            this.__ptr := pElement
            this.__kind := kind
        }

        __Delete() => this.__ptr ? AxSpy.__Release(this.__ptr) : 0

        __Item[params*] {
            get {
                local el, _, param, arr, path, maybeEl, m, int
                el := this
                for _, param in params {
                    if IsObject(param) {
                        try el := el.FindElement(param, 2)
                        catch
                            el := ""
                    } else if param is Integer {
                        try {
                            arr := el.Children
                            el := arr[param < 0 ? arr.Length + param + 1 : param]
                        } catch
                            el := ""
                    } else if param is String {
                        maybeEl := ""
                        for path in StrSplit(param, "|") {
                            try {
                                if InStr(path, ".") || InStr(path, ",") {
                                    arr := StrSplit(StrReplace(path, ".", ","), ",")
                                    for i, int in arr
                                        arr[i] := IsInteger(int) ? Integer(int) : int
                                    maybeEl := el[arr*]
                                } else if RegexMatch(path, "i)^([a-zA-Z]+) *(\d+)?$", &m:="") {
                                    maybeEl := el.FindElement({RoleName:m[1], i:(m.Count > 1 ? m[2] : 1)}, 2)
                                } else
                                    continue
                                break
                            }
                        }
                        el := maybeEl
                    } else
                        throw TypeError("Invalid item type at index " _, -1)
                    if !el
                        throw IndexError("Invalid index/condition at index " _, -1)
                }
                return el
            }
        }

        Ptr => this.__ptr
        Kind => this.__kind

        Pid {
            get {
                pid := 0
                err := DllCall(AxSpy.__Sym(AxSpy.LibAX, "AXUIElementGetPid"), "Ptr", this.__ptr, "Int*", &pid, "Int")
                return err ? 0 : pid
            }
        }

        Attribute(attr, default := unset) {
            p := AxSpy.__CopyAttributeValue(this.__ptr, attr, false)
            if !p {
                if IsSet(default)
                    return default
                throw Error("Attribute unavailable: " AxSpy.__NormalizeAttr(attr), -1)
            }
            return AxSpy.__CFToValue(p, true)
        }

        AttributeRaw(attr) {
            p := AxSpy.__CopyAttributeValue(this.__ptr, attr, true)
            return p
        }

        AttributeValues(attr, start := 0, maxValues := 0) => AxSpy.__CopyAttributeValues(this.__ptr, attr, start, maxValues)

        SetAttribute(attr, value) {
            pValue := AxSpy.__ValueToCF(attr, value)
            err := AxSpy.__SetAttributeValue(this.__ptr, attr, pValue, true)
            AxSpy.__ThrowAX(err, "AXUIElementSetAttributeValue(" attr ")")
            return this
        }

        SetBoolAttribute(attr, value := true) {
            err := AxSpy.__SetAttributeValue(this.__ptr, attr, AxSpy.__CFBoolean(!!value), false)
            AxSpy.__ThrowAX(err, "AXUIElementSetAttributeValue(" attr ")")
            return this
        }

        IsAttributeSettable(attr) {
            settable := 0
            err := DllCall(AxSpy.__Sym(AxSpy.LibAX, "AXUIElementIsAttributeSettable"), "Ptr", this.__ptr, "Ptr", AxSpy.__CFStr(AxSpy.__NormalizeAttr(attr)), "Int*", &settable, "Int")
            return err ? false : !!settable
        }

        Attributes {
            get {
                pNames := 0
                err := DllCall(AxSpy.__Sym(AxSpy.LibAX, "AXUIElementCopyAttributeNames"), "Ptr", this.__ptr, "Ptr*", &pNames, "Int")
                return err ? [] : AxSpy.__CFArrayToArray(pNames, true)
            }
        }

        Actions {
            get {
                pNames := 0
                err := DllCall(AxSpy.__Sym(AxSpy.LibAX, "AXUIElementCopyActionNames"), "Ptr", this.__ptr, "Ptr*", &pNames, "Int")
                return err ? [] : AxSpy.__CFArrayToArray(pNames, true)
            }
        }

        ParameterizedAttributes {
            get {
                pNames := 0
                err := DllCall(AxSpy.__Sym(AxSpy.LibAX, "AXUIElementCopyParameterizedAttributeNames"), "Ptr", this.__ptr, "Ptr*", &pNames, "Int")
                return err ? [] : AxSpy.__CFArrayToArray(pNames, true)
            }
        }

        ParameterizedAttributeNames => this.ParameterizedAttributes
        AttributeNames => this.Attributes
        ActionNames => this.Actions

        SettableAttributes {
            get {
                out := []
                for _, attr in this.Attributes {
                    try {
                        if this.IsAttributeSettable(attr)
                            out.Push(attr)
                    }
                }
                return out
            }
        }

        AttributeCount(attr) {
            attr := AxSpy.__NormalizeAttr(attr)
            count := 0
            err := DllCall(AxSpy.__Sym(AxSpy.LibAX, "AXUIElementGetAttributeValueCount"), "Ptr", this.__ptr, "Ptr", AxSpy.__CFStr(attr), "Ptr*", &count, "Int")
            return err ? 0 : count
        }

        MultipleAttributes(attrs := unset, options := 0) {
            if !IsSet(attrs)
                attrs := this.Attributes
            options := IsInteger(options) ? options : AxSpy.CopyMultipleAttributeOptions.%options%
            return AxSpy.__CopyMultipleAttributeValues(this.__ptr, attrs, options)
        }
        AttributeMap(attrs := unset, options := 0) => this.MultipleAttributes(IsSet(attrs) ? attrs : this.Attributes, options)
        AllAttributes => this.AttributeMap()

        ActionDescriptions {
            get {
                out := Map()
                for _, action in this.Actions
                    out[action] := this.ActionDescription(action)
                return out
            }
        }

        SetMessagingTimeout(seconds) => AxSpy.SetMessagingTimeout(this, seconds)
        PostKeyboardEvent(charCode := 0, virtualKey := 0, keyDown := true) => AxSpy.PostKeyboardEvent(charCode, virtualKey, keyDown, this)

        Role => this.Attribute("AXRole", "")
        RoleName => RegExReplace(this.Role, "^AX")
        RoleId => this.Role
        Subrole => this.Attribute("AXSubrole", "")
        RoleDescription => this.Attribute("AXRoleDescription", "")
        Title {
            get => this.Attribute("AXTitle", "")
            set => this.SetAttribute("AXTitle", value)
        }
        Description => this.Attribute("AXDescription", "")
        Help => this.Attribute("AXHelp", "")
        HelpText => this.Help
        Identifier => this.Attribute("AXIdentifier", "")
        URL => this.Attribute("AXURL", "")
        Filename => this.Attribute("AXFilename", "")
        PlaceholderValue => this.Attribute("AXPlaceholderValue", "")
        ValueDescription => this.Attribute("AXValueDescription", "")
        RoleText => this.RoleName
        ProcessId => this.Pid
        AccessibleId => this.Identifier
        Id => this.Identifier ? this.Identifier : Format("0x{:X}", this.Ptr)
        Hidden => !!this.Attribute("AXHidden", false)
        Visible {
            get {
                try {
                    loc := this.Location
                    return !this.Hidden && loc.w > 0 && loc.h > 0
                } catch
                    return !this.Hidden
            }
        }
        DefaultAction {
            get {
                actions := this.Actions
                return actions.Length ? actions[1] : ""
            }
        }
        KeyboardShortcut {
            get {
                key := "", glyph := "", mods := 0
                try key := this.Attribute("AXMenuItemCmdChar", "")
                try glyph := this.Attribute("AXMenuItemCmdGlyph", "")
                try mods := this.Attribute("AXMenuItemCmdModifiers", 0)
                if !(key || glyph || mods)
                    return ""
                parts := []
                if !(mods & AxSpy.MenuItemModifier.NoCommand)
                    parts.Push("Cmd")
                if mods & AxSpy.MenuItemModifier.Shift
                    parts.Push("Shift")
                if mods & AxSpy.MenuItemModifier.Option
                    parts.Push("Option")
                if mods & AxSpy.MenuItemModifier.Control
                    parts.Push("Ctrl")
                parts.Push(key ? key : glyph)
                out := ""
                for _, part in parts
                    out .= (out ? "+" : "") part
                return out
            }
        }
        States {
            get {
                states := []
                for _, p in ["Enabled", "Focused", "Selected", "Main", "Minimized", "Frontmost", "Hidden", "Visible", "Expanded", "Edited", "Modal", "ElementBusy", "AlternateUIVisible", "Disclosing", "OrderedByRow", "IsEditable", "Visited", "Loaded", "Required"] {
                    try {
                        if this.%p%
                            states.Push(p)
                    }
                }
                return states
            }
        }
        State => this.States
        StateText {
            get {
                out := ""
                for _, v in this.States
                    out .= (out ? "," : "") v
                return out
            }
        }
        Interfaces {
            get {
                out := []
                try {
                    if this.Actions.Length
                        out.Push("Action")
                }
                try {
                    if this.Children.Length
                        out.Push("Children")
                }
                try {
                    if this.Windows.Length
                        out.Push("Application")
                }
                try {
                    if this.NumberOfCharacters
                        out.Push("Text")
                }
                try {
                    if this.SelectedTextRange.length >= 0
                        out.Push("TextSelection")
                }
                try {
                    if this.Rows.Length || this.Columns.Length
                        out.Push("Table")
                }
                try {
                    if this.IsAttributeSettable("AXValue")
                        out.Push("EditableValue")
                }
                return out
            }
        }

        Name {
            get {
                for _, attr in ["AXTitle", "AXDescription", "AXValue", "AXHelp", "AXIdentifier"] {
                    try {
                        v := this.Attribute(attr)
                        if !IsObject(v) && v != ""
                            return v ""
                    }
                }
                return this.RoleName
            }
        }

        Value {
            get => this.Attribute("AXValue", "")
            set => this.SetAttribute("AXValue", value)
        }
        MinValue => this.Attribute("AXMinValue", "")
        MaxValue => this.Attribute("AXMaxValue", "")
        ValueIncrement => this.Attribute("AXValueIncrement", "")
        ValueWraps => !!this.Attribute("AXValueWraps", false)
        AllowedValues => this.Attribute("AXAllowedValues", [])
        NumberOfCharacters => this.Attribute("AXNumberOfCharacters", 0)

        Enabled => !!this.Attribute("AXEnabled", false)
        Focused {
            get => !!this.Attribute("AXFocused", false)
            set => this.SetBoolAttribute("AXFocused", value)
        }
        Selected {
            get => !!this.Attribute("AXSelected", false)
            set => this.SetBoolAttribute("AXSelected", value)
        }
        Main {
            get => !!this.Attribute("AXMain", false)
            set => this.SetBoolAttribute("AXMain", value)
        }
        Minimized {
            get => !!this.Attribute("AXMinimized", false)
            set => this.SetBoolAttribute("AXMinimized", value)
        }
        Frontmost {
            get => !!this.Attribute("AXFrontmost", false)
            set => this.SetBoolAttribute("AXFrontmost", value)
        }

        Position {
            get => this.Attribute("AXPosition")
            set => this.SetAttribute("AXPosition", value)
        }
        Size {
            get => this.Attribute("AXSize")
            set => this.SetAttribute("AXSize", value)
        }
        Frame {
            get {
                try return this.Attribute("AXFrame")
                catch {
                    pos := this.Position, sz := this.Size
                    return {x:pos.x, y:pos.y, w:sz.w, h:sz.h}
                }
            }
            set => this.SetAttribute("AXFrame", value)
        }
        RawLocation => this.Location
        Location {
            get {
                try {
                    pos := this.Position, sz := this.Size
                    return {x:Round(pos.x), y:Round(pos.y), w:Round(sz.w), h:Round(sz.h)}
                } catch {
                    try return this.Attribute("AXFrame")
                    catch
                        throw Error("Location unavailable", -1)
                }
            }
            set {
                this.SetLocation(value.x, value.y, value.HasOwnProp("w") ? value.w : value.width, value.HasOwnProp("h") ? value.h : value.height)
            }
        }

        SetLocation(x, y, w := unset, h := unset) {
            this.SetAttribute("AXPosition", {x:x, y:y})
            if IsSet(w) && IsSet(h)
                this.SetAttribute("AXSize", {w:w, h:h})
            return this
        }

        Parent => this.Attribute("AXParent", 0)
        Window => this.Attribute("AXWindow", 0)
        TopLevelUIElement => this.Attribute("AXTopLevelUIElement", 0)
        Application {
            get {
                try return this.Attribute("AXApplication")
                catch {
                    pid := this.Pid
                    return pid ? AxSpy.ElementFromPid(pid) : 0
                }
            }
        }

        Children {
            get {
                try return this.Attribute("AXChildren")
                catch {
                    if this.__kind = "SystemWide"
                        return AxSpy.Applications()
                    for _, attr in ["AXVisibleChildren", "AXChildrenInNavigationOrder"] {
                        try return this.Attribute(attr)
                    }
                    return []
                }
            }
        }
        VisibleChildren => this.Attribute("AXVisibleChildren", [])
        ChildrenInNavigationOrder => this.Attribute("AXChildrenInNavigationOrder", [])
        ChildCount => this.Children.Length
        Rows => this.Attribute("AXRows", [])
        Columns => this.Attribute("AXColumns", [])
        SelectedRows => this.Attribute("AXSelectedRows", [])
        SelectedColumns => this.Attribute("AXSelectedColumns", [])
        SelectedChildren => this.Attribute("AXSelectedChildren", [])
        SelectedCells => this.Attribute("AXSelectedCells", [])
        Windows => this.Attribute("AXWindows", [])
        FocusedWindow => this.Attribute("AXFocusedWindow", 0)
        FocusedUIElement => this.Attribute("AXFocusedUIElement", 0)
        MenuBar => this.Attribute("AXMenuBar", 0)
        ExtrasMenuBar => this.Attribute("AXExtrasMenuBar", 0)

        MainWindow => this.Attribute("AXMainWindow", 0)
        CloseButton => this.Attribute("AXCloseButton", 0)
        ZoomButton => this.Attribute("AXZoomButton", 0)
        MinimizeButton => this.Attribute("AXMinimizeButton", 0)
        ToolbarButton => this.Attribute("AXToolbarButton", 0)
        FullScreenButton => this.Attribute("AXFullScreenButton", 0)
        Proxy => this.Attribute("AXProxy", 0)
        GrowArea => this.Attribute("AXGrowArea", 0)
        Modal => !!this.Attribute("AXModal", false)
        DefaultButton => this.Attribute("AXDefaultButton", 0)
        CancelButton => this.Attribute("AXCancelButton", 0)
        TitleUIElement => this.Attribute("AXTitleUIElement", 0)
        ServesAsTitleForUIElements => this.Attribute("AXServesAsTitleForUIElements", [])
        LinkedUIElements => this.Attribute("AXLinkedUIElements", [])
        SharedFocusElements => this.Attribute("AXSharedFocusElements", [])
        HorizontalScrollBar => this.Attribute("AXHorizontalScrollBar", 0)
        VerticalScrollBar => this.Attribute("AXVerticalScrollBar", 0)
        Orientation => this.Attribute("AXOrientation", "")
        Header => this.Attribute("AXHeader", 0)
        Edited => !!this.Attribute("AXEdited", false)
        ElementBusy => !!this.Attribute("AXElementBusy", false)
        Busy => this.ElementBusy
        Expanded {
            get => !!this.Attribute("AXExpanded", false)
            set => this.SetBoolAttribute("AXExpanded", value)
        }
        AlternateUIVisible => !!this.Attribute("AXAlternateUIVisible", false)
        Tabs => this.Attribute("AXTabs", [])
        OverflowButton => this.Attribute("AXOverflowButton", 0)
        Splitters => this.Attribute("AXSplitters", [])
        Contents => this.Attribute("AXContents", [])
        NextContents => this.Attribute("AXNextContents", [])
        PreviousContents => this.Attribute("AXPreviousContents", [])
        Document => this.Attribute("AXDocument", "")
        Incrementor => this.Attribute("AXIncrementor", 0)
        DecrementButton => this.Attribute("AXDecrementButton", 0)
        IncrementButton => this.Attribute("AXIncrementButton", 0)
        ColumnTitle => this.Attribute("AXColumnTitles", [])
        ColumnTitles => this.Attribute("AXColumnTitles", [])
        LabelUIElements => this.Attribute("AXLabelUIElements", [])
        LabelValue => this.Attribute("AXLabelValue", "")
        ShownMenuUIElement => this.Attribute("AXShownMenuUIElement", 0)
        MenuItemPrimaryUIElement => this.Attribute("AXMenuItemPrimaryUIElement", 0)
        MenuItemCmdChar => this.Attribute("AXMenuItemCmdChar", "")
        MenuItemCmdVirtualKey => this.Attribute("AXMenuItemCmdVirtualKey", "")
        MenuItemCmdGlyph => this.Attribute("AXMenuItemCmdGlyph", "")
        MenuItemCmdModifiers => this.Attribute("AXMenuItemCmdModifiers", 0)
        MenuItemMarkChar => this.Attribute("AXMenuItemMarkChar", "")
        SearchButton => this.Attribute("AXSearchButton", 0)
        ClearButton => this.Attribute("AXClearButton", 0)
        IsApplicationRunning => !!this.Attribute("AXIsApplicationRunning", false)
        VisibleRows => this.Attribute("AXVisibleRows", [])
        VisibleColumns => this.Attribute("AXVisibleColumns", [])
        SortDirection => this.Attribute("AXSortDirection", "")
        Index => this.Attribute("AXIndex", 0)
        Disclosing {
            get => !!this.Attribute("AXDisclosing", false)
            set => this.SetBoolAttribute("AXDisclosing", value)
        }
        DisclosedRows => this.Attribute("AXDisclosedRows", [])
        DisclosedByRow => this.Attribute("AXDisclosedByRow", 0)
        DisclosureLevel => this.Attribute("AXDisclosureLevel", 0)
        RowCount => this.Attribute("AXRowCount", 0)
        ColumnCount => this.Attribute("AXColumnCount", 0)
        OrderedByRow => !!this.Attribute("AXOrderedByRow", false)
        VisibleCells => this.Attribute("AXVisibleCells", [])
        RowHeaderUIElements => this.Attribute("AXRowHeaderUIElements", [])
        ColumnHeaderUIElements => this.Attribute("AXColumnHeaderUIElements", [])
        RowIndexRange => this.Attribute("AXRowIndexRange", {location:0, length:0, start:0, end:0})
        ColumnIndexRange => this.Attribute("AXColumnIndexRange", {location:0, length:0, start:0, end:0})
        WarningValue => this.Attribute("AXWarningValue", "")
        CriticalValue => this.Attribute("AXCriticalValue", "")
        MatteHole => this.Attribute("AXMatteHole", "")
        MatteContentUIElement => this.Attribute("AXMatteContentUIElement", 0)
        MarkerUIElements => this.Attribute("AXMarkerUIElements", [])
        Units => this.Attribute("AXUnits", "")
        UnitDescription => this.Attribute("AXUnitDescription", "")
        MarkerType => this.Attribute("AXMarkerType", "")
        MarkerTypeDescription => this.Attribute("AXMarkerTypeDescription", "")
        HorizontalUnits => this.Attribute("AXHorizontalUnits", "")
        VerticalUnits => this.Attribute("AXVerticalUnits", "")
        HorizontalUnitDescription => this.Attribute("AXHorizontalUnitDescription", "")
        VerticalUnitDescription => this.Attribute("AXVerticalUnitDescription", "")
        Handles => this.Attribute("AXHandles", [])
        Text => this.Attribute("AXText", "")
        VisibleText => this.Attribute("AXVisibleText", "")
        IsEditable => !!this.Attribute("AXIsEditable", false)
        DOMIdentifier => this.Attribute("AXDOMIdentifier", "")
        DOMClassList => this.Attribute("AXDOMClassList", [])
        ARIALandmarkRole => this.Attribute("AXARIALandmarkRole", "")
        ARIACurrent => this.Attribute("AXARIACurrent", "")
        ARIAAtomic => this.Attribute("AXARIAAtomic", "")
        ARIABusy => this.Attribute("AXARIABusy", "")
        ARIARelevant => this.Attribute("AXARIARelevant", "")
        ARIAPosInSet => this.Attribute("AXARIAPosInSet", "")
        ARIASetSize => this.Attribute("AXARIASetSize", "")
        Visited => !!this.Attribute("AXVisited", false)
        Loaded => !!this.Attribute("AXLoaded", false)
        LoadingProgress => this.Attribute("AXLoadingProgress", "")
        Required => !!this.Attribute("AXRequired", false)
        Invalid => this.Attribute("AXInvalid", "")
        Grabbed => this.Attribute("AXGrabbed", "")
        Owns => this.Attribute("AXOwns", [])
        DropsEffects => this.Attribute("AXDropsEffects", [])

        SelectedText {
            get => this.Attribute("AXSelectedText", "")
            set => this.SetAttribute("AXSelectedText", value)
        }
        SelectedTextRange {
            get => this.Attribute("AXSelectedTextRange", {location:0, length:0, start:0, end:0})
            set => this.SetAttribute("AXSelectedTextRange", value)
        }
        VisibleCharacterRange => this.Attribute("AXVisibleCharacterRange", {location:0, length:0, start:0, end:0})
        SelectedTextRanges {
            get => this.Attribute("AXSelectedTextRanges", [])
            set => this.SetAttribute("AXSelectedTextRanges", value)
        }
        SharedTextUIElements => this.Attribute("AXSharedTextUIElements", [])
        SharedCharacterRange => this.Attribute("AXSharedCharacterRange", {location:0, length:0, start:0, end:0})
        InsertionPointLineNumber => this.Attribute("AXInsertionPointLineNumber", 0)

        GetNthChild(index) {
            children := this.Children
            if index < 0
                index := children.Length + index + 1
            if index < 1 || index > children.Length
                throw IndexError("Child index " index " is out of bounds", -1)
            return children[index]
        }

        ActionDescription(action) {
            action := IsInteger(action) ? this.Actions[action] : AxSpy.__NormalizeAction(action)
            pDesc := 0
            err := DllCall(AxSpy.__Sym(AxSpy.LibAX, "AXUIElementCopyActionDescription"), "Ptr", this.__ptr, "Ptr", AxSpy.__CFStr(action), "Ptr*", &pDesc, "Int")
            return err ? "" : AxSpy.__CFStringToStr(pDesc, true)
        }

        DoAction(action) {
            if IsInteger(action)
                action := this.Actions[action]
            else
                action := AxSpy.__NormalizeAction(action)
            err := DllCall(AxSpy.__Sym(AxSpy.LibAX, "AXUIElementPerformAction"), "Ptr", this.__ptr, "Ptr", AxSpy.__CFStr(action), "Int")
            return !err
        }

        DoDefaultAction() {
            for _, action in ["AXPress", "AXConfirm", "AXShowMenu", "AXPick", "AXRaise", "AXIncrement", "AXDecrement"]
                if this.HasAction(action) && this.DoAction(action)
                    return true
            actions := this.Actions
            return actions.Length ? this.DoAction(actions[1]) : false
        }

        HasAction(action) {
            action := AxSpy.__NormalizeAction(action)
            for _, a in this.Actions
                if a = action
                    return true
            return false
        }

        Press() => this.DoAction("AXPress")
        Confirm() => this.DoAction("AXConfirm")
        Cancel() => this.DoAction("AXCancel")
        Raise() => this.DoAction("AXRaise")
        ShowMenu() => this.DoAction("AXShowMenu")
        Increment() => this.DoAction("AXIncrement")
        Decrement() => this.DoAction("AXDecrement")
        Pick() => this.DoAction("AXPick")
        ScrollTo() => this.DoAction("AXScrollToVisible")

        Focus() {
            try this.SetBoolAttribute("AXFocused", true)
            try this.SetBoolAttribute("AXMain", true)
            try this.SetBoolAttribute("AXFrontmost", true)
            try this.DoAction("AXRaise")
            return this
        }

        ParameterizedAttribute(attr, parameter, default := unset) {
            attr := AxSpy.__NormalizeParamAttr(attr)
            pParam := 0, releaseParam := false
            if IsObject(parameter) {
                if parameter.HasOwnProp("Ptr") && !(parameter is Array)
                    pParam := parameter.Ptr, releaseParam := false
                else
                    pParam := AxSpy.__AnyToCF(parameter), releaseParam := true
            } else if parameter is Integer || parameter is Float || parameter is String
                pParam := AxSpy.__AnyToCF(parameter), releaseParam := true
            else
                throw TypeError("Unsupported parameter type for " attr, -1)
            pOut := 0
            err := DllCall(AxSpy.__Sym(AxSpy.LibAX, "AXUIElementCopyParameterizedAttributeValue"), "Ptr", this.__ptr, "Ptr", AxSpy.__CFStr(attr), "Ptr", pParam, "Ptr*", &pOut, "Int")
            if releaseParam
                AxSpy.__Release(pParam)
            if err {
                if IsSet(default)
                    return default
                AxSpy.__ThrowAX(err, "AXUIElementCopyParameterizedAttributeValue(" attr ")")
            }
            return AxSpy.__CFToValue(pOut, true)
        }

        GetText(startOffset := 0, endOffset := -1) {
            if endOffset = -1 {
                try endOffset := this.NumberOfCharacters
                catch {
                    endOffset := 0
                }
            }
            if endOffset > startOffset {
                try return this.GetStringForRange(startOffset, endOffset - startOffset)
            }
            try return this.Value ""
            catch
                return ""
        }

        SetText(text) => this.SetAttribute("AXValue", text)

        GetStringForRange(start, length) => this.ParameterizedAttribute("AXStringForRange", {location:start, length:length}, "")
        GetRTFForRange(start, length) => this.ParameterizedAttribute("AXRTFForRange", {location:start, length:length}, "")
        GetBoundsForRange(start, length) => this.ParameterizedAttribute("AXBoundsForRange", {location:start, length:length})
        GetRangeForPosition(x, y) => this.ParameterizedAttribute("AXRangeForPosition", {x:x, y:y})
        GetRangeForLine(line) => this.ParameterizedAttribute("AXRangeForLine", line)
        GetLineForIndex(index) => this.ParameterizedAttribute("AXLineForIndex", index)
        GetStyleRangeForIndex(index) => this.ParameterizedAttribute("AXStyleRangeForIndex", index)
        GetAttributedStringForRange(start, length) => this.ParameterizedAttribute("AXAttributedStringForRange", {location:start, length:length}, "")
        GetRangeForIndex(index) => this.ParameterizedAttribute("AXRangeForIndex", index)
        GetCellForColumnAndRow(column, row) => this.ParameterizedAttribute("AXCellForColumnAndRow", [column, row], 0)
        GetLayoutPointForScreenPoint(x, y) => this.ParameterizedAttribute("AXLayoutPointForScreenPoint", {x:x, y:y})
        GetLayoutSizeForScreenSize(w, h) => this.ParameterizedAttribute("AXLayoutSizeForScreenSize", {w:w, h:h})
        GetScreenPointForLayoutPoint(x, y) => this.ParameterizedAttribute("AXScreenPointForLayoutPoint", {x:x, y:y})
        GetScreenSizeForLayoutSize(w, h) => this.ParameterizedAttribute("AXScreenSizeForLayoutSize", {w:w, h:h})

        SelectText(start, length := unset) {
            if !IsSet(length)
                length := 0
            this.SelectedTextRange := {location:start, length:length}
            return this
        }

        ReplaceText(start, length, replacement) {
            old := this.GetText()
            this.SetText(SubStr(old, 1, start) replacement SubStr(old, start + length + 1))
            return this
        }

        InsertText(position, text) => this.ReplaceText(position, 0, text)
        DeleteText(start, length) => this.ReplaceText(start, length, "")

        Exists {
            get {
                try {
                    pNames := 0
                    err := DllCall(AxSpy.__Sym(AxSpy.LibAX, "AXUIElementCopyAttributeNames"), "Ptr", this.__ptr, "Ptr*", &pNames, "Int")
                    if pNames
                        AxSpy.__Release(pNames)
                    return err != AxSpy.Error.InvalidUIElement
                } catch
                    return false
            }
        }

        IsEqual(oCompare) {
            if !(oCompare is AxSpy.Element)
                return false
            eq := DllCall(AxSpy.__Sym(AxSpy.LibCF, "CFEqual"), "Ptr", this.__ptr, "Ptr", oCompare.Ptr, "Int")
            return !!eq
        }

        GetPath(oTarget) {
            if !(oTarget is AxSpy.Element)
                throw TypeError("oTarget must be a valid AxSpy element", -1)
            oNext := oTarget, oPrev := oTarget, path := ""
            try {
                while !this.IsEqual(oNext) {
                    oNext := oNext.Parent
                    for i, oChild in oNext.Children {
                        if oChild.IsEqual(oPrev) {
                            path := i "," path, oPrev := oNext
                            break
                        }
                    }
                }
                path := SubStr(path, 1, -1)
                if this.ElementFromPath(path).IsEqual(oTarget)
                    return path
            }
            oFind := this.FindElement({IsEqual:oTarget})
            return oFind ? oFind.Path : ""
        }

        FindElement(condition, scope := 4, index := 1, order := 0, depth := -1) {
            if IsObject(condition) && !HasMethod(condition) {
                for key in ["index", "scope", "depth", "order"]
                    if condition.HasOwnProp(key)
                        %key% := condition.%key%
                if condition.HasOwnProp("i")
                    index := condition.i
                if index < 0
                    order |= 2, index := -index
                else if index = 0
                    throw Error("Condition index cannot be 0", -1)
            }
            scope := IsInteger(scope) ? scope : AxSpy.TreeScope.%scope%
            order := IsInteger(order) ? order : AxSpy.TreeTraversalOptions.%order%
            if order&1
                return order&2 ? PostOrderLastToFirstRecursiveFind(this, condition, scope,, ++depth) : PostOrderFirstToLastRecursiveFind(this, condition, scope,, ++depth)
            if scope&1
                if this.ValidateCondition(condition) && (--index = 0)
                    return this.DefineProp("Path", {value:""})
            if scope>1
                return order&2 ? PreOrderLastToFirstRecursiveFind(this, condition, scope,, depth) : PreOrderFirstToLastRecursiveFind(this, condition, scope,, depth)

            PreOrderFirstToLastRecursiveFind(element, condition, scope := 4, path := "", depth := -1) {
                --depth
                for i, child in element.Children {
                    if child.ValidateCondition(condition) && (--index = 0)
                        return child.DefineProp("Path", {value:path (path?",":"") i})
                    else if (scope&4) && (depth != 0) && (rf := PreOrderFirstToLastRecursiveFind(child, condition,, path (path?",":"") i, depth))
                        return rf
                }
            }
            PreOrderLastToFirstRecursiveFind(element, condition, scope := 4, path := "", depth := -1) {
                children := element.Children, length := children.Length + 1, --depth
                Loop (length - 1) {
                    child := children[length-A_index]
                    if child.ValidateCondition(condition) && (--index = 0)
                        return child.DefineProp("Path", {value:path (path?",":"") (length-A_index)})
                    else if scope&4 && (depth != 0) && (rf := PreOrderLastToFirstRecursiveFind(child, condition,, path (path?",":"") (length-A_index), depth))
                        return rf
                }
            }
            PostOrderFirstToLastRecursiveFind(element, condition, scope := 4, path := "", depth := -1) {
                if (--depth != 0) && scope>1 {
                    for i, child in element.Children {
                        if (rf := PostOrderFirstToLastRecursiveFind(child, condition, (scope & ~2)|1, path (path?",":"") i, depth))
                            return rf
                    }
                }
                if scope&1 && element.ValidateCondition(condition) && (--index = 0)
                    return element.DefineProp("Path", {value:path})
            }
            PostOrderLastToFirstRecursiveFind(element, condition, scope := 4, path := "", depth := -1) {
                if (--depth != 0) && scope>1 {
                    children := element.Children, length := children.Length + 1
                    Loop (length - 1) {
                        if (rf := PostOrderLastToFirstRecursiveFind(children[length-A_index], condition, (scope & ~2)|1, path (path?",":"") (length-A_index), depth))
                            return rf
                    }
                }
                if scope&1 && element.ValidateCondition(condition) && (--index = 0)
                    return element.DefineProp("Path", {value:path})
            }
        }
        FindFirst(args*) => this.FindElement(args*)

        FindElements(condition := True, scope := 4, depth := -1) {
            if IsObject(condition) && !HasMethod(condition) {
                if condition.HasOwnProp("scope")
                    scope := condition.scope
                if condition.HasOwnProp("depth")
                    depth := condition.depth
            }
            matches := [], ++depth, scope := IsInteger(scope) ? scope : AxSpy.TreeScope.%scope%
            if scope&1
                if this.ValidateCondition(condition)
                    matches.Push(this.DefineProp("Path", {value:""}))
            if scope>1
                RecursiveFind(this, condition, (scope|1)^1, &matches,, depth)
            return matches
            RecursiveFind(element, condition, scope, &matches, path := "", depth := -1) {
                if scope>1 {
                    --depth
                    for i, child in element.Children {
                        if child.ValidateCondition(condition)
                            matches.Push(child.DefineProp("Path", {value:path (path?",":"") i}))
                        if scope&4 && (depth != 0)
                            RecursiveFind(child, condition, scope, &matches, path (path?",":"") i, depth)
                    }
                }
            }
        }
        FindAll(args*) => this.FindElements(args*)

        ElementExist(condition, scope := 4, index := 1, order := 0, depth := -1) {
            try return this.FindElement(condition, scope, index, order, depth)
            catch
                return 0
        }

        WaitElement(conditionOrPath, timeOut := -1, scope := 4, index := 1, order := 0, depth := -1) {
            if Type(conditionOrPath) = "Object" && conditionOrPath.HasOwnProp("timeOut")
                timeOut := conditionOrPath.timeOut
            waitTime := A_TickCount + timeOut
            while ((timeOut < 1) ? 1 : (A_TickCount < waitTime)) {
                try {
                    if IsObject(conditionOrPath)
                        return this.FindElement(conditionOrPath, scope, index, order, depth)
                    return this.ElementFromPath(conditionOrPath)
                }
                Sleep 40
            }
        }

        WaitElementExist(conditionOrPath, timeOut := -1, scope := 4, index := 1, order := 0, depth := -1) {
            waitTime := A_TickCount + timeOut
            while ((timeOut < 1) ? 1 : (A_TickCount < waitTime)) {
                try {
                    oFind := IsObject(conditionOrPath) ? this.FindElement(conditionOrPath, scope, index, order, depth) : this.ElementFromPath(conditionOrPath)
                    if oFind.Exists
                        return oFind
                }
                Sleep 40
            }
        }

        ElementFromPath(paths*) {
            local err
            try return this[paths*]
            catch IndexError as err
                throw IndexError(StrReplace(err.Message, "at index", "at path index"), -1, err.Extra)
        }

        ElementFromPathExist(paths*) {
            try return this[paths*]
            catch IndexError
                return 0
            return 0
        }

        WaitElementFromPath(paths*) {
            local timeOut := -1, endtime, tick := 20
            if paths.Length > 1 && paths[paths.Length] is Integer {
                paths := paths.Clone()
                if paths[-2] is Integer
                    tick := paths.Pop()
                timeOut := paths.Pop()
            }
            endtime := A_TickCount + timeOut
            while ((timeOut = -1) || (A_TickCount < endtime)) {
                try return this[paths*]
                Sleep tick
            }
        }

        WaitNotExist(timeOut := -1) {
            waitTime := A_TickCount + timeOut
            while ((timeOut < 1) ? 1 : (A_TickCount < waitTime)) {
                if !this.Exists
                    return 1
                Sleep 40
            }
        }

        Normalize(condition) {
            if this.ValidateCondition(condition)
                return this
            oEl := this
            Loop {
                try {
                    oEl := oEl.Parent
                    if oEl.ValidateCondition(condition)
                        return oEl
                } catch
                    break
            }
            return 0
        }

        ValidateCondition(oCond) {
            if !IsObject(oCond)
                return !!oCond
            if HasMethod(oCond)
                return oCond(this)
            else if oCond is Array {
                for _, c in oCond
                    if this.ValidateCondition(c)
                        return 1
                return 0
            }
            matchmode := 3, casesensitive := 1
            for p in ["matchmode", "mm"]
                if oCond.HasOwnProp(p)
                    matchmode := oCond.%p%
            try matchmode := IsInteger(matchmode) ? matchmode : AxSpy.MatchMode.%matchmode%
            for p in ["casesensitive", "cs"]
                if oCond.HasOwnProp(p)
                    casesensitive := oCond.%p%
            for prop, cond in oCond.OwnProps() {
                switch Type(cond) {
                    case "String", "Integer", "Float":
                        if prop ~= "i)^(index|i|matchmode|mm|casesensitive|cs|scope|timeout|order|depth)$"
                            continue
                        propValue := ""
                        try propValue := this.%prop%
                        if IsObject(propValue)
                            return 0
                        if !AxSpy.__MatchText(propValue, cond, matchmode, casesensitive)
                            return 0
                    case "AxSpy.Element":
                        if (prop="IsEqual") ? !this.IsEqual(cond) : !this.ValidateCondition(cond)
                            return 0
                    default:
                        if (HasProp(cond, "Length") ? cond.Length = 0 : ObjOwnPropCount(cond) = 0) {
                            try return this.%prop% && 0
                            catch
                                return 1
                        } else if (prop = "Location") {
                            try loc := this.Location
                            catch
                                return 0
                            for lprop, lval in cond.OwnProps() {
                                if (lprop = "relative" || lprop = "r")
                                    continue
                                if (loc.%lprop% != lval)
                                    return 0
                            }
                        } else if ((prop = "not") ? this.ValidateCondition(cond) : !this.ValidateCondition(cond))
                            return 0
                }
            }
            return 1
        }

        Dump(scope := 1, delimiter := " ", depth := -1) {
            out := "", scope := IsInteger(scope) ? scope : AxSpy.TreeScope.%scope%
            if scope&1 {
                RoleName := "N/A", Subrole := "", Name := "N/A", Value := "", Location := {x:"N/A",y:"N/A",w:"N/A",h:"N/A"}, Pid := "N/A"
                Actions := "", Attrs := ""
                try RoleName := this.RoleName
                try Subrole := this.Subrole
                try Name := this.Name
                try Value := this.Value
                try Location := this.Location
                try Pid := this.Pid
                try Actions := JoinArray(this.Actions, ",")
                try Attrs := JoinArray(this.Attributes, ",")
                out := "Role: " RoleName (Subrole ? "/" Subrole : "") delimiter "Pid: " Pid delimiter "[Location: {x:" Location.x ",y:" Location.y ",w:" Location.w ",h:" Location.h "}]" delimiter "[Name: " Name "]"
                    . (Value != "" && !IsObject(Value) ? delimiter "[Value: " Value "]" : "")
                    . (Actions ? delimiter "[Actions: " Actions "]" : "")
                    . (Attrs ? delimiter "[Attrs: " Attrs "]" : "") "`n"
            }
            if scope&4
                return Trim(RecurseTree(this, out,, depth), "`n")
            if scope&2 {
                for n, oChild in this.Children
                    out .= n ":" delimiter oChild.Dump() "`n"
            }
            return Trim(out, "`n")

            JoinArray(arr, delim) {
                out := ""
                for _, v in arr
                    out .= (out ? delim : "") v
                return out
            }
            RecurseTree(oAcc, tree, path := "", depth := -1) {
                if depth > 0 {
                    StrReplace(path, ",",, , &count)
                    if count >= (depth - 1)
                        return tree
                }
                try {
                    if !oAcc.ChildCount
                        return tree
                } catch
                    return tree
                for i, oChild in oAcc.Children {
                    tree .= path (path?",":"") i ":" delimiter oChild.Dump() "`n"
                    tree := RecurseTree(oChild, tree, path (path?",":"") i, depth)
                }
                return tree
            }
        }
        DumpAll(delimiter := " ", depth := -1) => this.Dump(5, delimiter, depth)

        Highlight(showTime := unset, color := "Red", d := 2) {
            if !AxSpy.__HighlightGuis.Has(this)
                AxSpy.__HighlightGuis[this] := []
            if (!IsSet(showTime) && AxSpy.__HighlightGuis[this].Length) || (IsSet(showTime) && showTime = "clear") {
                for _, r in AxSpy.__HighlightGuis[this]
                    try r.Destroy()
                AxSpy.__HighlightGuis[this] := []
                return this
            } else if !IsSet(showTime)
                showTime := 2000
            try loc := this.Location
            if !IsSet(loc) || !IsObject(loc) || loc.w < 1 || loc.h < 1
                return this
            try {
                Loop 4
                    AxSpy.__HighlightGuis[this].Push(Gui("+AlwaysOnTop -Caption +ToolWindow -DPIScale +E0x08000000"))
                Loop 4 {
                    i := A_Index
                    x1 := (i=2 ? loc.x+loc.w : loc.x-d)
                    y1 := (i=3 ? loc.y+loc.h : loc.y-d)
                    w1 := (i=1 or i=3 ? loc.w+2*d : d)
                    h1 := (i=2 or i=4 ? loc.h+2*d : d)
                    AxSpy.__HighlightGuis[this][i].BackColor := color
                    AxSpy.__HighlightGuis[this][i].Show("NA x" . x1 . " y" . y1 . " w" . w1 . " h" . h1)
                }
                if showTime > 0 {
                    Sleep(showTime)
                    this.Highlight()
                } else if showTime < 0
                    SetTimer(ObjBindMethod(this, "Highlight", "clear"), -Abs(showTime))
            }
            return this
        }
        ClearHighlight() => this.Highlight("clear")

        Click(WhichButton := "left", ClickCount := 1, DownOrUp := "", Relative := "", NoActivate := False) {
            if WhichButton = "left" && ClickCount = 1 && DownOrUp = "" && Relative = "" && this.HasAction("AXPress") {
                if this.DoAction("AXPress")
                    return this
            }
            rel := [0,0], pos := this.Location, saveCoordMode := A_CoordModeMouse, cCount := 1, SleepTime := -1
            if (Relative && !InStr(Relative, "rel"))
                rel := StrSplit(Relative, " "), Relative := ""
            if IsInteger(WhichButton)
                SleepTime := WhichButton, WhichButton := "left"
            if !IsInteger(ClickCount) && InStr(ClickCount, " ") {
                sCount := StrSplit(ClickCount, " ")
                cCount := sCount[1], SleepTime := sCount[2]
            } else if ClickCount > 9 {
                SleepTime := ClickCount, cCount := 1, ClickCount := 1
            }
            CoordMode("Mouse", "Screen")
            Click(pos.x + pos.w//2 + rel[1] " " pos.y + pos.h//2 + rel[2] " " WhichButton (cCount ? " " cCount : "") (DownOrUp ? " " DownOrUp : "") (Relative ? " " Relative : ""))
            CoordMode("Mouse", saveCoordMode)
            Sleep(SleepTime)
            return this
        }
    }

    /**
     * Simple macOS AX element viewer (tree + live inspection).
     */
    class Viewer {
        __New() {
            CoordMode("Mouse", "Screen")
            this.Stored := {mwId:0, FilteredTreeView:Map(), TreeView:Map()}
            this.OnlyVisibleElements := false
            this.Capturing := false
            this.gViewer := Gui("AlwaysOnTop Resize", "AxSpyViewer")
            this.gViewer.OnEvent("Close", (*) => ExitApp())
            this.gViewer.OnEvent("Size", this.GetMethod("gViewer_Size").Bind(this))
            this.gViewer.Add("Text", "w100", "Window Info").SetFont("bold")
            this.LVWin := this.gViewer.Add("ListView", "h140 w250", ["Property", "Value"])
            this.LVWin.OnEvent("ContextMenu", LV_CopyTextMethod := this.GetMethod("LV_CopyText").Bind(this))
            this.LVWin.ModifyCol(1, 100)
            this.LVWin.ModifyCol(2, 140)
            for _, v in this.DefaultLVWinItems := ["Title", "Text", "Id", "Location", "Class(NN)", "Process", "PID"]
                this.LVWin.Add(, v, "")
            this.gViewer.Add("Text", "w100", "AxSpy Info").SetFont("bold")
            this.LVProps := this.gViewer.Add("ListView", "h220 w250", ["Property", "Value"])
            this.LVProps.OnEvent("ContextMenu", LV_CopyTextMethod)
            this.LVProps.ModifyCol(1, 100)
            this.LVProps.ModifyCol(2, 140)
            for _, v in this.DefaultLVPropsItems := ["RoleName", "RoleId", "Subrole", "RoleDescription", "Name", "Title", "Description", "Value", "ValueDescription", "States", "Location", "RawLocation", "Actions", "ActionDescriptions", "Attributes", "SettableAttributes", "ParameterizedAttributes", "Identifier", "AccessibleId", "ProcessId", "Id", "ChildCount", "Interfaces"]
                this.LVProps.Add(, v, "")
            this.ButCapture := this.gViewer.Add("Button", "xp+60 y+10 w140", "Start capturing (Alt+S)")
            this.ButCapture.OnEvent("Click", this.CaptureHotkeyFunc := this.GetMethod("ButCapture_Click").Bind(this))
            HotKey("!s", this.CaptureHotkeyFunc)
            this.SBMain := this.gViewer.Add("StatusBar",, "  Start capturing, then hold cursor still to construct tree")
            this.SBMain.OnEvent("Click", this.GetMethod("SBMain_Click").Bind(this))
            this.SBMain.OnEvent("ContextMenu", this.GetMethod("SBMain_Click").Bind(this))
            this.gViewer.Add("Text", "x278 y10 w200", "AX Tree").SetFont("bold")
            this.TVElement := this.gViewer.Add("TreeView", "x275 y35 w250 h390 -0x800")
            this.TVElement.OnEvent("ItemSelect", this.GetMethod("TVElement_Click").Bind(this))
            this.TVElement.OnEvent("ContextMenu", this.GetMethod("TVElement_ContextMenu").Bind(this))
            this.TVElement.Add("Start capturing to show tree")
            this.TextFilterTVElement := this.gViewer.Add("Text", "x275 y428", "Filter:")
            this.EditFilterTVElement := this.gViewer.Add("Edit", "x305 y425 w100")
            this.EditFilterTVElement.OnEvent("Change", this.GetMethod("EditFilterTV_Change").Bind(this))
            this.CBVisibleElements := this.gViewer.Add("Checkbox", "x+8 yp+4", "Visible elements")
            this.CBVisibleElements.OnEvent("Click", this.GetMethod("CBVisibleElements_Change").Bind(this))
            this.gViewer.Show()
        }

        gViewer_Size(GuiObj, MinMax, Width, Height) {
            this.SBMain.GetPos(,,, &SBHeight)
            bottomY := Height - SBHeight - GuiObj.MarginY
            this.TVElement.GetPos(&TVElementX, &TVElementY, &TVElementWidth, &TVElementHeight)
            this.EditFilterTVElement.GetPos(,,, &editH)
            this.TextFilterTVElement.GetPos(,,, &textH)
            this.CBVisibleElements.GetPos(,,, &cbH)
            filterY := bottomY - editH
            this.EditFilterTVElement.Move(TVElementX+30, filterY)
            this.TextFilterTVElement.Move(TVElementX, filterY + (editH - textH) // 2)
            this.CBVisibleElements.Move(TVElementX+140, filterY + (editH - cbH) // 2)
            gap := 10
            this.TVElement.Move(,, Width-TVElementX-10, filterY-TVElementY-gap)
            this.LVProps.GetPos(&LVPropsX, &LVPropsY, &LVPropsWidth, &LVPropsHeight)
            lvPropsHeight := bottomY - LVPropsY - 45
            this.LVProps.Move(,,, lvPropsHeight)
            this.ButCapture.Move(, LVPropsY + lvPropsHeight + 10)
        }

        ButCapture_Click(GuiCtrlObj?, Info?) {
            if this.Capturing {
                this.StopCapture()
                return
            }
            this.Capturing := true
            this.TVElement.Delete()
            this.TVElement.Add("Hold cursor still to construct tree")
            this.ButCapture.Text := "Stop capturing (Alt+S)"
            this.CaptureCallback := this.GetMethod("CaptureCycle").Bind(this)
            SetTimer(this.CaptureCallback, 200)
        }

        LV_CopyText(GuiCtrlObj, Info, *) {
            local out := "", LVData := Info > GuiCtrlObj.GetCount()
                ? ListViewGetContent("", GuiCtrlObj)
                : ListViewGetContent("Selected", GuiCtrlObj)
            for LVData in StrSplit(LVData, "`n") {
                LVData := StrSplit(LVData, "`t",, 2)
                if LVData.Length < 2
                    continue
                switch LVData[1], 0 {
                    case "Location", "RawLocation":
                        LVData[2] := "{" RegExReplace(LVData[2], "(\w:) ([^ ]+)(?= )", "$1$2,") "}"
                }
                out .= ", " (GuiCtrlObj.Hwnd = this.LVWin.Hwnd ? "" : LVData[1] ":") (LVData[1] = "Location" || LVData[1] = "RawLocation" || IsInteger(LVData[2]) ? LVData[2] : "`"" StrReplace(StrReplace(LVData[2], "``", "````"), "`"", "```"") "`"")
            }
            ToolTip("Copied: " (A_Clipboard := SubStr(out, 3)))
            SetTimer((*) => ToolTip(), -3000)
        }

        SBMain_Click(GuiCtrlObj, Info, *) {
            if InStr(this.SBMain.Text, "Path:") {
                ToolTip("Copied: " (A_Clipboard := SubStr(this.SBMain.Text, 9)))
                SetTimer((*) => ToolTip(), -3000)
            }
        }

        CBVisibleElements_Change(GuiCtrlObj, Info) {
            this.OnlyVisibleElements := GuiCtrlObj.Value
            if this.Stored.TreeView.Count
                this.ConstructTreeView()
        }

        StopCapture(GuiCtrlObj:=0, Info:=0) {
            if this.Capturing {
                this.Capturing := false
                this.ButCapture.Text := "Start capturing (Alt+S)"
                SetTimer(this.CaptureCallback, 0)
                if this.Stored.HasOwnProp("oElement")
                    this.Stored.oElement.Highlight()
                return
            }
        }

        CaptureCycle() {
            Thread "NoTimers"
            MouseGetPos(&mX, &mY, &mwId)
            if WinExist(mwId)
                this.LVWin_Populate(mwId)
            else
                this.LVWin_Clear()
            try oElement := AxSpy.ElementFromPoint(mX, mY)
            catch {
                AxSpy.ClearAllHighlights()
                this.LVProps_Clear()
                this.TVElement.Delete()
                this.TVElement.Add("No accessible element at point")
                return
            }
            if !IsObject(oElement) {
                AxSpy.ClearAllHighlights()
                this.LVProps_Clear()
                this.TVElement.Delete()
                this.TVElement.Add("No accessible element at point")
                return
            }
            if this.Stored.HasOwnProp("oElement") && oElement.IsEqual(this.Stored.oElement) {
                if this.FoundTime != 0 && ((A_TickCount - this.FoundTime) > 1000) {
                    if (mX == this.Stored.mX) && (mY == this.Stored.mY)
                        this.ConstructTreeView(), this.FoundTime := 0
                    else
                        this.FoundTime := A_TickCount
                }
                this.Stored.mX := mX, this.Stored.mY := mY
                return
            }
            this.LVProps_Populate(oElement)
            this.Stored.mwId := mwId, this.Stored.oElement := oElement, this.Stored.mX := mX, this.Stored.mY := mY, this.FoundTime := A_TickCount
        }

        LVWin_Clear() {
            this.LVWin.Delete()
            for v in this.DefaultLVWinItems
                this.LVWin.Add(, v, "")
        }

        LVWin_Populate(mwId) {
            this.LVWin.Delete()
            WinGetPos(&mwX, &mwY, &mwW, &mwH, mwId)
            props := Map("Title", WinGetTitle(mwId), "Text", WinGetText(mwId), "Id", mwId, "Location", "x: " mwX " y: " mwY " w: " mwW " h: " mwH, "Class(NN)", WinGetClass(mwId), "Process", WinGetProcessName(mwId), "PID", WinGetPID(mwId))
            for propName in this.DefaultLVWinItems
                this.LVWin.Add(, propName, props[propName])
        }

        LVProps_Clear() {
            this.LVProps.Delete()
            for v in this.DefaultLVPropsItems
                this.LVProps.Add(, v, "")
        }

        LVProps_Populate(oElement) {
            AxSpy.ClearAllHighlights()
            oElement.Highlight(0)
            this.LVProps.Delete()
            for _, prop in this.DefaultLVPropsItems {
                value := "N/A"
                try {
                    if prop = "Location" || prop = "RawLocation"
                        value := this.FormatLocation(oElement.%prop%)
                    else
                        value := this.ValueToString(oElement.%prop%)
                }
                this.LVProps.Add(, prop, value)
            }
        }

        FormatLocation(loc) => IsObject(loc) ? "x: " loc.x " y: " loc.y " w: " loc.w " h: " loc.h : loc

        ValueToString(value) {
            if value is Buffer
                return "Buffer(size=" value.Size ")"
            if value is Array
                return value.Length ? this.JoinArray(value, ",") : ""
            if value is Map {
                out := ""
                for k, v in value
                    out .= (out ? "," : "") k ":" v
                return out
            }
            if IsObject(value) {
                if value.HasOwnProp("x") && value.HasOwnProp("y")
                    return this.FormatLocation(value)
                if value.HasOwnProp("location") && value.HasOwnProp("length")
                    return "location: " value.location " length: " value.length
                try return value.Name
                catch
                    return Type(value)
            }
            return value ""
        }

        TVElement_Click(GuiCtrlObj, Info) {
            if this.Capturing
                return
            try oElement := this.EditFilterTVElement.Value ? this.Stored.FilteredTreeView[Info] : this.Stored.TreeView[Info]
            if IsSet(oElement) && oElement {
                try this.SBMain.SetText("  Path: " oElement.Path)
                this.LVProps_Populate(oElement)
            }
        }

        TVElement_ContextMenu(GuiCtrlObj, Item, IsRightClick, X, Y) {
            TVElement_Menu := Menu()
            try oElement := this.EditFilterTVElement.Value ? this.Stored.FilteredTreeView[Item] : this.Stored.TreeView[Item]
            if IsSet(oElement)
                TVElement_Menu.Add("Copy to Clipboard", (*) => A_Clipboard := oElement.Dump())
            TVElement_Menu.Add("Copy Tree to Clipboard", (*) => A_Clipboard := this.GetTreeRoot().DumpAll())
            TVElement_Menu.Show()
        }

        EditFilterTV_Change(GuiCtrlObj, Info, *) {
            static TimeoutFunc := "", ChangeActive := false
            if !this.Stored.TreeView.Count
                return
            if (Info != "DoAction") || ChangeActive {
                if !TimeoutFunc
                    TimeoutFunc := this.GetMethod("EditFilterTV_Change").Bind(this, GuiCtrlObj, "DoAction")
                SetTimer(TimeoutFunc, -500)
                return
            }
            ChangeActive := true
            this.Stored.FilteredTreeView := Map(), parents := Map()
            if !(searchPhrase := this.EditFilterTVElement.Value) {
                this.ConstructTreeView()
                ChangeActive := false
                return
            }
            this.TVElement.Delete()
            this.TVElement.Add("Searching...")
            Sleep -1
            this.TVElement.Opt("-Redraw")
            this.TVElement.Delete()
            for index, oElement in this.Stored.TreeView {
                for _, prop in this.DefaultLVPropsItems {
                    try {
                        haystack := this.ValueToString(oElement.%prop%)
                        if InStr(haystack, searchPhrase) {
                            if !parents.Has(prop)
                                parents[prop] := this.TVElement.Add(prop,, "Expand")
                            this.Stored.FilteredTreeView[this.TVElement.Add(this.GetShortDescription(oElement), parents[prop], "Expand")] := oElement
                        }
                    }
                }
            }
            if !this.Stored.FilteredTreeView.Count
                this.TVElement.Add("No results found matching `"" searchPhrase "`"")
            this.TVElement.Opt("+Redraw")
            TimeoutFunc := "", ChangeActive := false
        }

        ConstructTreeView() {
            this.TVElement.Delete()
            this.TVElement.Add("Constructing Tree, please wait...")
            Sleep -1
            this.TVElement.Opt("-Redraw")
            this.TVElement.Delete()
            this.Stored.TreeView := Map()
            root := this.GetTreeRoot()
            if !root {
                this.TVElement.Add("No tree root available")
                this.TVElement.Opt("+Redraw")
                return
            }
            this.RecurseTreeView(root)
            this.TVElement.Opt("+Redraw")
            if this.Stored.HasOwnProp("oElement") {
                for k, v in this.Stored.TreeView
                    if this.Stored.oElement.IsEqual(v)
                        this.TVElement.Modify(k, "Vis Select"), this.SBMain.SetText("  Path: " v.Path)
            }
        }

        GetTreeRoot() {
            if !this.Stored.HasOwnProp("oElement")
                return 0
            oElement := this.Stored.oElement
            for _, attr in ["TopLevelUIElement", "Window", "Application"] {
                try {
                    root := oElement.%attr%
                    if root
                        return root
                }
            }
            try return AxSpy.ElementFromPid(oElement.Pid)
            catch
                return oElement
        }

        RecurseTreeView(oElement, parent:=0, path:="", depth:=0) {
            if !oElement
                return
            this.Stored.TreeView[TWEl := this.TVElement.Add(this.GetShortDescription(oElement), parent, "Expand")] := oElement.DefineProp("Path", {value:path})
            i := 0
            try children := oElement.Children
            catch
                return
            for _, v in children {
                if !this.OnlyVisibleElements || this.IsVisible(v)
                    ++i, this.RecurseTreeView(v, TWEl, path (path ? "," : "") i, depth + 1)
            }
        }

        GetShortDescription(oElement) {
            elDesc := " `"`""
            try elDesc := " `"" oElement.Name "`""
            try elDesc := oElement.RoleName elDesc
            catch
                elDesc := "`"`"" elDesc
            return elDesc
        }

        IsVisible(oElement) {
            try return oElement.Visible
            catch
                return false
        }

        JoinArray(arr, delim) {
            out := ""
            for _, v in arr
                out .= (out ? delim : "") v
            return out
        }
    }

}
