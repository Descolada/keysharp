# Capability Matrix

Generated from docs/capabilities.json via scripts/generate-capabilities.ps1.

Status legend:
- Full: Implemented and generally usable
- Partial: Implemented with known limitations or gaps
- Planned: Not implemented yet, but intended
- Unsupported: Not supported
- Unknown: Not yet verified

| Capability | Windows | Linux (X11) | Linux (Wayland) | macOS | Notes |
|---|---|---|---|---|---|
| - | Full | Full | Full | Full | Subtraction / unary minus operator |
| -- | Full | Full | Full | Full | Decrement operator |
| ! | Full | Full | Full | Full | Logical NOT operator |
| !~= | Planned | Planned | Planned | Planned | Regular-expression not-match operator. |
| != | Full | Full | Full | Full | Inequality operator |
| !== | Full | Full | Full | Full | Strict inequality operator. |
| #AssemblyCompany | Full | Full | Full | Full | Sets assembly company metadata for compiled scripts. |
| #AssemblyConfiguration | Full | Full | Full | Full | Sets assembly configuration metadata for compiled scripts. |
| #AssemblyCopyright | Full | Full | Full | Full | Sets assembly copyright metadata for compiled scripts. |
| #AssemblyDescription | Full | Full | Full | Full | Sets assembly description metadata for compiled scripts. |
| #AssemblyName | Full | Full | Full | Full | Sets assembly name metadata for compiled scripts. |
| #AssemblyProduct | Full | Full | Full | Full | Sets assembly product metadata for compiled scripts. |
| #AssemblyTrademark | Full | Full | Full | Full | Sets assembly trademark metadata for compiled scripts. |
| #AssemblyVersion | Full | Full | Full | Full | Sets assembly version metadata for compiled scripts. |
| #ClipboardTimeout | Full | Full | Full | Full | Sets how long clipboard operations should wait before timing out. |
| #DefaultReturn | Full | Full | Full | Full | The #DefaultReturn directive sets the default return to either "" or unset. |
| #Define | Full | Full | Full | Full | Defines a conditional compilation symbol. |
| #DllLoad | Full | Full | Full | Full | The #DllLoad directive loads a DLL or EXE file before the script starts executing. |
| #ElIf | Full | Full | Full | Full | Adds an alternate conditional compilation branch. |
| #Else | Full | Full | Full | Full | Adds a fallback branch in a conditional compilation block. |
| #EndIf | Full | Full | Full | Full | Ends a conditional compilation block. |
| #EndRegion | Planned | Planned | Planned | Planned | Ends a foldable source region. |
| #Error | Planned | Planned | Planned | Planned | Emits a compile-time error directive message. |
| #ErrorStdOut | Full | Full | Full | Full | The #ErrorStdOut directive sends any syntax error that prevents a script from launching to the standard error stream (stderr) rather than displaying a dialog. |
| #HookMutexName | Full | Full | Full | Full | Sets the mutex name used for global hook synchronization. |
| #HotIf | Full | Full | Full | Full | The #HotIf directive creates context-sensitive hotkeys and hotstrings. They perform a different action (or none at all) depending on any condition (an expression). |
| #HotIfTimeout | Full | Full | Full | Full | The #HotIfTimeout directive sets the maximum time that may be spent evaluating a single #HotIf expression. |
| #Hotstring | Full | Full | Full | Full | The #Hotstring directive changes hotstring options or ending characters. |
| #If | Full | Full | Full | Full | Begins a conditional compilation block. |
| #Import | Full | Full | Full | Full | Imports exported names from another script file. |
| #Include | Full | Full | Full | Full | The #Include and #IncludeAgain directives cause the script to behave as though the specified file's contents are present at this exact position. |
| #IncludeAgain | Full | Full | Full | Full | The #Include and #IncludeAgain directives cause the script to behave as though the specified file's contents are present at this exact position. |
| #InputLevel | Full | Full | Full | Full | The #InputLevel directive controls which artificial keyboard and mouse events are ignored by hotkeys and hotstrings. |
| #Line | Planned | Planned | Planned | Planned | Controls reported line/file information for diagnostics. |
| #MaxThreads | Full | Full | Full | Full | The #MaxThreads directive sets the maximum number of simultaneous threads. |
| #MaxThreadsBuffer | Full | Full | Full | Full | The #MaxThreadsBuffer directive causes some or all hotkeys to buffer rather than ignore keypresses when their #MaxThreadsPerHotkey limit has been reached. |
| #MaxThreadsPerHotkey | Full | Full | Full | Full | The #MaxThreadsPerHotkey directive sets the maximum number of simultaneous threads per hotkey or hotstring. |
| #Module | Full | Full | Full | Full | The #Module directive starts a new module or reopens an existing module. |
| #NoTrayIcon | Full | Full | Full | Full | The #NoTrayIcon directive disables the showing of a tray icon. |
| #Nullable | Planned | Planned | Planned | Planned | Sets nullable-context preprocessor state. |
| #Pragma | Planned | Planned | Planned | Planned | Applies pragma options recognized by the preprocessor. |
| #Region | Planned | Planned | Planned | Planned | Begins a foldable source region. |
| #Requires | Full | Full | Full | Full | The #Requires directive displays an error and quits if a version requirement is not met. |
| #SingleInstance | Full | Full | Full | Full | The #SingleInstance directive determines whether a script is allowed to run again when it is already running. |
| #StructPack | Planned | Planned | Planned | Planned | Planned; struct support is not yet implemented in Keysharp. |
| #SuspendExempt | Full | Full | Full | Full | The #SuspendExempt directive exempts subsequent hotkeys and hotstrings from suspension. |
| #Undef | Full | Full | Full | Full | Undefines a conditional compilation symbol. |
| #UseHook | Full | Full | Full | Full | The #UseHook directive forces the use of the hook to implement all or some keyboard hotkeys. |
| #Warn | Full | Full | Full | Full | The #Warn directive enables or disables warnings for specific conditions which may indicate an error, such as a typo or missing "global" declaration. |
| #Warning | Full | Full | Full | Full | Emits a compile-time warning directive message. |
| #WinActivateForce | Full | Full | Full | Full | The #WinActivateForce directive skips the gentle method of activating a window and goes straight to the forceful method. |
| %...% / Dereference | Full | Full | Full | Full | Performs dynamic dereferencing (double-deref) to resolve a variable name stored in another variable. |
| & | Full | Full | Full | Full | Bitwise AND operator |
| & (VarRef) | Full | Full | Full | Full | VarRef/address-of operator |
| && | Full | Full | Full | Full | Logical AND operator |
| &= | Full | Full | Full | Full | Compound assignment operator |
| * | Full | Full | Full | Full | Multiplication operator |
| ** | Full | Full | Full | Full | Power operator |
| **= | Full | Full | Full | Full | Compound assignment operator |
| *= | Full | Full | Full | Full | Compound assignment operator |
| , | Full | Full | Full | Full | Comma/sequence operator |
| . | Full | Full | Full | Full | Concatenation operator |
| .= | Full | Full | Full | Full | Compound assignment operator |
| / | Full | Full | Full | Full | Division operator |
| // | Full | Full | Full | Full | Integer division operator |
| //= | Full | Full | Full | Full | Compound assignment operator |
| /= | Full | Full | Full | Full | Compound assignment operator |
| := | Full | Full | Full | Full | Assignment operator |
| ?: | Full | Full | Full | Full | Ternary operator |
| ?? | Full | Full | Full | Full | Null coalescing operator |
| ??= | Full | Full | Full | Full | Null-coalescing assignment operator |
| [ ... ] / Array | Full | Full | Full | Full | Creates an Array literal. |
| [ ... ] / Map | Full | Full | Full | Full | Creates an Map literal. |
| ^ | Full | Full | Full | Full | Bitwise XOR operator |
| ^= | Full | Full | Full | Full | Compound assignment operator |
| __Call | Full | Full | Full | Full | Meta-function invoked when calling a missing method or property. |
| __Delete | Full | Full | Full | Full | Meta-function invoked when an object is being deleted. |
| __Enum() | Full | Full | Full | Full | Returns an enumerator for the object. |
| __Get | Full | Full | Full | Full | Meta-function invoked when getting a missing property. |
| __Init() | Full | Full | Full | Full | Class initialization method executed once before first use. |
| __Item | Full | Full | Full | Full | Indexer meta-property for bracket access. |
| __New | Full | Full | Full | Full | Meta-function invoked when constructing a new object. |
| __Set | Full | Full | Full | Full | Meta-function invoked when setting a missing property. |
| { ... } (Block) | Full | Full | Full | Full | Creates a block scope for one or more statements. |
| { ... } / Object | Full | Full | Full | Full | Creates an Object literal. |
| {Blind} | Full | Partial | Unknown | Unknown | Send option which preserves modifier state while sending keys. |
| \\| | Full | Full | Full | Full | Bitwise OR operator |
| \\|\\| | Full | Full | Full | Full | Logical OR operator |
| \\|= | Full | Full | Full | Full | Compound assignment operator |
| ~ | Full | Full | Full | Full | Bitwise NOT operator |
| ~= | Full | Full | Full | Full | Regex match operator |
| + | Full | Full | Full | Full | Addition / unary plus operator |
| ++ | Full | Full | Full | Full | Increment operator |
| += | Full | Full | Full | Full | Compound assignment operator |
| < | Full | Full | Full | Full | Comparison operator |
| << | Full | Full | Full | Full | Left shift operator |
| <<= | Full | Full | Full | Full | Compound assignment operator |
| <= | Full | Full | Full | Full | Comparison operator |
| <> | Full | Full | Full | Full | Inequality alias operator |
| = | Full | Full | Full | Full | Case-insensitive equality operator |
| -= | Full | Full | Full | Full | Compound assignment operator |
| == | Full | Full | Full | Full | Case-sensitive equality operator |
| => | Full | Full | Full | Full | Fat-arrow function operator. |
| > | Full | Full | Full | Full | Comparison operator |
| >= | Full | Full | Full | Full | Comparison operator |
| >> | Full | Full | Full | Full | Right shift operator |
| >>= | Full | Full | Full | Full | Compound assignment operator |
| >>> | Full | Full | Full | Full | Logical right shift operator |
| >>>= | Full | Full | Full | Full | Compound assignment operator |
| 1, 2, 3 | Full | Full | Full | Full | Comma operator evaluates expressions left-to-right and returns the last value. |
| A_AhkPath | Full | Full | Full | Full | The full path to the executable compiling the script. For compiled scripts, it's the path to the compiled executable. |
| A_AhkVersion | Full | Full | Full | Full | The version of the program used to compile the script. |
| A_AllowMainWindow | Full | Full | Full | Full | Built-in variable. |
| A_AllowTimers | Full | Full | Full | Full | Gets/sets whether timers are allowed to run. |
| A_AppData | Full | Full | Full | Full | Built-in variable. |
| A_AppDataCommon | Full | Full | Full | Full | Built-in variable. |
| A_Args | Full | Full | Full | Full | Built-in variable containing command-line arguments passed to the script. |
| A_Clipboard | Full | Full | Full | Full | A_Clipboard is a built-in variable that reflects the current contents of the Windows clipboard. |
| A_ClipboardTimeout | Full | Full | Full | Full | Gets/sets clipboard operation timeout used by Keysharp. |
| A_CommandLine | Full | Full | Full | Full | Gets the current script command line. |
| A_ComputerName | Full | Full | Full | Full | Built-in variable. |
| A_ComSpec | Full | Full | Full | Full | Built-in variable. |
| A_ControlDelay | Full | Full | Full | Full | Sets or returns the delay in milliseconds that will occur after each control-modifying command. |
| A_CoordModeCaret | Full | Full | Full | Full | Gets the coordinate mode for caret operations to be relative to either the active window, the client area of the active window, or the screen. |
| A_CoordModeMenu | Full | Full | Full | Full | Gets the coordinate mode for menus to be relative to either the active window, the client area of the active window, or the screen. |
| A_CoordModeMouse | Full | Full | Full | Full | Gets the coordinate mode for mouse operations to be relative to either the active window, the client area of the active window, or the screen. |
| A_CoordModePixel | Full | Full | Full | Full | Gets the coordinate mode for pixel operations to be relative to either the active window, the client area of the active window, or the screen. |
| A_CoordModeToolTip | Full | Full | Full | Full | Gets the coordinate mode for tool tips to be relative to either the active window, the client area of the active window, or the screen. |
| A_Cursor | Full | Full | Full | Full | Built-in variable. |
| A_DD | Full | Full | Full | Full | The two digit day of month from 1 - 31. Same as A_Mday. |
| A_DDD | Full | Full | Full | Full | The current day of the week's abbreviated name in the user's current culture language. |
| A_DDDD | Full | Full | Full | Full | The current day of the week's full name in the user's current culture language. |
| A_DefaultHotstringCaseSensitive | Full | Full | Full | Full | Gets/sets default option used for newly created hotstrings. |
| A_DefaultHotstringConformToCase | Full | Full | Full | Full | Gets/sets default option used for newly created hotstrings. |
| A_DefaultHotstringDetectWhenInsideWord | Full | Full | Full | Full | Gets/sets default option used for newly created hotstrings. |
| A_DefaultHotstringDoBackspace | Full | Full | Full | Full | Gets/sets default option used for newly created hotstrings. |
| A_DefaultHotstringDoReset | Full | Full | Full | Full | Gets/sets default option used for newly created hotstrings. |
| A_DefaultHotstringEndCharRequired | Full | Full | Full | Full | Gets/sets default option used for newly created hotstrings. |
| A_DefaultHotstringEndChars | Full | Full | Full | Full | Gets/sets default option used for newly created hotstrings. |
| A_DefaultHotstringKeyDelay | Full | Full | Full | Full | Gets/sets default option used for newly created hotstrings. |
| A_DefaultHotstringNoMouse | Full | Full | Full | Full | Gets/sets default option used for newly created hotstrings. |
| A_DefaultHotstringOmitEndChar | Full | Full | Full | Full | Gets/sets default option used for newly created hotstrings. |
| A_DefaultHotstringPriority | Full | Full | Full | Full | Gets/sets default option used for newly created hotstrings. |
| A_DefaultHotstringSendMode | Full | Full | Full | Full | Gets/sets default option used for newly created hotstrings. |
| A_DefaultHotstringSendRaw | Full | Full | Full | Full | Gets/sets default option used for newly created hotstrings. |
| A_DefaultMouseSpeed | Full | Full | Full | Full | Sets or returns the mouse speed that will be used if unspecified in Click and MouseMove/Click/Drag. |
| A_Desktop | Full | Full | Full | Full | Built-in variable. |
| A_DesktopCommon | Full | Full | Full | Full | Built-in variable. |
| A_DetectHiddenText | Full | Full | Full | Full | Toggles whether window text searchign includes hidden text. |
| A_DetectHiddenWindows | Full | Full | Full | Full | Toggles whether window searching includes hidden windows. |
| A_DirSeparator | Full | Full | Full | Full | Gets the current platform directory separator character. |
| A_EndChar | Full | Full | Full | Full | Built-in variable. |
| A_EventInfo | Full | Full | Full | Full | Built-in variable. |
| A_ExitReason | Full | Full | Full | Full | Always null until the main form is closing, in which case the value will be "OnExit()". |
| A_FileEncoding | Full | Full | Full | Full | Sets or returns the encoding used for reading and writing files. This differs from AHK in that it only supports ASCII (ascii), UTF-8 (utf-8/utf8-raw) or Unicode (utf-16/utf16-raw or unicode). ASCII will always return us-ascii because that is the name of the encoding in C#. |
| A_HasExited | Full | Full | Full | Full | Returns whether script termination has been requested/completed. |
| A_HotIf | Full | Full | Full | Full | Built-in variable. |
| A_HotkeyInterval | Full | Full | Full | Full | Built-in variable. |
| A_HotkeyModifierTimeout | Full | Full | Full | Full | Built-in variable. |
| A_Hour | Full | Full | Full | Full | The current 2 digit hour 00 - 23. |
| A_IconFile | Full | Full | Full | Full | Blank unless a custom tray icon has been specified via Menu, tray, icon, in which case it's the full path and name of the icon's file. |
| A_IconHidden | Full | Full | Full | Full | Gets whether the system tray icon is hidden. 1 for hidden, 0 for visible. |
| A_IconNumber | Full | Full | Full | Full | If A_IconFile has been specified, gets the number of the icon of the icon file used for the system tray icon. Otherwise blank. |
| A_IconTip | Full | Full | Full | Full | Sets or returns the tool tip text of the system tray icon. |
| A_Index | Full | Full | Full | Full | Built-in variable. |
| A_InitialWorkingDir | Full | Full | Full | Full | Built-in variable. |
| A_Is64bitOS | Full | Full | Full | Full | Built-in variable. |
| A_IsAdmin | Full | Full | Full | Full | Built-in variable. |
| A_IsCompiled | Full | Full | Full | Full | True if the program is running as a compiled executable, else false if it's running as a script passed to Keysharp.exe. |
| A_IsCritical | Full | Full | Full | Full | Returns 1 if the script is in critical priority mode, else 0. |
| A_IsPaused | Full | Full | Full | Full | Built-in variable indicating whether the current script/thread is paused. |
| A_IsSuspended | Full | Full | Full | Full | Returns 1 if the script is suspended, else 0. |
| A_IsUnicode | Full | Full | Full | Full | Whether the program uses unicode strings. Always returns true because C# programs are always unicode. |
| A_KeybdHookInstalled | Full | Full | Full | Full | Built-in variable. |
| A_KeyDelay | Full | Full | Full | Full | Sets or returns the delay that will occur after each keystroke sent by Send and ControlSend. |
| A_KeyDelayPlay | Full | Full | Full | Full | Sets or returns the delay that will occur after each keystroke sent by Send and ControlSend in SendPlay mode. |
| A_KeyDuration | Full | Full | Full | Full | Sets or returns the delay that will occur between the key down and key up events of each keystroke sent by Send and ControlSend. |
| A_KeyDurationPlay | Full | Full | Full | Full | Sets or returns the delay that will occur between the key down and key up events of each keystroke sent by Send and ControlSend in SendPlay mode. |
| A_KeysharpCorePath | Full | Full | Full | Full | Gets the path to Keysharp.Core. |
| A_KsVersion | Full | Full | Full | Full | Gets the Keysharp runtime version. |
| A_Language | Full | Full | Full | Full | Built-in variable. |
| A_LastError | Full | Full | Full | Full | Built-in variable. |
| A_LineFile | Full | Full | Full | Full | The full path and name of the file to which A_LineNumber belongs, which will be the same as A_ScriptFullPath unless the line belongs to one of a non-compiled script's #Include files. |
| A_LineNumber | Full | Full | Full | Full | The exact line number in the script, including comment lines. |
| A_ListLines | Full | Full | Full | Full | Built-in variable controlling/listing script line logging behavior. |
| A_LoopField | Full | Full | Full | Full | Built-in variable. |
| A_LoopFileAttrib | Full | Full | Full | Full | The attributes of the file currently retrieved as a string with one character for each attribute present. |
| A_LoopFileDir | Full | Full | Full | Full | The path of the directory in which A_LoopFileName resides. If FilePattern contains a relative path rather than an absolute path, the path here will also be relative. A root directory will not contain a trailing backslash. For example: C: |
| A_LoopFileExt | Full | Full | Full | Full | The file's extension (e.g. TXT, DOC, or EXE). The period (.) is not included. |
| A_LoopFileFullPath | Full | Full | Full | Full | This is different than A_LoopFilePath in the following ways: 1) It always contains the absolute/complete path of the file even if FilePattern contains a relative path; 2) Any short (8.3) folder names in FilePattern itself are converted to their long names; 3) Characters in FilePattern are converted to uppercase or lowercase to match the case stored in the file system. This is useful for converting file names -- such as those passed into a script as command line parameters -- to their exact path names as shown by Explorer. |
| A_LoopFileLongPath | Full | Full | Full | Full | A synonym for A_LoopFileFullPath. |
| A_LoopFileName | Full | Full | Full | Full | The name of the file or folder currently retrieved (without the path). |
| A_LoopFilePath | Full | Full | Full | Full | The path and name of the file/folder currently retrieved. If FilePattern contains a relative path rather than an absolute path, the path here will also be relative. Short file names are not used. |
| A_LoopFileShortName | Full | Full | Full | Full | The 8.3 short name, or alternate name of the file. If the file doesn't have one, A_LoopFileName will be retrieved instead. |
| A_LoopFileShortPath | Full | Full | Full | Full | The 8.3 short path and name of the file/folder currently retrieved. |
| A_LoopFileSize | Full | Full | Full | Full | The size in bytes of the file currently retrieved. Files larger than 4 gigabytes are also supported. |
| A_LoopFileSizeKB | Full | Full | Full | Full | The size in Kbytes of the file currently retrieved, rounded down to the nearest integer. |
| A_LoopFileSizeMB | Full | Full | Full | Full | The size in Mbytes of the file currently retrieved, rounded down to the nearest integer. |
| A_LoopFileTimeAccessed | Full | Full | Full | Full | The time the file was last accessed. Format YYYYMMDDHH24MISS. |
| A_LoopFileTimeCreated | Full | Full | Full | Full | The time the file was created. Format YYYYMMDDHH24MISS. |
| A_LoopFileTimeModified | Full | Full | Full | Full | The time the file was last modified. Format YYYYMMDDHH24MISS. |
| A_LoopReadLine | Full | Full | Full | Full | Built-in variable. |
| A_LoopRegKey | Full | Full | Full | Full | Built-in variable. |
| A_LoopRegName | Full | Full | Full | Full | Built-in variable. |
| A_LoopRegTimeModified | Full | Full | Full | Full | Built-in variable. |
| A_LoopRegType | Full | Full | Full | Full | Built-in variable. |
| A_LoopRegValue | Full | Full | Full | Full | A new property to get the value of a registry item when using Loop Reg, which is more succinct than typing Value:= RegRead(). |
| A_MaxHotkeysPerInterval | Full | Full | Full | Full | Built-in variable. |
| A_MaxThreads | Full | Full | Full | Full | Gets/sets the script-wide max thread count setting. |
| A_MDay | Full | Full | Full | Full | Built-in variable. |
| A_MenuMaskKey | Full | Full | Full | Full | Built-in variable. |
| A_Min | Full | Full | Full | Full | The current 2 digit minute 00- 23. |
| A_MM | Full | Full | Full | Full | The two digit month from 1 - 12. Same as A_Month. |
| A_MMM | Full | Full | Full | Full | The current month's abbreviated text name in the user's current culture language. |
| A_MMMM | Full | Full | Full | Full | The current month's full text name in the user's current culture language. |
| A_Mon | Full | Full | Full | Full | Built-in variable. |
| A_MouseDelay | Full | Full | Full | Full | Sets or returns the delay that will occur after each mouse movement or click. |
| A_MouseDelayPlay | Full | Full | Full | Full | Sets or returns the delay that will occur after each mouse movement or click in SendPlay mode. |
| A_MouseHookInstalled | Full | Full | Full | Full | Built-in variable. |
| A_MSec | Full | Full | Full | Full | Current 3 digit millisecond 000 - 999. |
| A_MyDocuments | Full | Full | Full | Full | Built-in variable. |
| A_NoTrayIcon | Full | Full | Full | Full | Gets/sets whether the tray icon is hidden. |
| A_Now | Full | Full | Full | Full | The current local time in YYYYMMDDHH24MISS format. |
| A_NowMs | Full | Full | Full | Full | Gets current local timestamp including milliseconds. |
| A_NowUTC | Full | Full | Full | Full | The current Coordinated Universal Time (UTC) in YYYYMMDDHH24MISS format. |
| A_NowUtcMs | Full | Full | Full | Full | Gets current UTC timestamp including milliseconds. |
| A_OSVersion | Full | Full | Full | Full | Built-in variable. |
| A_PriorHotkey | Full | Full | Full | Full | Built-in variable. |
| A_PriorKey | Full | Full | Full | Full | Built-in variable. |
| A_ProgramFiles | Full | Full | Full | Full | Built-in variable. |
| A_Programs | Full | Full | Full | Full | Built-in variable. |
| A_ProgramsCommon | Full | Full | Full | Full | Built-in variable. |
| A_PtrSize | Full | Full | Full | Full | Built-in variable. |
| A_RegView | Full | Full | Full | Full | Gets whether the registry is in 32 or 64 bit mode. |
| A_ScreenDPI | Full | Full | Full | Full | Built-in variable. |
| A_ScreenHeight | Full | Full | Full | Full | Built-in variable. |
| A_ScreenWidth | Full | Full | Full | Full | Built-in variable. |
| A_ScriptDir | Full | Full | Full | Full | The full path of the script being compiled and ran, without a trailing backslash. Evaluates to a constant string in the C# code output. |
| A_ScriptFullPath | Full | Full | Full | Full | The full path of the script being compiled and ran. Evaluates to a constant string in the C# code output. |
| A_ScriptHwnd | Full | Full | Full | Full | The handle to the main window, as an int64, if it exists, else 0. |
| A_ScriptName | Full | Full | Full | Full | The name with extension, but without the path, of the script being compiled and ran. Evaluates to a constant string in the C# code output. |
| A_Sec | Full | Full | Full | Full | The current 2 digit second 00 - 23. |
| A_SendLevel | Full | Full | Full | Full | Controls which artificial keyboard and mouse events are ignored by hotkeys and hotstrings. |
| A_SendMode | Full | Full | Full | Full | Unsure at the moment because send modes aren't quite clear. |
| A_Space | Full | Full | Full | Full | String containing a single space. |
| A_StartMenu | Full | Full | Full | Full | Built-in variable. |
| A_StartMenuCommon | Full | Full | Full | Full | Built-in variable. |
| A_Startup | Full | Full | Full | Full | Built-in variable. |
| A_StartupCommon | Full | Full | Full | Full | Built-in variable. |
| A_StoreCapsLockMode | Full | Full | Full | Full | Toggles whether the state of CapsLock is restored after a Send. |
| A_SuspendExempt | Full | Full | Full | Full | Gets/sets whether current thread is exempt from Suspend. |
| A_Tab | Full | Full | Full | Full | String containing a single tab. |
| A_Temp | Full | Full | Full | Full | Built-in variable. |
| A_ThisFunc | Full | Full | Full | Full | The name of the function. If called outside of a function, empty string is returned. |
| A_ThisHotkey | Full | Full | Full | Full | Built-in variable. |
| A_TickCount | Full | Full | Full | Full | The number of milliseconds since the system started. Note this is not limited to 49.7 days like AHK because it uses a long integer. |
| A_TimeIdle | Full | Full | Full | Full | Built-in variable. |
| A_TimeIdleKeyboard | Full | Full | Full | Full | Built-in variable. |
| A_TimeIdleMouse | Full | Full | Full | Full | Built-in variable. |
| A_TimeIdlePhysical | Full | Full | Full | Full | Built-in variable. |
| A_Timers | Full | Full | Full | Full | Gets active timers as a map of callback -> enabled state. |
| A_TimeSincePriorHotkey | Full | Full | Full | Full | Built-in variable. |
| A_TimeSinceThisHotkey | Full | Full | Full | Full | Built-in variable. |
| A_TitleMatchMode | Full | Full | Full | Full | Sets or returns 1 for matching the start of a title, 2 for matching anywhere in a title, 3 for matching exactly a title, or "regex" for matching using a regular expression. |
| A_TitleMatchModeSpeed | Full | Full | Full | Full | Sets or returns "fast" for fast window title matching, or "slow" for slow window title matching. |
| A_TotalScreenHeight | Full | Full | Full | Full | Gets total virtual screen height across monitors. |
| A_TotalScreenWidth | Full | Full | Full | Full | Gets total virtual screen width across monitors. |
| A_TrayMenu | Full | Full | Full | Full | Built-in variable. |
| A_UseHook | Full | Full | Full | Full | Gets/sets whether keyboard hook usage is forced. |
| A_UserName | Full | Full | Full | Full | Built-in variable. |
| A_WDay | Full | Full | Full | Full | The current 1 digit day of the week. |
| A_WinActivateForce | Full | Full | Full | Full | Gets/sets whether window activation is forced. |
| A_WinDelay | Full | Full | Full | Full | Sets or returns the delay that will occur after each windowing command, such as WinActivate. |
| A_WinDir | Full | Full | Full | Full | Built-in variable. |
| A_WorkAreaHeight | Full | Full | Full | Full | Gets primary work area height. |
| A_WorkAreaWidth | Full | Full | Full | Full | Gets primary work area width. |
| A_WorkingDir | Full | Full | Full | Full | The full path of the working folder of the executable compiling and running the script. |
| A_YDay | Full | Full | Full | Full | The current 1-366 day of the year. |
| A_Year | Full | Full | Full | Full | Built-in variable. |
| A_YWeek | Full | Full | Full | Full | The current year and week number expressed as a string containing a 4 digit year and 2 digit week. |
| A_YYYY | Full | Full | Full | Full | The four digit year. Same as A_Year. |
| Abs() | Full | Full | Full | Full | Computes the absolute value. |
| Acos() | Full | Full | Full | Full | Computes the arc cosine. Throws an exception if the argument value is not between -1 and 1. |
| AES() | Full | Full | Full | Full | Encrypts/decrypts data using AES. |
| and | Full | Full | Full | Full | Logical AND operator. |
| Any | Full | Full | Full | Full | Special type value that can match any type. |
| Array | Full | Full | Full | Full | Ordered collection object. |
| Array.__Enum() | Full | Full | Full | Full | Enumerates array elements. |
| Array.__Item | Full | Full | Full | Full | Indexer property for getting or setting array elements. |
| Array.__New() | Full | Full | Full | Full | Constructs a new Array object. |
| Array.AddRange() | Full | Full | Full | Full | Keysharp-specific Array method. |
| Array.Capacity | Full | Full | Full | Full | Gets or sets the reserved element capacity. |
| Array.Clear() | Full | Full | Full | Full | Clears all elements in an array. |
| Array.Clone() | Full | Full | Full | Full | Returns a shallow copy of an array. |
| Array.Contains() | Full | Full | Full | Full | Keysharp-specific Array method. |
| Array.Default | Full | Full | Full | Full | Default value returned for missing indexes. |
| Array.Delete() | Full | Full | Full | Full | Sets the element at the specified index to null, returns the element at that index before it was cleared. |
| Array.Filter() | Full | Full | Full | Full | Returns a new array containing elements accepted by a callback predicate. |
| Array.FindIndex() | Full | Full | Full | Full | Returns the index of the first element matching a callback predicate. |
| Array.Get() | Full | Full | Full | Full | Gets the value at an index with optional fallback default. |
| Array.Has() | Full | Full | Full | Full | Returns whether an array contains a non-empty value at the given index. |
| Array.IndexOf() | Full | Full | Full | Full | Returns the index of the first occurrence of a value. |
| Array.InsertAt() | Full | Full | Full | Full | Inserts an element or range of elements at a given index. |
| Array.Join() | Full | Full | Full | Full | Joins array elements into a string with a separator. |
| Array.Length | Full | Full | Full | Full | Gets/sets the logical length of the array. |
| Array.MapTo() | Full | Full | Full | Full | Returns a new array transformed by a callback. |
| Array.MaxIndex() | Full | Full | Full | Full | Returns the largest integer contained in the array. Returns empty string if no integers are present. |
| Array.MinIndex() | Full | Full | Full | Full | Returns the smallest integer contained in the array. Returns empty string if no integers are present. |
| Array.Pop() | Full | Full | Full | Full | Removes and returns the last element of an array. An exception is thrown if the array was empty. |
| Array.Push() | Full | Full | Full | Full | Appends values to the end of an array. |
| Array.Remove() | Full | Full | Full | Full | Keysharp-specific Array method. |
| Array.RemoveAt() | Full | Full | Full | Full | Removes the element at a given index, plus optionally a length. Returns the removed item if no length was specified. Returns the null if a length was specified. |
| Array.Sort() | Full | Full | Full | Full | Sorts array elements, optionally using a custom comparer callback. |
| Asin() | Full | Full | Full | Full | Computes the arc sine. Throws an exception if the argument value is not between -1 and 1. |
| Atan() | Full | Full | Full | Full | Computes the arc tangent. |
| ATan2() | Full | Full | Full | Full | Computes the arc tangent by using two numbers. |
| Base | Full | Full | Unknown | Unknown | Retrieves the value's base object. |
| Base64Decode() | Full | Full | Full | Full | Decodes a Base64 string to binary data. |
| Base64Encode() | Full | Full | Full | Full | Encodes binary data to a Base64 string. |
| BlockInput() | Full | Partial | Unknown | Unknown | The BlockInput function disables or enables the user's ability to interact with the computer via keyboard and mouse. |
| Break | Full | Full | Full | Full | Exits the current loop. |
| Buffer() | Full | Full | Unknown | Unknown | The Buffer object encapsulates a block of memory for use with advanced techniques such as DllCall, structures, StrPut and raw file I/O. |
| Buffer.__Item[] | Full | Full | Full | Full | Indexer for reading/writing bytes in Buffer by offset. |
| Buffer.__New() | Full | Full | Full | Full | Constructs a new Buffer object. |
| Buffer.ToBase64() | Full | Full | Full | Full | Returns Buffer contents as a Base64 string. |
| Buffer.ToByteArray() | Full | Full | Full | Full | Returns Buffer contents as a byte array. |
| Buffer.ToHex() | Full | Full | Full | Full | Returns Buffer contents as a hexadecimal string. |
| CallbackCreate() | Full | Full | Unknown | Unknown | The CallbackCreate function creates a machine-code address that when called, redirects the call to a function in the script. |
| CallbackFree() | Full | Full | Unknown | Unknown | The CallbackCreate function creates a machine-code address that when called, redirects the call to a function in the script. |
| CaretGetPos() | Full | Partial | Unknown | Unknown | The CaretGetPos function retrieves the current position of the caret (text insertion point). |
| Case | Full | Full | Full | Full | Case branch label used by switch. |
| Catch | Full | Full | Full | Full | Handles an exception thrown by try/throw. |
| Ceil() | Full | Full | Full | Full | Computes the ceiling value of a number, rounding away from zero for positive numbers, and toward zero for negative numbers. |
| Chr | Full | Full | Full | Full | Returns the string (usually a single character) corresponding to the character code indicated by the specified number. |
| Click() | Full | Partial | Unknown | Unknown | The Click function clicks a mouse button at the specified coordinates. It can also hold down a mouse button, turn the mouse wheel, or move the mouse. |
| Clipboard | Full | Partial | Unknown | Unknown | Clipboard functionality is implemented with platform-specific limitations outside Windows. |
| ClipboardAll() | Full | Partial | Unknown | Unknown | The ClipboardAll class facilitates saving and restoring everything on the clipboard (such as pictures and formatting). |
| ClipWait() | Full | Partial | Unknown | Unknown | The ClipWait function waits until the clipboard contains data. |
| Clr() | Partial | Partial | Unknown | Unknown | Creates/returns a CLR interop facade for loading and invoking .NET types. |
| Clr.GetNamespaceName() | Partial | Partial | Unknown | Unknown | Returns namespace name for a managed wrapper/type. |
| Clr.GetTypeName() | Partial | Partial | Unknown | Unknown | Returns type name for a managed wrapper/object. |
| Clr.Load() | Partial | Partial | Unknown | Unknown | Loads a managed assembly for CLR interop. |
| Clr.ManagedAssembly | Partial | Partial | Unknown | Unknown | Represents a loaded managed assembly wrapper. |
| Clr.ManagedInstance | Partial | Partial | Unknown | Unknown | Represents a managed object instance wrapper. |
| Clr.ManagedInstance.__Enum() | Partial | Partial | Unknown | Unknown | Enumerates members exposed by a managed instance wrapper. |
| Clr.ManagedNamespace | Partial | Partial | Unknown | Unknown | Represents a managed namespace wrapper used for type resolution. |
| Clr.Type() | Partial | Partial | Unknown | Unknown | Derived from current implementation (experimental Clr surface). |
| ClrManagedType | Partial | Partial | Unknown | Unknown | Wrapper describing a managed type for reflection/invocation. |
| Collect() | Full | Full | Full | Full | Forces garbage collection and finalizer processing. |
| COM APIs | Full | Unsupported | Unsupported | Unsupported | COM is available on Windows only. |
| ComCall() | Full | Unsupported | Unknown | Unknown | The ComCall function calls a native COM interface method by index. |
| ComObjActive() | Full | Unsupported | Unknown | Unknown | The ComObjActive function retrieves a registered COM object. |
| ComObjArray() | Full | Unsupported | Unknown | Unknown | The ComObjArray function creates a SafeArray for use with COM. |
| ComObjConnect() | Full | Unsupported | Unknown | Unknown | The ComObjConnect function connects a COM object's event source to the script, enabling events to be handled. |
| ComObject() | Full | Unsupported | Unknown | Unknown | The ComObject function creates a COM object. |
| ComObjFlags() | Full | Unsupported | Unknown | Unknown | The ComObjFlags function retrieves or changes flags which control a COM wrapper object's behaviour. |
| ComObjFromPtr() | Full | Unsupported | Unknown | Unknown | The ComObjFromPtr function wraps a raw IDispatch pointer (COM object) for use by the script. |
| ComObjGet() | Full | Unsupported | Unknown | Unknown | The ComObjGet function returns a reference to an object provided by a COM component. |
| ComObjQuery() | Full | Unsupported | Unknown | Unknown | The ComObjQuery function queries a COM object for an interface or service. |
| ComObjType() | Full | Unsupported | Unknown | Unknown | The ComObjType function retrieves type information from a COM object. |
| ComObjValue() | Full | Unsupported | Unknown | Unknown | The ComObjValue function retrieves the value or pointer stored in a COM wrapper object. |
| ComValue() | Full | Unsupported | Unknown | Unknown | The ComValue class wraps a value, SafeArray or COM object for use by the script or for passing to a COM method. |
| ComValueRef | Full | Unsupported | Unsupported | Unsupported | Reference wrapper type for COM values. |
| contains | Full | Full | Full | Full | Substring containment operator. |
| Continue | Full | Full | Full | Full | Skips to the next loop iteration. |
| ControlAddItem() | Full | Partial | Unknown | Unknown | The ControlAddItem function adds a new entry at the bottom of a list box, combo box, or drop-down list. |
| ControlChooseIndex() | Full | Partial | Unknown | Unknown | The ControlChooseIndex function selects an entry in a list box, combo box, or drop-down list, or a tab control page, by index. |
| ControlChooseString() | Full | Partial | Unknown | Unknown | The ControlChooseString function selects an entry in a list box, combo box, or drop-down list, or a tab control page, by string. |
| ControlClick() | Full | Partial | Unknown | Unknown | The ControlClick function sends a mouse button or mouse wheel event to a window or control. |
| ControlDeleteItem() | Full | Partial | Unknown | Unknown | The ControlDeleteItem function deletes an entry from a list box, combo box, or drop-down list by index. |
| ControlFindItem() | Full | Unsupported | Unknown | Unknown | The ControlFindItem function searches for an entry in a list box, combo box, or drop-down list by string, and returns its index. |
| ControlFocus() | Full | Unsupported | Unknown | Unknown | The ControlFocus function sets keyboard focus to a control. |
| ControlGetChecked() | Full | Unsupported | Unknown | Unknown | The ControlGetChecked function returns 1 if a check box or radio button is checked, or 0 if unchecked. |
| ControlGetChoice() | Full | Partial | Unknown | Unknown | The ControlGetChoice function returns the text of the currently selected entry in a list box, combo box, or drop-down list. |
| ControlGetClassNN() | Full | Partial | Unknown | Unknown | The ControlGetClassNN function returns the class ClassNN (class name and sequence number) of a control. |
| ControlGetEnabled() | Full | Partial | Unknown | Unknown | The ControlGetEnabled function returns 1 if a control is enabled, or 0 if disabled. |
| ControlGetExStyle() | Full | Partial | Unknown | Unknown | The ControlGetStyle and ControlGetExStyle functions return an integer representing the style or extended style of a control. |
| ControlGetFocus() | Full | Partial | Unknown | Unknown | The ControlGetFocus function retrieves which control of the target window has keyboard focus, if any. |
| ControlGetHwnd() | Full | Partial | Unknown | Unknown | The ControlGetHwnd function returns the window handle (HWND) of a control. |
| ControlGetIndex() | Full | Partial | Unknown | Unknown | The ControlGetIndex function returns the index of the currently selected entry in a list box, combo box, or drop-down list, or the index of the active page in a tab control. |
| ControlGetItems() | Full | Partial | Unknown | Unknown | The ControlGetItems function returns an array of entries from a list box, combo box, or drop-down list. |
| ControlGetPos() | Full | Partial | Unknown | Unknown | The ControlGetPos function retrieves the position and size of a control. |
| ControlGetStyle() | Full | Partial | Unknown | Unknown | The ControlGetStyle and ControlGetExStyle functions return an integer representing the style or extended style of a control. |
| ControlGetText() | Full | Partial | Unknown | Unknown | The ControlGetText function retrieves text from a control. |
| ControlGetVisible() | Full | Partial | Unknown | Unknown | The ControlGetVisible function returns 1 if a control is visible, or 0 if hidden. |
| ControlHide() | Full | Partial | Unknown | Unknown | The ControlHide function hides a control. |
| ControlHideDropDown() | Full | Partial | Unknown | Unknown | The ControlHideDropDown function hides the popup list of a combo box or drop-down list. |
| ControlMove() | Full | Partial | Unknown | Unknown | The ControlMove function moves and/or resizes a control. |
| ControlSend() | Full | Partial | Unknown | Unknown | The ControlSend and ControlSendText functions send simulated keystrokes or text to a window or control. |
| ControlSendText() | Full | Partial | Unknown | Unknown | The ControlSend and ControlSendText functions send simulated keystrokes or text to a window or control. |
| ControlSetChecked() | Full | Partial | Unknown | Unknown | The ControlSetChecked function checks or unchecks a check box or radio button. |
| ControlSetEnabled() | Full | Partial | Unknown | Unknown | The ControlSetEnabled function enables or disables a control. |
| ControlSetExStyle() | Full | Partial | Unknown | Unknown | The ControlSetStyle and ControlSetExStyle functions change the style or extended style of a control. |
| ControlSetStyle() | Full | Partial | Unknown | Unknown | The ControlSetStyle and ControlSetExStyle functions change the style or extended style of a control. |
| ControlSetText() | Full | Partial | Unknown | Unknown | The ControlSetText function changes the text of a control. |
| ControlShow() | Full | Partial | Unknown | Unknown | The ControlShow function shows a control if it was previously hidden. |
| ControlShowDropDown() | Full | Partial | Unknown | Unknown | The ControlShowDropDown function shows the popup list of a combo box or drop-down list. |
| CoordMode() | Full | Full | Unknown | Unknown | The CoordMode function sets coordinate mode for various built-in functions to be relative to either the active window or the screen. |
| CopyImageToClipboard() | Full | Partial | Unknown | Unknown | Built-in function. |
| Cos() | Full | Full | Full | Full | Computes the cosine of a number. |
| Cosh() | Full | Full | Full | Full | Computes the hyperbolic cosine of a number. |
| CRC32() | Full | Full | Full | Full | Computes CRC32 checksum for input data. |
| Critical() | Full | Full | Unknown | Unknown | The Critical statement prevents the current thread from being interrupted by other threads, or enables it to be interrupted. |
| Date Time Built-in Variables | Full | Full | Full | Full | Language/runtime capability. |
| DateAdd() | Full | Full | Full | Full | The DateAdd function adds or subtracts time from a date-time value. |
| DateDiff() | Full | Full | Full | Full | The DateDiff function compares two date-time values and returns the difference. |
| Default | Full | Full | Full | Full | Default branch label used by switch. |
| DetectHiddenText() | Full | Full | Unknown | Unknown | The DetectHiddenText function determines whether invisible text in a window is "seen" for the purpose of finding the window. |
| DetectHiddenWindows() | Full | Full | Unknown | Unknown | The DetectHiddenWindows function determines whether invisible windows are "seen" by the script. |
| DirCopy() | Full | Full | Unknown | Unknown | Copies a folder along with all its sub-folders and files (similar to xcopy). Platform statuses inherited from curated 'File and directory operations'; per-function validation pending. |
| DirCreate() | Full | Full | Unknown | Unknown | Creates a folder, and all of its parent folders if needed. Platform statuses inherited from curated 'File and directory operations'; per-function validation pending. |
| DirDelete() | Full | Full | Unknown | Unknown | Deletes a folder, optionally recursive. Platform statuses inherited from curated 'File and directory operations'; per-function validation pending. |
| Directives and preprocessing | Full | Full | Full | Full | OS-specific directives supported via compile constants. |
| DirExist() | Full | Full | Unknown | Unknown | Checks for the existence of a folder and returns its attributes. Platform statuses inherited from curated 'File and directory operations'; per-function validation pending. |
| DirMove() | Full | Full | Unknown | Unknown | Moves a folder along with all its sub-folders and files. It can also rename a folder. Platform statuses inherited from curated 'File and directory operations'; per-function validation pending. |
| DirSelect() | Full | Full | Unknown | Unknown | Displays a standard dialog that allows the user to select a folder. Differs in that it does not support folder access locking, selecting a folder in the tree, showing an edit box because the user can just type in the combo box, option 7, or hiding the New Folder button. Platform statuses inherited from curated 'File and directory operations'; per-function validation pending. |
| DllCall() | Full | Full | Unknown | Unknown | The DllCall function calls a function inside a DLL, such as a standard Windows API function. |
| Download() | Full | Full | Unknown | Unknown | The Download function downloads a file from the Internet. |
| DriveEject() | Full | Full | Unknown | Unknown | Ejects or retracts the tray of the specified CD/DVD drive. |
| DriveGetCapacity() | Full | Full | Unknown | Unknown | Returns the total capacity of the drive which contains the specified path, in megabytes. |
| DriveGetFileSystem() | Full | Full | Unknown | Unknown | Returns the type of the specified drive's file system. |
| DriveGetLabel() | Full | Full | Unknown | Unknown | Returns the volume label of the specified drive. |
| DriveGetList() | Full | Full | Unknown | Unknown | Returns a string of letters, one character for each drive letter in the system. |
| DriveGetSerial() | Full | Full | Unknown | Unknown | Returns the volume serial number of the specified drive. |
| DriveGetSpaceFree() | Full | Full | Unknown | Unknown | The DriveGetSpaceFree function returns the free disk space of the drive which contains the specified path, in megabytes. |
| DriveGetStatus() | Full | Full | Unknown | Unknown | Returns the status of the drive which contains the specified path. |
| DriveGetStatusCD() | Full | Full | Unknown | Unknown | Returns the media status of the specified CD/DVD drive. |
| DriveGetType() | Full | Full | Unknown | Unknown | Returns the type of the drive which contains the specified path. |
| DriveLock() | Full | Full | Unknown | Unknown | Prevents the eject feature of the specified drive from working. |
| DriveRetract() | Full | Full | Unknown | Unknown | The DriveEject and DriveRetract functions eject or retract the tray of the specified CD/DVD drive. DriveEject can also eject a removable drive. |
| DriveSetLabel() | Full | Full | Unknown | Unknown | Changes the volume label of the specified drive. |
| DriveUnlock() | Full | Full | Unknown | Unknown | Restores the eject feature of the specified drive. |
| Edit() | Full | Full | Full | Full | Opens text in the default editor. |
| EditGetCurrentCol() | Full | Full | Full | Full | The EditGetCurrentCol function returns the column number in an edit control where the caret resides. |
| EditGetCurrentLine() | Full | Full | Full | Full | The EditGetCurrentLine function returns the line number in an edit control where the caret resides. |
| EditGetLine() | Full | Full | Full | Full | The EditGetLine function returns the text of a line in an edit control by line number. |
| EditGetLineCount() | Full | Full | Full | Full | The EditGetLineCount function returns the number of lines in an edit control. |
| EditGetSelectedText() | Full | Full | Full | Full | The EditGetSelectedText function returns the selected text in an edit control. |
| EditPaste() | Full | Full | Full | Full | The EditPaste function pastes a string at the caret in an edit control. |
| Else | Full | Full | Full | Full | Alternate branch executed when if condition is false. |
| Enumerator | Full | Full | Full | Full | Enumerator object used for iteration. |
| EnvGet() | Full | Full | Unknown | Unknown | Returns the value of the specified environment variable if it exists, else it returns an empty string. |
| EnvSet() | Full | Full | Unknown | Unknown | Sets the specified environment variable to the specified value. Using a value of null deletes the variable. |
| Error | Full | Full | Full | Full | Built-in error class. |
| Exit | Full | Full | Full | Full | Exits the current thread/subroutine. |
| ExitApp() | Full | Full | Unknown | Unknown | The ExitApp function terminates the script. |
| Exp() | Full | Full | Full | Full | Computes e raised to the nth power. |
| Export | Full | Full | Full | Full | An Export declaration marks a function, class or variable for wildcard import, and optionally marks it as the default export. |
| extends | Full | Full | Full | Full | Keyword used to derive a class from a base class. |
| False | Full | Full | Full | Full | Boolean false constant. |
| File | Full | Full | Full | Full | File object type. |
| File and directory operations | Full | Full | Unknown | Unknown | macOS recycle/trash and privacy-scoped file access still evolving. |
| FileAppend() | Full | Full | Unknown | Unknown | Writes text or binary data to the end of a file (first creating the file, if necessary). Platform statuses inherited from curated 'File and directory operations'; per-function validation pending. |
| FileCopy() | Full | Full | Unknown | Unknown | Copies one or more files. Platform statuses inherited from curated 'File and directory operations'; per-function validation pending. |
| FileCreateShortcut() | Full | Partial | Unknown | Unknown | Creates a shortcut (.lnk) file. Platform statuses inherited from curated 'File and directory operations'; per-function validation pending. Shortcut implementations are platform-specific and not fully parity-validated. |
| FileDelete() | Full | Full | Unknown | Unknown | Deletes one or more files. Platform statuses inherited from curated 'File and directory operations'; per-function validation pending. |
| FileDirName() | Full | Full | Full | Full | Returns directory portion of a file path. |
| FileEncoding() | Full | Full | Unknown | Unknown | Sets the default encoding for FileRead, Loop Read, FileAppend, and FileOpen. Platform statuses inherited from curated 'File and directory operations'; per-function validation pending. |
| FileExist() | Full | Full | Unknown | Unknown | Checks for the existence of a file or folder and returns its attributes. Platform statuses inherited from curated 'File and directory operations'; per-function validation pending. |
| FileFullPath() | Full | Full | Full | Full | Returns absolute normalized full path. |
| FileGetAttrib() | Full | Full | Unknown | Unknown | Reports whether a file or folder is read-only, hidden, etc. Platform statuses inherited from curated 'File and directory operations'; per-function validation pending. |
| FileGetShortcut() | Full | Partial | Unknown | Unknown | Retrieves information about a shortcut (.lnk) file, such as its target file. Platform statuses inherited from curated 'File and directory operations'; per-function validation pending. Shortcut metadata extraction differs by platform format and backend. |
| FileGetSize() | Full | Full | Unknown | Unknown | Retrieves the size of a file. Also allows for passing "t" to return the size in terms of terrabytes. Platform statuses inherited from curated 'File and directory operations'; per-function validation pending. |
| FileGetTime() | Full | Full | Unknown | Unknown | Retrieves the datetime stamp of a file or folder. Platform statuses inherited from curated 'File and directory operations'; per-function validation pending. |
| FileGetVersion() | Full | Partial | Unknown | Unknown | Retrieves the version of a file. Platform statuses inherited from curated 'File and directory operations'; per-function validation pending. Version metadata availability differs by platform file formats. |
| FileInstall() | Unsupported | Unsupported | Unknown | Unknown | All scripts are converted into compiled executables, so this doesn't apply. Platform statuses inherited from curated 'File and directory operations'; per-function validation pending. |
| FileMove() | Full | Full | Unknown | Unknown | Moves or renames one or more files. Platform statuses inherited from curated 'File and directory operations'; per-function validation pending. |
| FileOpen() | Full | Full | Unknown | Unknown | Platform statuses inherited from curated 'File and directory operations'; per-function validation pending. |
| FileRead() | Full | Full | Unknown | Unknown | Retrieves the contents of a file. Platform statuses inherited from curated 'File and directory operations'; per-function validation pending. |
| FileRecycle() | Full | Partial | Unknown | Unknown | Sends a file or directory to the recycle bin if possible, or permanently deletes it. Platform statuses inherited from curated 'File and directory operations'; per-function validation pending. Recycle/Trash behavior depends on desktop APIs and permission context. |
| FileRecycleEmpty() | Full | Partial | Unknown | Unknown | Empties the recycle bin. Platform statuses inherited from curated 'File and directory operations'; per-function validation pending. Recycle/Trash behavior depends on desktop APIs and permission context. |
| FileSelect() | Full | Full | Unknown | Unknown | Displays a standard dialog that allows the user to open or save file(s). Platform statuses inherited from curated 'File and directory operations'; per-function validation pending. |
| FileSetAttrib() | Full | Full | Unknown | Unknown | Changes the attributes of one or more files or folders. Wildcards are supported. Platform statuses inherited from curated 'File and directory operations'; per-function validation pending. |
| FileSetTime() | Full | Full | Unknown | Unknown | Changes the datetime stamp of one or more files or folders. Wildcards are supported. Platform statuses inherited from curated 'File and directory operations'; per-function validation pending. |
| Finally | Full | Full | Full | Full | Runs after try/catch regardless of whether an exception occurred. |
| Float() | Full | Full | Full | Full | The Float function converts a numeric string or integer value to a floating-point number. |
| Floor() | Full | Full | Full | Full | Computes a number rounded down to the nearest integer. Rounds toward zero for positive numbers and away from zero for negative numbers. |
| For | Full | Full | Full | Full | Iterates over enumerable values or key/value pairs. |
| Foreign window management (non-Keysharp apps) | Full | Partial | Unknown | Unknown | On Linux, Control* functions are not supported for foreign apps; use the included AtSpi library for cross-process window/control interaction. macOS currently relies on Accessibility APIs with permission requirements. |
| Format() | Full | Full | Full | Full | Formats text by substituting placeholders with argument values. |
| FormatCs() | Full | Full | Full | Full | Formats text using C#-style format placeholders (1-based indexing adaptation). |
| FormatTime | Full | Full | Full | Full | Formats a datetime string according to the parameters. All C# formatting options are supported. Supports all V2 functionality except for the Dn and Tn options. If you want to specify a specific format, do it in the format parameter. |
| Func | Full | Full | Full | Full | Function object type. |
| GetKeyName() | Full | Partial | Unknown | Unknown | The GetKeyName function retrieves the name/text of a key. |
| GetKeySC() | Full | Partial | Unknown | Unknown | The GetKeySC function retrieves the scan code of a key. |
| GetKeyState() | Full | Partial | Unknown | Unknown | The GetKeyState function returns 1 (true) or 0 (false) depending on whether the specified keyboard key or mouse/controller button is down or up. Also retrieves controller status. |
| GetKeyVK() | Full | Partial | Unknown | Unknown | The GetKeyVK function retrieves the virtual key code of a key. |
| GetMethod() | Full | Full | Full | Full | The GetMethod function retrieves the implementation function of a method. |
| Global keyboard hooks | Full | Partial | Unknown | Unknown | Linux uses SharpHook/X11 behavior; macOS behavior is still being aligned. |
| Global mouse hooks | Full | Partial | Unknown | Unknown | Suppression/injection semantics differ by platform. |
| Goto | Partial | Partial | Unknown | Unknown | Goto doesn't support expressions in Keysharp. |
| GroupActivate() | Full | Partial | Unknown | Unknown | The GroupActivate function activates the next window in a window group that was defined with the GroupAdd function. |
| GroupAdd() | Full | Partial | Unknown | Unknown | The GroupAdd function adds a window specification to a window group, creating the group if necessary. |
| GroupClose() | Full | Partial | Unknown | Unknown | The GroupClose function closes the active window if it was just activated by the GroupActivate or GroupDeactivate function. |
| GroupDeactivate() | Full | Partial | Unknown | Unknown | The GroupDeactivate function is similar to the GroupActivate function but activates the next window not in the group. |
| Gui control types | Full | Partial | Unknown | Unknown | GUI control types are elements of interaction which can be added to a GUI window using the Gui object's Add method. |
| Gui() | Full | Partial | Unknown | Unknown | The Gui object provides an interface to create a window, add controls, modify the window, and retrieve information about the window. Such windows can be used as data entry forms or custom user interfaces. |
| Gui.__Enum() | Full | Partial | Unknown | Unknown | Returns an enumerator for GUI controls. |
| Gui.__Item | Full | Partial | Unknown | Unknown | Indexer property for retrieving controls by name or key. |
| Gui.__New() | Full | Partial | Unknown | Unknown | Constructs a new GUI window object. |
| Gui.Add() | Full | Partial | Unknown | Unknown | Adds a control to the GUI. |
| Gui.BackColor | Full | Partial | Unknown | Unknown | Gets or sets the GUI background color. |
| Gui.Call() | Full | Partial | Unknown | Unknown | Shows the GUI when the object is called like a function. |
| Gui.Control.Add() | Full | Partial | Unknown | Unknown | Adds an item to controls that support item lists. |
| Gui.Control.Choose() | Full | Partial | Unknown | Unknown | Selects an item in the control. |
| Gui.Control.ClassNN | Full | Partial | Unknown | Unknown | ClassNN identifier of the control. |
| Gui.Control.Delete() | Full | Partial | Unknown | Unknown | Deletes items from controls that support item lists. |
| Gui.Control.Enabled | Full | Partial | Unknown | Unknown | Gets or sets whether the control is enabled. |
| Gui.Control.Focus() | Full | Partial | Unknown | Unknown | Sets keyboard focus to the control. |
| Gui.Control.Focused | Full | Partial | Unknown | Unknown | Whether the control currently has focus. |
| Gui.Control.GetPos() | Full | Partial | Unknown | Unknown | Gets the control position and size. |
| Gui.Control.Gui | Full | Partial | Unknown | Unknown | Parent GUI object for the control. |
| Gui.Control.Hwnd | Full | Partial | Unknown | Unknown | Native window handle of the control. |
| Gui.Control.Move() | Full | Partial | Unknown | Unknown | Moves or resizes the control. |
| Gui.Control.Name | Full | Partial | Unknown | Unknown | Associated control name. |
| Gui.Control.OnCommand() | Full | Partial | Unknown | Unknown | Registers a WM_COMMAND callback for the control. |
| Gui.Control.OnEvent() | Full | Partial | Unknown | Unknown | Registers a control event callback. |
| Gui.Control.OnMessage() | Full | Partial | Unknown | Unknown | Registers a window-message callback for the control. |
| Gui.Control.OnNotify() | Full | Partial | Unknown | Unknown | Registers a WM_NOTIFY callback for the control. |
| Gui.Control.Opt() | Full | Partial | Unknown | Unknown | Applies options to the control. |
| Gui.Control.Redraw() | Full | Partial | Unknown | Unknown | Redraws the control. |
| Gui.Control.SetCue() | Full | Partial | Unknown | Unknown | Sets cue banner (placeholder text) for the control. |
| Gui.Control.SetFont() | Full | Partial | Unknown | Unknown | Sets the control font. |
| Gui.Control.Text | Full | Partial | Unknown | Unknown | Gets or sets the control text. |
| Gui.Control.Type | Full | Partial | Unknown | Unknown | Control type name. |
| Gui.Control.Value | Full | Partial | Unknown | Unknown | Gets or sets the control value. |
| Gui.Control.Visible | Full | Partial | Unknown | Unknown | Gets or sets whether the control is visible. |
| Gui.Destroy() | Full | Partial | Unknown | Unknown | Destroys the GUI window and releases associated resources. |
| Gui.Flash() | Full | Partial | Unknown | Unknown | Flashes the GUI window to attract attention. |
| Gui.FocusedCtrl | Full | Partial | Unknown | Unknown | Currently focused control in the GUI. |
| Gui.GetClientPos() | Full | Partial | Unknown | Unknown | Gets the GUI client-area position and size. |
| Gui.GetPos() | Full | Partial | Unknown | Unknown | Gets the GUI window position and size. |
| Gui.Hide() | Full | Partial | Unknown | Unknown | Hides the GUI window. |
| Gui.Hwnd | Full | Partial | Unknown | Unknown | Native window handle of the GUI. |
| Gui.MarginX | Full | Partial | Unknown | Unknown | Default horizontal margin for layout. |
| Gui.MarginY | Full | Partial | Unknown | Unknown | Default vertical margin for layout. |
| Gui.Maximize() | Full | Partial | Unknown | Unknown | Maximizes the GUI window. |
| Gui.MenuBar | Full | Partial | Unknown | Unknown | Gets or sets the menu bar attached to the GUI. |
| Gui.Minimize() | Full | Partial | Unknown | Unknown | Minimizes the GUI window. |
| Gui.Move() | Full | Partial | Unknown | Unknown | Moves or resizes the GUI window. |
| Gui.Name | Full | Partial | Unknown | Unknown | Associated GUI name. |
| Gui.OnEvent() | Full | Partial | Unknown | Unknown | Registers a GUI event callback. |
| Gui.OnMessage() | Full | Partial | Unknown | Unknown | Registers a window-message callback for the GUI. |
| Gui.Opt() | Full | Partial | Unknown | Unknown | Applies GUI window options. |
| Gui.Restore() | Full | Partial | Unknown | Unknown | Restores the GUI window from minimized or maximized state. |
| Gui.SetFont() | Full | Partial | Unknown | Unknown | Sets the default font for subsequent controls. |
| Gui.Show() | Full | Partial | Unknown | Unknown | Shows the GUI window. |
| Gui.Submit() | Full | Partial | Unknown | Unknown | Submits control values and returns them to script variables. |
| Gui.Tab.UseTab() | Full | Partial | Unknown | Unknown | Selects the active tab page for subsequent control additions. |
| Gui.Title | Full | Partial | Unknown | Unknown | Gets or sets the GUI window title. |
| Gui.Visible | Full | Partial | Unknown | Unknown | Gets/sets GUI visibility state. |
| GuiCtrlFromHwnd() | Full | Partial | Unknown | Unknown | The GuiCtrlFromHwnd function retrieves the GuiControl object of a GUI control associated with the specified window handle. |
| GuiFromHwnd() | Full | Partial | Unknown | Unknown | The GuiFromHwnd function retrieves the Gui object of a GUI window associated with the specified window handle. |
| HasBase() | Full | Full | Full | Full | The HasBase function returns a non-zero number if the specified value is derived from the specified base object. |
| HashMap | Full | Full | Full | Full | Keysharp-specific map class extending Map without sorted enumeration. |
| HashMap.__New() | Full | Full | Full | Full | Keysharp-specific HashMap constructor (inherits Map methods/properties). |
| HasMethod() | Full | Full | Full | Full | The HasMethod function returns a non-zero number if the specified value has a method by the specified name. |
| HasProp() | Full | Full | Full | Full | The HasProp function returns a non-zero number if the specified value has a property by the specified name. |
| HotIf() | Full | Partial | Unknown | Unknown | The HotIf and HotIfWin functions specify the criteria for subsequently created or modified hotkey variants and hotstring variants. |
| HotIfWinActive() | Full | Partial | Unknown | Unknown | Sets hotkey context for active windows. |
| HotIfWinExist() | Full | Partial | Unknown | Unknown | Sets hotkey context for existing windows. |
| HotIfWinNotActive() | Full | Partial | Unknown | Unknown | Sets hotkey context for windows that are not active. |
| HotIfWinNotExist() | Full | Partial | Unknown | Unknown | Sets hotkey context for windows that do not exist. |
| Hotkey() | Full | Partial | Unknown | Unknown | The Hotkey function creates, modifies, enables, or disables a hotkey while the script is running. |
| Hotkeys/Hotstrings | Full | Partial | Unknown | Unknown | Depends on hook and key-state parity. |
| Hotstring() | Full | Partial | Unknown | Unknown | The Hotstring function creates, modifies, enables, or disables a hotstring while the script is running. |
| If | Full | Full | Full | Full | Conditional statement. |
| IL_Add() | Full | Full | Full | Full | Adds an image to an image list, optionally can resize or split the image. |
| IL_Create() | Full | Full | Full | Full | Creates an image list and returns its unique ID. Differs in that it only takes one parameter, LargeIcons, because the first two parameters, InitialCount and GrowCount, have been omitted because C# handles memory internally. |
| IL_Destroy() | Full | Full | Full | Full | Removes an ImageList from the global list of ImageLists. Note, this does not dispose it, it just removes the reference. The garbage collector will handle final disposal when the reference count goes to 0. |
| ImageCapture() | Full | Partial | Unknown | Unknown | Captures a screen region to memory/file. |
| ImageSearch() | Full | Partial | Unknown | Unknown | Searches a region of the screen for an image. Differs in that instead of writing to ref arguments, it returns a structure whose fields are what the original input parameter names would have been. Also does not support file types of.ani,.emf,.exif or.wmf. Only 32-bit color is supported. |
| Import | Full | Full | Full | Full | The Import declaration imports a module, or imports names from a module. |
| in | Full | Full | Full | Full | Membership operator. |
| IndexError | Full | Full | Full | Full | Built-in error class. |
| IniDelete() | Full | Full | Full | Full | Deletes a value from a standard format.ini file. |
| IniRead() | Full | Full | Full | Full | Reads a value, section or list of section names from a standard format.ini file. |
| IniWrite() | Full | Full | Full | Full | Writes a value or section to a standard format.ini file. |
| InputBox() | Full | Partial | Unknown | Unknown | The InputBox function displays an input box to ask the user to enter a string. |
| InputHook() | Full | Partial | Unknown | Unknown | The InputHook function creates an object which can be used to collect or intercept keyboard input. |
| InputHook.BackspaceIsUndo | Full | Partial | Unknown | Unknown | Treats Backspace as undo for collected input. |
| InputHook.CaseSensitive | Full | Partial | Unknown | Unknown | Whether match checks are case-sensitive. |
| InputHook.EndKey | Full | Partial | Unknown | Unknown | Key that ended the input hook. |
| InputHook.EndMods | Full | Partial | Unknown | Unknown | Modifier-state snapshot when input ended. |
| InputHook.EndReason | Full | Partial | Unknown | Unknown | Reason the input hook ended. |
| InputHook.FindAnywhere | Full | Partial | Unknown | Unknown | Matches phrases anywhere in the input buffer. |
| InputHook.InProgress | Full | Partial | Unknown | Unknown | Whether the input hook is currently running. |
| InputHook.Input | Full | Partial | Unknown | Unknown | Text captured so far by the input hook. |
| InputHook.KeyOpt() | Full | Partial | Unknown | Unknown | Sets key-specific behavior options for the input hook. |
| InputHook.Match | Full | Partial | Unknown | Unknown | Phrase that matched and ended input, if any. |
| InputHook.MinSendLevel | Full | Partial | Unknown | Unknown | Minimum SendLevel accepted by the hook. |
| InputHook.NotifyNonText | Full | Partial | Unknown | Unknown | Whether non-text key notifications are enabled. |
| InputHook.OnChar | Full | Partial | Unknown | Unknown | Callback invoked for character input. |
| InputHook.OnEnd | Full | Partial | Unknown | Unknown | Callback invoked when input capture ends. |
| InputHook.OnKeyDown | Full | Partial | Unknown | Unknown | Callback invoked on key-down events. |
| InputHook.OnKeyUp | Full | Partial | Unknown | Unknown | Callback invoked on key-up events. |
| InputHook.Start() | Full | Partial | Unknown | Unknown | Starts capturing input. |
| InputHook.Stop() | Full | Partial | Unknown | Unknown | Stops capturing input. |
| InputHook.Timeout | Full | Partial | Unknown | Unknown | Maximum capture duration in seconds. |
| InputHook.Wait() | Full | Partial | Unknown | Unknown | Waits until capture ends or times out. |
| InputHook.VisibleNonText | Full | Partial | Unknown | Unknown | Whether visible non-text keys are collected. |
| InputHook.VisibleText | Full | Partial | Unknown | Unknown | Whether visible text characters are collected. |
| InstallKeybdHook() | Full | Partial | Unknown | Unknown | The InstallKeybdHook function installs or uninstalls the keyboard hook. |
| InstallMouseHook() | Full | Partial | Unknown | Unknown | The InstallMouseHook function installs or uninstalls the mouse hook. |
| InStr() | Full | Full | Full | Full | Searches for a string within another string, returning the 1-based index where it was found. Use negative numbers for searching in reverse order. |
| Integer() | Full | Full | Full | Full | Computes the integer portion of a number. |
| is | Full | Full | Full | Full | Type check operator |
| is not | Full | Full | Full | Full | Type check operator |
| IsAlnum() | Full | Full | Full | Full | Returns true if a string is alphanumeric. |
| IsAlpha() | Full | Full | Full | Full | Returns true if a string contains only letters. |
| IsClipboardEmpty() | Full | Partial | Unknown | Unknown | Built-in function. |
| IsDigit() | Full | Full | Full | Full | Returns true if a string contains only digits. |
| IsFloat() | Full | Full | Full | Full | Returns true if a value is a floating-point number. |
| IsInteger() | Full | Full | Full | Full | Returns true if a value is an integer. |
| IsLabel() | Partial | Partial | Unknown | Unknown | Does not support expressions. |
| IsLower() | Full | Full | Full | Full | Returns true if a string is lowercase. |
| IsNumber() | Full | Full | Full | Full | Returns true if a value is numeric. |
| IsObject() | Full | Full | Full | Full | The IsObject function returns a non-zero number if the specified value is an object. |
| IsSet() | Full | Full | Full | Full | The IsSet operator and IsSetRef function return a non-zero number if the specified variable has been assigned a value. |
| IsSetRef() | Full | Full | Full | Full | The IsSet operator and IsSetRef function return a non-zero number if the specified variable has been assigned a value. |
| IsSpace() | Full | Full | Full | Full | Returns true if a string contains only whitespace. |
| IsTime() | Full | Full | Full | Full | Returns true if a value is a valid time/date string. |
| IsUpper() | Full | Full | Full | Full | Returns true if a string is uppercase. |
| IsXDigit() | Full | Full | Full | Full | Returns true if a string contains only hexadecimal digits. |
| Join() | Full | Full | Full | Full | Joins arguments into a string using a separator. |
| Keyboard/Mouse send (synthetic input) | Full | Partial | Unknown | Unknown | Requires platform permissions on macOS. |
| KeyError | Full | Full | Full | Full | Built-in error class for missing keys/items. |
| KeyHistory() | Full | Partial | Unknown | Unknown | The KeyHistory function displays script info and a history of the most recent keystrokes and mouse clicks. |
| KeyWait() | Full | Partial | Unknown | Unknown | The KeyWait function waits for a key or mouse/controller button to be released or pressed down. |
| ListHotkeys() | Full | Partial | Unknown | Unknown | The ListHotkeys function displays the hotkeys in use by the current script, whether their subroutines are currently running, and whether they use a hook. |
| ListLines() | Full | Full | Unknown | Unknown | The ListLines function enables or disables line logging or displays the script lines most recently executed. |
| ListVars() | Full | Full | Unknown | Unknown | The ListVars function displays the script's variables: their names and current contents. |
| ListView.Add() | Full | Partial | Unknown | Unknown | Adds a row to a ListView control. |
| ListView.Delete() | Full | Partial | Unknown | Unknown | Deletes one row or all rows in a ListView. |
| ListView.DeleteCol() | Full | Partial | Unknown | Unknown | Deletes a column from a ListView control. |
| ListView.GetCount() | Full | Partial | Unknown | Unknown | Gets item, selected-item, or column count in a ListView. |
| ListView.GetNext() | Full | Partial | Unknown | Unknown | Gets the next row matching selection/focus criteria. |
| ListView.GetText() | Full | Partial | Unknown | Unknown | Gets text from a ListView row and column. |
| ListView.Insert() | Full | Partial | Unknown | Unknown | Inserts a row at a specific position in a ListView. |
| ListView.InsertCol() | Full | Partial | Unknown | Unknown | Inserts a column into a ListView. |
| ListView.Modify() | Full | Partial | Unknown | Unknown | Changes ListView row state, text, or icon. |
| ListView.ModifyCol() | Full | Partial | Unknown | Unknown | Changes ListView column options and width. |
| ListView.SetImageList() | Full | Partial | Unknown | Unknown | Assigns an image list for ListView icons. |
| ListViewGetContent() | Full | Partial | Unknown | Unknown | The ListViewGetContent function returns content data from a list-view control, such as rows, columns, or count values. |
| Ln() | Full | Full | Full | Full | Computes the base e (natural) logarithm of a number. Throws an exception if a negative number is passed in. |
| LoadPicture() | Full | Full | Full | Full | Loads an image, icon or cursor. Differs in that instead of writing to a ref argument, it returns a structure whose fields are Handle and ImageType. |
| LockRun() | Full | Full | Full | Full | Runs code under a lock to prevent concurrent execution overlap. |
| Log() | Full | Full | Full | Full | Computes the base 10 logarithm of a number. Throws an exception if a negative number is passed in. |
| Loop | Full | Full | Full | Full | Loop statement. |
| Loop (files & folders) | Full | Full | Full | Full | Language/runtime capability. |
| Loop (normal) | Full | Full | Full | Full | Repeats a block a specified number of times or indefinitely. |
| Loop (read file contents) | Full | Full | Full | Full | Language/runtime capability. |
| Loop File | Full | Full | Full | Full | Lists files and folders at a location, matching a specified pattern, optionally recursing. |
| Loop Files | Full | Full | Full | Full | Enumerates files/folders matching a pattern. |
| Loop Parse | Full | Full | Full | Full | Parses the string either one character at a time, or broken into pieces based on the delimiter. Note this accepts strings as delimiters, unlike AHK which did not. |
| Loop Read | Full | Full | Full | Full | Reads though a file one line at a time. Optionally supports an output file, which can then be used with FileAppend with no filename argument. |
| Loop Reg | Full | Unsupported | Unknown | Unknown | Reads through registry keys and values, optionally recursive. Additionally supports HKEY_PERFORMANCE_DATA, and an accessor A_LoopRegValue to get the values. Supports data types except the following, which will return UNKNOWN: REG_LINK, REG_RESOURCE_LIST, REG_FULL_RESOURCE_DESCRIPTOR, REG_RESOURCE_REQUIREMENTS_LIST, REG_DWORD_BIG_ENDIAN. |
| LTrim() | Full | Full | Full | Full | Trims characters from the end of a string. |
| Mail() | Full | Full | Full | Full | Sends email via configured SMTP settings. |
| Map | Full | Full | Full | Full | Map object type. |
| Map.__Enum() | Full | Full | Full | Full | Enumerates key-value pairs. |
| Map.__Item | Full | Full | Full | Full | Indexer property for getting or setting map values by key. |
| Map.__New() | Full | Full | Full | Full | Constructs a new Map object. |
| Map.Capacity | Full | Full | Full | Full | Gets or sets the internal capacity for map entries. |
| Map.CaseSense | Full | Full | Full | Full | Retrieves or sets the map's case sensitivity setting. |
| Map.Clear() | Full | Full | Full | Full | Clears all key/value pairs in a map. |
| Map.Clone() | Full | Full | Full | Full | Returns a shallow copy of all of the values and keys of the map. |
| Map.Contains() | Full | Full | Full | Full | Keysharp-specific Map method (alias-style key containment check). |
| Map.Count | Full | Full | Full | Full | Number of key/value pairs in the map. |
| Map.Default | Full | Full | Full | Full | Default value returned for missing keys. |
| Map.Delete() | Full | Full | Full | Full | Deletes a key/value pair out of a map if the key exists, else throws an exception. |
| Map.Get() | Full | Full | Full | Full | Gets a value by key with optional fallback default. |
| Map.Has() | Full | Full | Full | Full | Returns whether a dictionary contains a value, even an empty one, for the given key. |
| Map.MaxIndex() | Full | Full | Full | Full | Returns the largest integer key contained in the map. Returns empty string if no keys were integers. |
| Map.MinIndex() | Full | Full | Full | Full | Returns the smallest integer key contained in the map. Returns empty string if no keys were integers. |
| Map.Set() | Full | Full | Full | Full | Sets zero or more items. |
| Max() | Full | Full | Full | Full | Computes the larger of two numbers. If either is not numeric, the empty string is returned. The largest value of an array is computed if one is passed in. |
| MD5() | Full | Full | Full | Full | Computes MD5 hash for input data. |
| MemberError | Full | Full | Full | Full | Built-in error class. |
| MemoryError | Full | Full | Full | Full | Built-in error class. |
| Menu() | Full | Partial | Unknown | Unknown | The Menu/MenuBar object provides an interface to create and modify a menu or menu bar, add and modify menu items, and retrieve information about the menu or menu bar. |
| Menu.Add() | Full | Partial | Unknown | Unknown | Adds an item to a menu. |
| Menu.AddStandard() | Full | Partial | Unknown | Unknown | Adds standard tray menu items. |
| Menu.Check() | Full | Partial | Unknown | Unknown | Checks a menu item. |
| Menu.ClickCount | Full | Partial | Unknown | Unknown | Number of clicks required to trigger a tray menu item. |
| Menu.Default | Full | Partial | Unknown | Unknown | Default menu item name or position. |
| Menu.Delete() | Full | Partial | Unknown | Unknown | Deletes one menu item or all items. |
| Menu.Disable() | Full | Partial | Unknown | Unknown | Disables a menu item. |
| Menu.Enable() | Full | Partial | Unknown | Unknown | Enables a menu item. |
| Menu.Handle | Full | Partial | Unknown | Unknown | Native menu handle. |
| Menu.Insert() | Full | Partial | Unknown | Unknown | Inserts a menu item at a specific position. |
| Menu.MenuItemCount | Full | Partial | Unknown | Unknown | Returns number of items in a menu. |
| Menu.MenuItemName() | Full | Partial | Unknown | Unknown | Returns item text/name for a menu entry. |
| Menu.Rename() | Full | Partial | Unknown | Unknown | Renames a menu item. |
| Menu.SetColor() | Full | Partial | Unknown | Unknown | Sets menu background color. |
| Menu.SetIcon() | Full | Partial | Unknown | Unknown | Sets icon for a menu item. |
| Menu.Show() | Full | Partial | Unknown | Unknown | Shows the menu at a screen position. |
| Menu.ToggleCheck() | Full | Partial | Unknown | Unknown | Toggles checked state of a menu item. |
| Menu.ToggleEnable() | Full | Partial | Unknown | Unknown | Toggles enabled state of a menu item. |
| Menu.ToggleItemVis() | Full | Partial | Unknown | Unknown | Toggles visibility of a menu item. |
| Menu.Uncheck() | Full | Partial | Unknown | Unknown | Unchecks a menu item. |
| MenuBar() | Full | Partial | Unknown | Unknown | The Menu/MenuBar object provides an interface to create and modify a menu or menu bar, add and modify menu items, and retrieve information about the menu or menu bar. |
| MenuFromHandle() | Full | Partial | Unknown | Unknown | The MenuFromHandle function retrieves the Menu or MenuBar object corresponding to a Win32 menu handle. |
| MenuSelect() | Full | Partial | Unknown | Unknown | The MenuSelect function invokes a menu item from the menu bar of the specified window. |
| MethodError | Full | Full | Full | Full | Built-in error class. |
| Min() | Full | Full | Full | Full | Computes the smaller of two numbers. If either is not numeric, the empty string is returned. The smaller value of an array is computed if one is passed in. |
| Mod() | Full | Full | Full | Full | Computes the remainder when the first number is divided by the second number. Throws an exception if the second number is 0. |
| MonitorGet() | Full | Partial | Unknown | Unknown | Checks if the specified monitor exists and optionally retrieves its bounding coordinates. Differs in that instead of writing to ref arguments, it returns a structure whose fields are what the original input parameter names would have been. |
| MonitorGetCount() | Full | Partial | Unknown | Unknown | Returns the total number of monitors. |
| MonitorGetName() | Full | Full | Full | Full | Returns the operating system's name of the specified monitor. |
| MonitorGetPrimary() | Full | Full | Full | Full | Returns the number of the primary monitor. |
| MonitorGetWorkArea() | Full | Partial | Unknown | Unknown | Checks if the specified monitor exists and optionally retrieves the bounding coordinates of its working area. Differs in that instead of writing to ref arguments, it returns a structure whose fields are what the original input parameter names would have been. |
| MouseClick() | Full | Partial | Unknown | Unknown | The MouseClick function clicks or holds down a mouse button, or turns the mouse wheel. |
| MouseClickDrag() | Full | Partial | Unknown | Unknown | The MouseClickDrag function clicks and holds the specified mouse button, moves the mouse to the destination coordinates, then releases the button. |
| MouseGetPos() | Full | Partial | Unknown | Unknown | The MouseGetPos function retrieves the current position of the mouse cursor, and optionally which window and control it is hovering over. |
| MouseMove() | Full | Partial | Unknown | Unknown | The MouseMove function moves the mouse cursor. |
| MsgBox() | Full | Partial | Unknown | Unknown | Displays the specified text in a small window containing one or more buttons. Differs in that the following option values are not supported: 6, 768, 4096, 8192, 262144, 16384 (meaning, no help button). Also, when the timeout options is used, or an owner window is set, the text will be right justified. |
| not | Full | Full | Full | Full | Logical NOT operator. |
| Number() | Full | Full | Full | Full | The Number function converts a numeric string to a pure integer or floating-point number. |
| NumGet() | Full | Full | Unknown | Unknown | The NumGet function returns the binary number stored at the specified address+offset. |
| NumPut() | Full | Full | Unknown | Unknown | The NumPut function stores one or more numbers in binary format at the specified address+offset. |
| ObjAddRef() | Full | Unsupported | Unsupported | Unsupported | The ObjAddRef and ObjRelease functions increment or decrement an object's reference count. |
| ObjBindMethod() | Full | Full | Full | Full | The ObjBindMethod function creates a BoundFunc object which calls a method of a given object. |
| Object() | Full | Full | Full | Full | Object is the basic class from which other AutoHotkey object classes derive. |
| Object.__Ref() | Planned | Planned | Planned | Planned | Returns a reference object for object lifetime management. |
| Object.OwnPropCount() | Full | Full | Unknown | Unknown | Returns number of own properties defined directly on the object. |
| ObjFree() | Full | Full | Full | Full | Releases object references associated with a pointer/COM wrapper context. |
| ObjFromPtr() | Full | Unsupported | Unknown | Unknown | Creates or retrieves an object wrapper from a raw pointer. |
| ObjFromPtrAddRef() | Full | Unsupported | Unknown | Unknown | Creates/retrieves an object wrapper from a pointer and increments its reference count. |
| ObjGetBase | Full | Full | Full | Full | Retrieves the value's base object. Differs in that it only returns the name of the base type as a string. |
| ObjGetCapacity() | Full | Full | Full | Full | Object is the basic class from which other AutoHotkey object classes derive. |
| ObjGetDataPtr() | Full | Full | Full | Full | Object is the basic class from which other AutoHotkey object classes derive. |
| ObjGetDataSize() | Full | Full | Full | Full | Object is the basic class from which other AutoHotkey object classes derive. |
| ObjHasOwnProp() | Full | Full | Full | Full | Object is the basic class from which other AutoHotkey object classes derive. |
| ObjOwnPropCount() | Full | Full | Full | Full | Object is the basic class from which other AutoHotkey object classes derive. |
| ObjOwnProps | Full | Full | Full | Full | Enumerates an object's own properties. |
| ObjPtr() | Full | Unsupported | Unknown | Unknown | Returns the raw pointer address of an object. |
| ObjPtrAddRef() | Full | Unsupported | Unknown | Unknown | Returns object pointer address and increments its reference count. |
| ObjRelease() | Full | Unsupported | Unknown | Unknown | The ObjAddRef and ObjRelease functions increment or decrement an object's reference count. |
| ObjSetBase() | Unsupported | Unsupported | Unknown | Unknown | Getting or setting an object's base class is not supported in C#. |
| ObjSetCapacity() | Full | Full | Full | Full | Object is the basic class from which other AutoHotkey object classes derive. |
| ObjSetDataPtr() | Full | Full | Full | Full | Object is the basic class from which other AutoHotkey object classes derive. |
| OnClipboardChange() | Full | Partial | Unknown | Unknown | Wires up an event to be called when the clipboard contents are change. |
| OnError() | Full | Full | Full | Full | The OnError function registers a function to be called automatically whenever an unhandled error occurs. |
| OnExit() | Full | Full | Unknown | Unknown | The OnExit function registers a function to be called automatically whenever the script exits. |
| OnMessage() | Full | Full | Full | Full | The OnMessage function registers a function to be called automatically whenever the script receives the specified message. |
| or | Full | Full | Full | Full | Logical OR operator. |
| Ord() | Full | Full | Full | Full | Returns the numeric two byte unicode value for the first character in a string. This differs from V2 in that it also takes an optional second parameter which specified the 1-based index in the string to return the numeric value for, rather than only doing it for the first character. |
| OSError | Full | Full | Full | Full | Built-in error class. |
| OutputDebug() | Full | Full | Unknown | Unknown | The OutputDebug function sends a string to the debugger (if any) for display. |
| OutputDebugLine() | Full | Full | Full | Full | Writes a debug line with newline terminator. |
| Parser and runtime execution | Full | Full | Full | Full | Parser, preprocessing, and script execution runtime are implemented. |
| ParseScript() | Full | Full | Full | Full | Parses script text and returns parse/compile result metadata. |
| Pause() | Full | Full | Unknown | Unknown | The Pause function pauses the script's current thread or sets the pause state of the underlying thread. |
| Persistent() | Full | Full | Unknown | Unknown | The Persistent function prevents the script from exiting automatically when its last thread completes, allowing it to stay running in an idle state. |
| PixelGetColor() | Full | Partial | Unknown | Unknown | Returns the pixel value at the specified coordinate as a hexadecimal string like 0x010203. Differs because the mode parameter is not supported because it is not needed. |
| PixelSearch() | Full | Partial | Unknown | Unknown | Searches a region of the screen for a pixel of the specified color. Differs in that instead of writing to ref arguments, it returns a structure whose fields are what the original input parameter names would have been. |
| PostMessage() | Full | Full | Full | Full | The PostMessage function places a message in the message queue of a window or control. |
| ProcessClose() | Full | Full | Full | Full | Forces the first matching process to close. |
| ProcessExist() | Full | Full | Full | Full | Checks if the specified process exists. |
| ProcessGetName() | Full | Full | Full | Full | The ProcessGetName and ProcessGetPath functions return the name or path of the specified process. |
| ProcessGetParent() | Full | Full | Full | Full | The ProcessGetParent function returns the process ID (PID) of the process which created the specified process. |
| ProcessGetPath() | Full | Full | Full | Full | The ProcessGetName and ProcessGetPath functions return the name or path of the specified process. |
| ProcessInfo | Full | Full | Full | Full | Represents process handle metadata and redirected I/O streams. |
| ProcessInfo.ExitCode | Full | Full | Full | Full | Gets process exit code after termination. |
| ProcessInfo.ExitTime | Full | Full | Full | Full | Gets process exit time. |
| ProcessInfo.HasExited | Full | Full | Full | Full | Returns whether the process has exited. |
| ProcessInfo.Kill() | Full | Full | Full | Full | Terminates the process. |
| ProcessInfo.StdErr | Full | Full | Full | Full | Gets redirected standard-error stream. |
| ProcessInfo.StdIn | Full | Full | Full | Full | Gets redirected standard-input stream. |
| ProcessInfo.StdOut | Full | Full | Full | Full | Gets redirected standard-output stream. |
| ProcessSetPriority() | Full | Full | Full | Full | Changes the priority level of the first matching process. |
| ProcessWait() | Full | Full | Full | Full | Waits for the specified process to exist. |
| ProcessWaitClose() | Full | Full | Full | Full | Waits for all matching processes to close. |
| PropertyError | Full | Full | Full | Full | Built-in error class. |
| PropRef | Planned | Planned | Planned | Planned | Property-reference object type. |
| Props | Full | Full | Full | Full | Helper for creating property definitions. |
| Random() | Full | Full | Full | Full | Computes a random number in the range of x to y. |
| RandomSeed() | Full | Full | Full | Full | Sets seed for the pseudo-random generator used by Random(). |
| RealThread | Full | Full | Full | Full | Represents a real background thread handle/promise. |
| RealThread.Call() | Full | Full | Full | Full | Invokes work on the underlying real thread context. |
| RealThread.ContinueWith() | Full | Full | Full | Full | Schedules continuation after thread task completion. |
| RealThread.Wait() | Full | Full | Full | Full | Waits for real thread completion. |
| RegCreateKey() | Full | Unsupported | Unknown | Unknown | The RegCreateKey function creates a registry key without writing a value. |
| RegDelete | Full | Unsupported | Unknown | Unknown | Deletes a value from the registry. |
| RegDeleteKey() | Full | Unsupported | Unknown | Unknown | Deletes a key from the registry. |
| RegExMatch() | Full | Full | Full | Full | Searches a string for a regular expression match. |
| RegExMatchCs() | Full | Full | Full | Full | Runs .NET/C# regex match and returns match details. |
| RegExMatchInfo | Full | Full | Full | Full | Match object returned by RegExMatch. |
| RegExReplace() | Full | Full | Full | Full | Replaces text matching a regular expression pattern. |
| RegExReplaceCs() | Full | Full | Full | Full | Runs .NET/C# regex replace. |
| Registry APIs | Full | Unsupported | Unsupported | Unsupported | Windows Registry APIs are Windows-only. |
| RegRead() | Full | Unsupported | Unknown | Unknown | Reads a value from the registry. Supports REG_QWORD in addition to the other types. |
| RegWrite | Full | Unsupported | Unknown | Unknown | Writes a value to the registry. Supports REG_QWORD in addition to the other types. |
| Reload() | Full | Full | Unknown | Unknown | The Reload function replaces the currently running instance of the script with a new one. |
| Return | Full | Full | Full | Full | Returns from a function/subroutine, optionally with a value. |
| Round() | Full | Full | Full | Full | Computes a number rounded to either the nearest integer, a specified number of decimal places, or a specified number of digits. |
| RTrim() | Full | Full | Full | Full | Trims characters from the beginning of a string. |
| Run() | Full | Full | Full | Full | The Run and RunWait functions run an external program. RunWait will wait until the program finishes before continuing. |
| RunAs() | Full | Full | Full | Full | Specifies a set of user credentials to use for all subsequent uses of Run and RunWait. |
| RunScript() | Full | Full | Full | Full | Executes script source text/file in a script engine context. |
| RunWait() | Full | Full | Full | Full | The Run and RunWait functions run an external program. RunWait will wait until the program finishes before continuing. |
| Screen capture and pixel/image functions | Full | Partial | Unknown | Unknown | Pixel/image search and screen capture depend on platform-specific backends. |
| Script-owned window management | Full | Partial | Unknown | Unknown | Built on WinForms/Eto; some controls and behavior still differ. |
| SecureRandom() | Full | Full | Full | Full | Generates cryptographically secure random numbers. |
| Send() | Full | Partial | Unknown | Unknown | Sends simulated keystrokes. |
| SendEvent() | Full | Partial | Unknown | Unknown | Sends keystrokes via Event mode. |
| SendInput() | Full | Partial | Unknown | Unknown | Sends keystrokes via Input mode. |
| SendLevel() | Full | Partial | Unknown | Unknown | Sets the send level for generated input. |
| SendMessage() | Full | Partial | Unknown | Unknown | The SendMessage function sends a message to a window or control and waits for acknowledgement. |
| SendMode() | Full | Partial | Unknown | Unknown | Sets the default send mode. |
| SendPlay() | Full | Partial | Unknown | Unknown | Sends keystrokes via Play mode. |
| SendText() | Full | Partial | Unknown | Unknown | Sends text without translating key names. |
| SetCapsLockState() | Full | Full | Full | Full | The SetCapsLockState, SetNumLockState and SetScrollLockState functions set the state of the corresponding key. Can also force the key to stay on or off. |
| SetControlDelay() | Full | Full | Full | Full | The SetControlDelay function sets the delay that will occur after each control-modifying function. |
| SetDefaultMouseSpeed() | Full | Partial | Unknown | Unknown | The SetDefaultMouseSpeed function sets the mouse speed that will be used if unspecified in Click, MouseMove, MouseClick and MouseClickDrag. |
| SetKeyDelay() | Full | Partial | Unknown | Unknown | The SetKeyDelay function sets the delay that will occur after each keystroke sent by the Send or ControlSend functions. |
| SetMouseDelay() | Full | Partial | Unknown | Unknown | The SetMouseDelay function sets the delay that will occur after each mouse movement or click. |
| SetNumLockState() | Full | Full | Full | Full | The SetCapsLockState, SetNumLockState and SetScrollLockState functions set the state of the corresponding key. Can also force the key to stay on or off. |
| SetRegView() | Full | Unsupported | Unknown | Unknown | Sets the registry view used by registry functions, allowing them in a 64-bit script to access the 32-bit registry view. |
| SetScrollLockState() | Full | Full | Full | Full | The SetCapsLockState, SetNumLockState and SetScrollLockState functions set the state of the corresponding key. Can also force the key to stay on or off. |
| SetStoreCapsLockMode() | Full | Full | Full | Full | The SetStoreCapsLockMode function determines whether to restore the state of the CapsLock key after a Send function. |
| SetTimer() | Full | Full | Full | Full | The SetTimer function causes a function to be called automatically and repeatedly at a specified time interval. |
| SetTitleMatchMode() | Full | Full | Unknown | Unknown | The SetTitleMatchMode function sets the matching behavior of the WinTitle parameter in built-in functions such as WinWait. |
| SetWinDelay() | Full | Full | Full | Full | The SetWinDelay function sets the delay that will occur after each windowing function, such as WinActivate. |
| SetWorkingDir() | Full | Full | Unknown | Unknown | Changes the script's current working directory. |
| SHA1() | Full | Full | Full | Full | Computes SHA-1 hash for input data. |
| SHA256() | Full | Full | Full | Full | Computes SHA-256 hash for input data. |
| SHA384() | Full | Full | Full | Full | Computes SHA-384 hash for input data. |
| SHA512() | Full | Full | Full | Full | Computes SHA-512 hash for input data. |
| ShowDebug() | Full | Full | Full | Full | Shows or toggles debug UI/log output. |
| Shutdown() | Full | Full | Full | Full | Shuts down, restarts, or logs off the system. |
| Sin() | Full | Full | Full | Full | Computes the hyperbolic sine of a number. |
| Sinh() | Full | Full | Full | Full | Computes the hyperbolic sine of a number. |
| Sleep() | Full | Full | Full | Full | The Sleep function waits the specified amount of time before continuing. |
| Sort() | Full | Full | Full | Full | Arranges a variable's contents in alphabetical, numerical, or random order (optionally removing duplicates). The back slash option also supports specifying a forward slash / so it can be used for paths on non-Windows systems. |
| Sound APIs | Full | Partial | Unknown | Unknown | Audio device/endpoint support differs by platform. |
| SoundBeep() | Full | Partial | Unknown | Unknown | Emits a tone from the PC speaker. Platform statuses inherited from curated 'Sound APIs'; per-function validation pending. |
| SoundGetInterface() | Full | Unsupported | Unknown | Unknown | Retrieves a native COM interface of a sound device or component. Platform statuses inherited from curated 'Sound APIs'; per-function validation pending. Returns a native COM interface and is Windows-specific. |
| SoundGetMute() | Full | Partial | Unknown | Unknown | Retrieves a mute setting of a sound device. Differs in that there is no support for components, so the function only takes one parameter: the 1-based index, or name for the device. Platform statuses inherited from curated 'Sound APIs'; per-function validation pending. |
| SoundGetName() | Full | Partial | Unknown | Unknown | Retrieves the name of a sound device. Differs in that there is no support for components, so the function only takes one parameter: the 1-based index, or name for the device. Platform statuses inherited from curated 'Sound APIs'; per-function validation pending. |
| SoundGetVolume() | Full | Partial | Unknown | Unknown | Retrieves a mute setting of a sound device. Differs in that there is no support for components, so the function only takes one parameter: the 1-based index, or name for the device. Platform statuses inherited from curated 'Sound APIs'; per-function validation pending. |
| SoundPlay() | Full | Partial | Unknown | Unknown | Plays a sound, video, or other supported file type. Platform statuses inherited from curated 'Sound APIs'; per-function validation pending. |
| SoundSetMute() | Full | Partial | Unknown | Unknown | Changes a mute setting of a sound device. Differs in that there is no support for components, so the function only takes one parameter: the 1-based index, or name for the device. Platform statuses inherited from curated 'Sound APIs'; per-function validation pending. |
| SoundSetVolume() | Full | Partial | Unknown | Unknown | Changes a volume setting of a sound device. Differs in that there is no support for components, so the function only takes one parameter: the 1-based index, or name for the device. Platform statuses inherited from curated 'Sound APIs'; per-function validation pending. |
| SplitPath() | Full | Full | Unknown | Unknown | Separates a file name or URL into its name, directory, extension, and drive. Differs in that instead of writing to ref arguments, it returns a structure whose fields are what the original input parameter names would have been. |
| Sqrt() | Full | Full | Full | Full | Computes the square root of a number. Throws an exception if the argument is negative. |
| StatusBarGetText() | Full | Full | Full | Full | The StatusbarGetText function retrieves the text from a standard status bar control. |
| StatusBarWait() | Full | Full | Full | Full | The StatusBarWait function waits until a window's status bar contains the specified string. |
| StrCompare() | Full | Full | Unknown | Unknown | Compares two strings alphabetically. Note this supports local, human readable comparison as well. |
| StrGet() | Full | Full | Unknown | Unknown | Copies a string from a memory address or buffer, optionally converting it from a given code page. |
| String() | Full | Full | Unknown | Unknown | Converts a value to a string. |
| String.EndsWith() | Full | Full | Full | Full | Returns whether a string ends with the specified suffix. |
| String.StartsWith() | Full | Full | Full | Full | Returns whether a string starts with the specified prefix. |
| StringBuffer() | Full | Full | Full | Full | Creates a mutable string buffer object. |
| StrLen() | Full | Full | Unknown | Unknown | Retrieves the count of how many characters are in a string. |
| StrLower() | Full | Full | Unknown | Unknown | Converts a string to lowercase. |
| StrPtr() | Full | Full | Full | Full | The StrPtr function returns the current memory address of a string. |
| StrPut() | Full | Full | Unknown | Unknown | Writes string data to a buffer/address using specified encoding. |
| StrReplace() | Full | Full | Unknown | Unknown | Replaces occurrences of a substring and returns the updated string. |
| StrSplit() | Full | Full | Unknown | Unknown | Retrieves one or more characters from the specified position in a string. |
| StrTitle() | Full | Full | Full | Full | The StrLower, StrUpper and StrTitle functions convert a string to lowercase, uppercase or title case. |
| struct.__Value | Unsupported | Unsupported | Unsupported | Unsupported | Backing value property for struct instances (struct support not yet implemented). |
| StructFromPtr() | Planned | Planned | Unknown | Unknown | Not implemented. |
| structures | Unsupported | Unsupported | Unsupported | Unsupported | User-defined structures (planned; not currently implemented). |
| StrUpper() | Full | Full | Unknown | Unknown | Converts a string to uppercase. |
| SubStr() | Full | Full | Full | Full | Retrieves one or more characters from the specified position in a string. |
| super | Full | Full | Full | Full | Keyword that accesses base-class methods and properties. |
| Suspend() | Full | Full | Unknown | Unknown | The Suspend function disables or enables all or selected hotkeys and hotstrings. |
| Switch | Full | Full | Full | Full | Selects one case branch based on a value/expression. |
| SysGet() | Full | Full | Unknown | Unknown | Gets various system information. Can accept either an integer or an enum. |
| SysGetIPAddresses() | Full | Full | Unknown | Unknown | The SysGetIPAddresses function returns an array of the system's IPv4 addresses. |
| ZeroDivisionError | Full | Full | Full | Full | Built-in error class. |
| TabControl.SetTabIcon() | Full | Partial | Unknown | Unknown | Sets icon for a tab page in tab controls. |
| Tan() | Full | Full | Full | Full | Computes the tangent of a number. |
| Tanh() | Full | Full | Full | Full | Computes the hyperbolic tangent of a number. |
| TargetError | Full | Full | Full | Full | Built-in error class. |
| Thread | Full | Full | Full | Full | Thread settings and controls. |
| Throw | Partial | Partial | Unknown | Unknown | Rethrowing with throw is only allowed directly within the scope of catch, not from an arbitrary point (eg from functions). |
| TimeoutError | Full | Full | Full | Full | Built-in error class. |
| ToolTip() | Full | Partial | Unknown | Unknown | Creates an always-on-top window anywhere on the screen. |
| Tray icon and menu | Full | Partial | Unknown | Unknown | Tray/menu behavior varies by desktop environment and platform APIs. |
| TraySetIcon() | Full | Partial | Unknown | Unknown | Changes the script's tray icon. |
| TrayTip() | Full | Partial | Unknown | Unknown | Creates a toast message window near the tray icon. Differs in that there is no way to mute the system sound specify a large icon. The duration will be 5 seconds by default, but an additional option dur can be used to specify the duration in seconds, such as dur7. The registry key EnableBalloonTips is not observed for disabling the notification. The option 4 has no effect because the tray icon is always shown at the top of the toast. |
| TreeView.Add() | Full | Partial | Unknown | Unknown | Adds an item to a TreeView. |
| TreeView.Delete() | Full | Partial | Unknown | Unknown | Deletes one item or all items in a TreeView. |
| TreeView.Get() | Full | Partial | Unknown | Unknown | Gets state information for a TreeView item. |
| TreeView.GetChild() | Full | Partial | Unknown | Unknown | Gets the first child item of a TreeView node. |
| TreeView.GetCount() | Full | Partial | Unknown | Unknown | Gets the total item count in a TreeView. |
| TreeView.GetNext() | Full | Partial | Unknown | Unknown | Gets the next sibling item in a TreeView. |
| TreeView.GetNode() | Full | Partial | Unknown | Unknown | Returns TreeView node object by node id/handle. |
| TreeView.GetParent() | Full | Partial | Unknown | Unknown | Gets the parent item of a TreeView node. |
| TreeView.GetPrev() | Full | Partial | Unknown | Unknown | Gets the previous sibling item in a TreeView. |
| TreeView.GetSelection() | Full | Partial | Unknown | Unknown | Gets the currently selected TreeView item. |
| TreeView.GetText() | Full | Partial | Unknown | Unknown | Gets text of a TreeView item. |
| TreeView.Modify() | Full | Partial | Unknown | Unknown | Changes TreeView item text, icon, or state. |
| TreeView.SetImageList() | Full | Partial | Unknown | Unknown | Assigns an image list for TreeView icons. |
| Trim() | Full | Full | Full | Full | Trims characters from the beginning and end of a string. |
| True | Full | Full | Full | Full | Boolean true constant. |
| Try | Full | Full | Full | Full | Starts exception-handling scope for statements that may throw. |
| Type | Full | Full | Full | Full | The Type function returns the class name of a value. |
| TypeError | Full | Full | Full | Full | Built-in error class. |
| UnsetError | Full | Full | Full | Full | Built-in error class. |
| UnsetItemError | Full | Full | Full | Full | Built-in error class. |
| Until | Full | Full | Full | Full | Loop-until condition syntax for terminating a loop when condition becomes true. |
| ValueError | Full | Full | Full | Full | Built-in error class. |
| VarSetStrCapacity() | Full | Full | Unknown | Unknown | Does nothing because the.NET runtime manages all memory. |
| VerCompare() | Full | Full | Full | Full | The VerCompare function compares two version strings. |
| While | Full | Full | Full | Full | While-loop statement. |
| WinActivate() | Full | Partial | Unknown | Unknown | The WinActivate function activates the specified window. |
| WinActivateBottom() | Full | Partial | Unknown | Unknown | The WinActivateBottom function is similar to the WinActivate function but it activates the bottommost matching window rather than the topmost. |
| WinActive() | Full | Partial | Unknown | Unknown | The WinActive function checks if the specified window is active and returns its unique ID (HWND). |
| WinClose() | Full | Partial | Unknown | Unknown | The WinClose function closes the specified window. |
| WinExist() | Full | Partial | Unknown | Unknown | The WinExist function checks if the specified window exists and returns the unique ID (HWND) of the first matching window. |
| WinFromPoint() | Full | Partial | Unknown | Unknown | Returns window handle located at screen coordinates. |
| WinGetAlwaysOnTop() | Full | Partial | Unknown | Unknown | The WinGetAlwaysOnTop function returns a non-zero value if the specified window is always-on-top. |
| WinGetClass() | Full | Partial | Unknown | Unknown | The WinGetClass function retrieves the specified window's class name. |
| WinGetClientPos() | Full | Partial | Unknown | Unknown | The WinGetClientPos function retrieves the position and size of the specified window's client area. |
| WinGetControls() | Full | Partial | Unknown | Unknown | The WinGetControls function returns an array of ClassNNs for all controls in the specified window. |
| WinGetControlsHwnd() | Full | Partial | Unknown | Unknown | The WinGetControlsHwnd function returns an array of unique IDs (HWNDs) for all controls in the specified window. |
| WinGetCount() | Full | Partial | Unknown | Unknown | The WinGetCount function returns the number of existing windows that match the specified criteria. |
| WinGetEnabled() | Full | Partial | Unknown | Unknown | The WinGetEnabled function returns a non-zero value if the specified window is enabled. |
| WinGetExStyle() | Full | Partial | Unknown | Unknown | The WinGetStyle and WinGetExStyle functions return the style or extended style (respectively) of the specified window. |
| WinGetID() | Full | Partial | Unknown | Unknown | The WinGetID function returns the unique ID (HWND) of the specified window. |
| WinGetIDLast() | Full | Partial | Unknown | Unknown | The WinGetIDLast function returns the unique ID (HWND) of the last/bottommost window if there is more than one match. |
| WinGetList() | Full | Partial | Unknown | Unknown | The WinGetList function returns an array of unique IDs (HWNDs) for all existing windows that match the specified criteria. |
| WinGetMinMax() | Full | Partial | Unknown | Unknown | The WinGetMinMax function returns a non-zero number if the specified window is maximized or minimized. |
| WinGetPID() | Full | Partial | Unknown | Unknown | The WinGetPID function returns the Process ID (PID) of the specified window. |
| WinGetPos() | Full | Partial | Unknown | Unknown | The WinGetPos function retrieves the position and size of the specified window. |
| WinGetProcessName() | Full | Partial | Unknown | Unknown | The WinGetProcessName function returns the name of the process that owns the specified window. |
| WinGetProcessPath() | Full | Partial | Unknown | Unknown | The WinGetProcessPath function returns the full path and name of the process that owns the specified window. |
| WinGetStyle() | Full | Partial | Unknown | Unknown | The WinGetStyle and WinGetExStyle functions return the style or extended style (respectively) of the specified window. |
| WinGetText | Full | Partial | Unknown | Unknown | The WinGetText function retrieves the text from the specified window. |
| WinGetTitle() | Full | Partial | Unknown | Unknown | The WinGetTitle function retrieves the title of the specified window. |
| WinGetTransColor() | Full | Partial | Unknown | Unknown | The WinGetTransColor function returns the color that is marked transparent in the specified window. |
| WinGetTransparent() | Full | Partial | Unknown | Unknown | The WinGetTransparent function returns the degree of transparency of the specified window. |
| WinHide() | Full | Partial | Unknown | Unknown | The WinHide function hides the specified window. |
| WinKill() | Full | Partial | Unknown | Unknown | The WinKill function forces the specified window to close. |
| WinMaximize() | Full | Partial | Unknown | Unknown | The WinMaximize function enlarges the specified window to its maximum size. |
| WinMaximizeAll() | Full | Partial | Unknown | Unknown | Maximizes all top-level windows. |
| WinMinimize() | Full | Partial | Unknown | Unknown | The WinMinimize function collapses the specified window into a button on the task bar. |
| WinMinimizeAll() | Full | Partial | Unknown | Unknown | The WinMinimizeAll and WinMinimizeAllUndo functions minimize or unminimize all windows. |
| WinMinimizeAllUndo() | Full | Partial | Unknown | Unknown | The WinMinimizeAll and WinMinimizeAllUndo functions minimize or unminimize all windows. |
| WinMove() | Full | Partial | Unknown | Unknown | The WinMove function changes the position and/or size of the specified window. |
| WinMoveBottom() | Full | Partial | Unknown | Unknown | The WinMoveBottom function sends the specified window to the bottom of stack; that is, beneath all other windows. |
| WinMoveTop() | Full | Partial | Unknown | Unknown | The WinMoveTop function brings the specified window to the top of the stack without explicitly activating it. |
| WinRedraw() | Full | Partial | Unknown | Unknown | The WinRedraw function redraws the specified window. |
| WinRestore() | Full | Partial | Unknown | Unknown | The WinRestore function unminimizes or unmaximizes the specified window if it is minimized or maximized. |
| WinSetAlwaysOnTop() | Full | Partial | Unknown | Unknown | The WinSetAlwaysOnTop function makes the specified window stay on top of all other windows (except other always-on-top windows). |
| WinSetEnabled() | Full | Partial | Unknown | Unknown | The WinSetEnabled function enables or disables the specified window. |
| WinSetExStyle() | Full | Partial | Unknown | Unknown | The WinSetStyle and WinSetExStyle functions change the style or extended style of the specified window, respectively. |
| WinSetRegion() | Full | Partial | Unknown | Unknown | The WinSetRegion function changes the shape of the specified window to be the specified rectangle, ellipse, or polygon. |
| WinSetStyle() | Full | Partial | Unknown | Unknown | The WinSetStyle and WinSetExStyle functions change the style or extended style of the specified window, respectively. |
| WinSetTitle() | Full | Partial | Unknown | Unknown | The WinSetTitle function changes the title of the specified window. |
| WinSetTransColor() | Full | Partial | Unknown | Unknown | The WinSetTransColor function makes all pixels of the chosen color invisible inside the specified window. |
| WinSetTransparent() | Full | Partial | Unknown | Unknown | The WinSetTransparent function makes the specified window semi-transparent. |
| WinShow() | Full | Partial | Unknown | Unknown | The WinShow function unhides the specified window. |
| WinWait() | Full | Partial | Unknown | Unknown | The WinWait function waits until the specified window exists. |
| WinWaitActive() | Full | Partial | Unknown | Unknown | The WinWaitActive and WinWaitNotActive functions wait until the specified window is active or not active. |
| WinWaitClose() | Full | Partial | Unknown | Unknown | The WinWaitClose function waits until no matching windows can be found. |
| WinWaitNotActive() | Full | Partial | Unknown | Unknown | The WinWaitActive and WinWaitNotActive functions wait until the specified window is active or not active. |
