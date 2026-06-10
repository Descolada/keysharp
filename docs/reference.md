# Keysharp Detailed Reference

This document contains detailed platform, implementation, and AutoHotkey v2 compatibility notes. For the concise project introduction and quick-start instructions, see the [main README](../README.md).

Jump directly to:

- [Windows platform support](#windows-platform-support)
- [Windows setup](#installing-on-windows)
- [Linux platform support](#linux-platform-support)
- [Linux setup](#installing-on-linux)
- [macOS platform support](#macos-platform-support)
- [MacOS setup](#installing-on-macos)
- [Cross-platform capability matrix](#cross-platform-capability-matrix)
- [AutoHotkey v2 differences](#differences)
- [Code acknowledgements](#code-acknowledgements)

## How do I get set up? ##
* If .NET 10 is not installed on your machine, download it from the [.NET 10 download page](https://dotnet.microsoft.com/en-us/download/dotnet/10.0).

## Windows Platform Support

Windows has the best feature implementation rate and very high AutoHotkey v2 compatibility. The largest differences are:
* Object destruction logic, which happens non-deterministically due to C# garbage collection
* GUI rendering, because WinForms is used as the backend. WinForms uses lazy initialization which means for example that GUIs render blank before they've been showed. Additionally WinForms has some differences concerning Z-ordering of controls and label rendering.

### Installing on Windows ###
* Download and run the Keysharp installer from the [Releases](https://github.com/Descolada/keysharp/releases) page.
	+ The install path can be optionally added to the $PATH varible, so you can run it from the command line from anywhere.
		+ The path entry will be removed upon uninstall.
	+ It also registers Keysharp.exe as the default program to open `.ks` and `.cks` files. So after installing, double click any `.ks` source script or `.cks` compiled script to run it.
	+ On Windows, the installer adds a right-click "Compile" action for `.ahk` and `.ks` source scripts, which writes a `.cks` compiled script next to the source file.

### Portable run on Windows ###
* Download and unzip the zip file from the [Releases](https://github.com/Descolada/keysharp/releases) page.
	+ CD to the unzipped folder.
	+ Run `.\Keysharp.exe yourfilename.ahk`

### Building from source on Windows ###
* Download the latest version of [Visual Studio 2022](https://visualstudio.microsoft.com/vs/community/).
	+ This should install .NET 10. If it doesn't, you need to install it manually from the link above.
* Open Keysharp.sln
* Build all (building the installer is not necessary).
* CD to bin\release\net10.0-windows (or \debug\, depending whether using Debug or Release mode)
* Run `.\Keysharp.exe yourtestfile.ahk`

## Linux Platform Support ##
Linux support is in active development. The following table summarises what works and what requires user action.

| Platform / compositor | Without root access | Root-access helpers add / enable | Notes |
|---|---|---|---|
| **X11** | Full window management and screen capture; partial input hooks with X11, input synthesis, and hotkeys/hotstrings | Full input hooks/synthesis, `BlockInput`, reliable hotkeys/hotstrings via `keysharp-inputd` | Root mainly upgrades input control |
| **Wayland – GNOME** | Full window management and mouse synthesis via GNOME Shell extension | Keyboard synthesis, hooks, `BlockInput`, hotkeys/hotstrings via `keysharp-inputd`; screen capture permission via `keysharp-screencap` | Shell extension must be enabled which requires a logout after install; `keysharp-screencap` must be installed for capture authorization |
| **Wayland – KWin / KDE Plasma** | Full window management via KWin scripting; mouse synthesis via FakeInput | Screen capture via `keysharp-screencap`; keyboard synthesis, hooks, `BlockInput`, hotkeys/hotstrings via `keysharp-inputd` | `keysharp-screencap` must be root-owned setuid with desktop file |
| **Wayland – other compositors**<br>Sway, Hyprland, COSMIC, Wayfire, labwc, etc. | Protocol-dependent window listing, active-window detection, activation, and screen capture | Input synthesis, hooks, `BlockInput`, hotkeys/hotstrings via `keysharp-inputd` | Depends on foreign-toplevel and screencopy protocol support |

### Installing on Linux ###
* Download and extract the Keysharp installer tarball from the [Releases](https://github.com/Descolada/keysharp/releases) page.
+ Either run the .deb file to install, or run the install.sh script with sudo: `sudo sh ./install.sh` which does the following:
	+ Installs the Linux runtime dependencies and attempts to install the .NET 10 runtime if it is missing.
		+ If your distribution does not provide the .NET 10 runtime package, install it manually using the instructions [here](https://learn.microsoft.com/en-us/dotnet/core/install/linux).
	+ Registers Keysharp as the default program to open `.ks` and `.cks` files. So after installing, double click any `.ks` source script or `.cks` compiled script to run it.
	+ Creates a symlink at `/usr/local/bin/keysharp` so you can run it from the command line from anywhere.
	+ Installs root-owned `keysharp-inputd` daemon for evdev device access and a uinput virtual device, enabling reliable keyboard/mouse hooks, input synthesis, and `BlockInput` on both X11 and Wayland.
	+ Installs root-owned `keysharp-screencap` which enables Wayland screen capture: on KWin it is directly needed for screen capture; on GNOME it acts as the trust gate for screen capture.
	+ Installs a GNOME Shell extension (`keysharp@keysharp.io`) for the invoking desktop user, which is required in GNOME for screen capture, mouse location queries, and window automation. This requires a logout or reboot to take effect. If GNOME is not installed then this has no effect.
+ Without sudo, Keysharp is installed under `$HOME/.local` and the privileged helpers are skipped. Linux input hooks/synthesis and Wayland screen capture will be unavailable until a root install is performed.

For thqby's **AutoHotkey v2 Language Support** VS Code extension, create an `AutoHotkey.exe` compatibility symlink because the extension requires that filename:
```sh
mkdir -p ~/.local/bin
ln -sf "$(command -v keysharp)" ~/.local/bin/AutoHotkey.exe
```
Then use `/home/YOUR_USERNAME/.local/bin/AutoHotkey.exe` as the interpreter path in the extension. The extension is designed for AutoHotkey on Windows, so static language features and running scripts are the most compatible features; Windows-specific debugging, help, and compiler integration will not work.

### Building from source on Linux ###
* Install the .NET 10 SDK (not just the runtime) as described in "Installing on Linux"
* In the same parent folder as keysharp, clone the Keysharp branch of [Descolada's fork of Eto](https://github.com/Descolada/Eto/tree/Keysharp); if keysharp is at `foo/keysharp`, clone Eto to `foo/Eto` by running `git clone -b Keysharp https://github.com/Descolada/Eto.git` from within `foo`.
* Run `Keysharp.Install/package-linux.sh`
* A build folder and a tarball of said build folder will be placed in `dist/keysharp-linux-x64` and `dist/keysharp-linux-x64.tar.gz` respectively. If `dpkg-deb` is installed, a Debian package such as `dist/keysharp_<version>_amd64.deb` will also be created.
* The build folder and tarball can be installed via the steps in "Installing on Linux" above. The `.deb` can be installed with `sudo apt install ./dist/keysharp_<version>_amd64.deb`.
* The folder and tarball are portable so both source repositories can be safely deleted.
* **Alternatively**, on arch-based systems keysharp is provided as an [AUR package](https://aur.archlinux.org/packages/keysharp-git)

## macOS Platform Support ##
macOS support is in active development. The following table summarises what works and what requires user action.

| Feature | Status | Notes |
|---|---|---|
| Script execution | Working | Parser, compiler, and runtime are functional |
| Hotkeys / Hotstrings | Working | Requires **Input Monitoring** permission on first use. #/Win maps to the Command key, !/Alt maps to the Option key |
| Keyboard & mouse send | Working | Requires **Accessibility** permission on first use |
| Global keyboard/mouse hooks | Working | Requires **Input Monitoring** permission on first use |
| GUI windows | Working | Eto.Forms backend; some controls differ from Windows |
| Screen capture / pixel functions | Working | Requires **Screen Recording** permission on first use |
| Window management | Partial | Accessibility API; foreign-app control requires permission |
| Registry APIs | Not supported | Windows-only |
| COM APIs | Not supported | Windows-only |

Permissions are requested automatically when first needed, or up front with `#Requires capability` (see [Additions/Improvements](#additionsimprovements) below). Grant them in **System Settings → Privacy & Security**.

### Installing on macOS ###

Two packages are available on the [Releases](https://github.com/Descolada/keysharp/releases) page.

#### DMG — user install, no administrator password required

The DMG contains `Keysharp.app`, `Keyview.app`, `Install.command`, and `Uninstall.command`.

Double-click **Install.command** (it runs in Terminal) to:
1. Copy `Keysharp.app` and `Keyview.app` to `/Applications`.
2. Optionally install the `keysharp` and `keyview` terminal commands to `/usr/local/bin` (requests an administrator password).
3. Optionally install the VS Code AutoHotkey v2 extension compatibility shim at `~/.local/bin/AutoHotkey.exe`.

Alternatively, drag both apps to the **Applications** folder shortcut inside the DMG, or to any folder of your choice (e.g. `~/Applications/`).

**First-launch Gatekeeper workaround** — because the app is not notarized, macOS will block it on the first open. Right-click (or Control-click) `Keysharp.app` → **Open**, then click **Open** in the prompt. Do the same for `Keyview.app`. After that one-time step the apps open normally.

Alternatively, in Terminal:
```sh
xattr -dr com.apple.quarantine /Applications/Keysharp.app
xattr -dr com.apple.quarantine /Applications/Keyview.app
```

The equivalent manual setup for the terminal commands is:
```sh
sudo ln -sf /Applications/Keysharp.app/Contents/MacOS/Keysharp /usr/local/bin/keysharp
sudo ln -sf /Applications/Keyview.app/Contents/MacOS/Keyview /usr/local/bin/keyview
```

Without terminal commands, use `Keyview.app` to write and run scripts. Keyview finds the sibling `Keysharp` binary automatically, whether the apps live in `/Applications/`, `~/Applications/`, or directly on a mounted DMG volume.

For thqby's **AutoHotkey v2 Language Support** VS Code extension, answer "Yes" to the compatibility shim prompt in `Install.command`. It creates `~/.local/bin/AutoHotkey.exe`; then use `/Users/YOUR_USERNAME/.local/bin/AutoHotkey.exe` as the interpreter path in the extension.

The extension is designed for AutoHotkey on Windows, so static language features and running scripts are the most compatible features; Windows-specific debugging, help, and compiler integration will not work.

#### PKG — system install, requires administrator password

The `.pkg` installer places both apps in `/Applications/`. After copying the apps, it shows two prompts (as the logged-in user):
- Whether to install the `keysharp` and `keyview` terminal commands in `/usr/local/bin`.
- Whether to install the VS Code AutoHotkey v2 extension compatibility shim at `~/.local/bin/AutoHotkey.exe`.

Install from Finder by double-clicking the `.pkg` and following the installer prompts (you will be asked for your administrator password), or from Terminal:
```sh
sudo installer -pkg Keysharp-osx-arm64.pkg -target /
```

Apply the same first-launch Gatekeeper workaround as above for each app after installation.

#### macOS permissions

On first use, macOS will ask for several permissions:

| Permission | Required for |
|---|---|
| **Input Monitoring** | Hotkeys, hotstrings, and reading keyboard/mouse input |
| **Accessibility** | Controlling and querying other application windows |
| **Screen Recording** | `PixelGetColor`, `ImageSearch`, `ImageCapture` |

Grant each permission in **System Settings → Privacy & Security** when prompted. Keysharp will wait up to 60 seconds for each permission to be granted before continuing, but usually the script will have to be restarted after granting capabilities. You can also request permissions explicitly at the top of a script:
```ahk
#Requires capability InputMonitoring, ScreenCapture
```

#### Uninstalling

Both the DMG and the PKG bundle an uninstaller that removes the app(s), terminal commands, the package receipt (PKG installs), and stored settings/cache data — no manual `rm` commands needed.

**DMG install** — open the mounted DMG and double-click **Uninstall.command** (it runs in Terminal). Eject the DMG and empty the Trash afterwards if you also dragged the apps there yourself — macOS Launch Services can still launch apps sitting in the Trash until it's emptied.

**PKG install** — run the bundled uninstaller from a terminal:
```sh
sudo keysharp-uninstall
```

If you removed the apps by hand instead and `.ks`/`.ahk` files still open in Keysharp, the apps are most likely still sitting in the Trash — empty it, since Launch Services can launch apps from there even though Spotlight does not index it.

macOS may retain granted permissions (Accessibility, Input Monitoring, Screen Recording) even after the app is removed. To revoke them, open **System Settings → Privacy & Security**, select each category, and remove any Keysharp or Keyview entries — the uninstaller cannot do this for you.

If you reinstall a different build (e.g. switching between a locally-built, ad-hoc-signed, and notarized version) and permissions seem stuck — toggles that won't stay on, or the app not appearing/disappearing from a permission list — the old TCC grant may be tied to the previous code signature. Reset *every* permission category for Keysharp/Keyview with `tccutil`:
```sh
tccutil reset All org.keysharp.keysharp
tccutil reset All org.keysharp.keyview
```
`All` clears every TCC entry for that bundle ID (Accessibility, Input Monitoring, Screen Recording, and any others macOS may have recorded), for all versions of the app sharing that bundle ID. macOS will prompt again next time each permission is needed.

### Building from source on macOS ###

* Install the [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0).
* In the same parent folder as `keysharp`, clone the Keysharp branch of [Descolada's fork of Eto](https://github.com/Descolada/Eto/tree/Keysharp). If `keysharp` is at `foo/keysharp`, clone Eto to `foo/Eto`:
  ```sh
  git clone -b Keysharp https://github.com/Descolada/Eto.git
  ```
* Run the packaging script to produce a release DMG and PKG:
  ```sh
  bash ./Keysharp.Install/package-macos.sh
  ```
  Output is written to `dist/`:
  - `Keysharp-osx-arm64.dmg` — drag-and-drop user install
  - `Keysharp-osx-arm64.pkg` — system install with terminal commands
* For a quick debug run without packaging, build and run directly:
  ```sh
  dotnet build Keyview/Keyview.csproj -c Debug
  open bin/Debug/net10.0/osx-arm64/Keyview.app
  ```
* The signing and notarization steps are skipped by default (no developer account required). To enable ad-hoc signing for local testing: `ADHOC_SIGN=true bash ./Keysharp.Install/package-macos.sh`.

## Cross-Platform Capability Matrix

This is a concise view of which AutoHotkey 2.1 features Keysharp implements. For full details and current notes, see [capabilities.md](capabilities.md).

<!-- CAPABILITIES_OVERVIEW:START -->
Status legend:
- 🟢 Full: Implemented and generally usable
- 🟡 Partial: Implemented with known limitations or gaps
- 🟠 Planned: Not implemented yet, but intended
- 🔴 Unsupported: Not supported
- ⚪ Unknown: Not yet verified

| Capability | Windows | Linux (X11) | Linux (Wayland) | macOS | Notes |
|---|---|---|---|---|---|
| Parser and runtime execution | 🟢 Full | 🟢 Full | 🟢 Full | 🟢 Full | Parser, preprocessing, and script execution runtime are implemented. |
| Directives and preprocessing | 🟢 Full | 🟢 Full | 🟢 Full | 🟢 Full | OS-specific directives supported via compile constants. |
| File and directory operations | 🟢 Full | 🟢 Full | ⚪ Unknown | ⚪ Unknown | macOS recycle/trash and privacy-scoped file access still evolving. |
| Keyboard/Mouse send (synthetic input) | 🟢 Full | 🟡 Partial | ⚪ Unknown | ⚪ Unknown | Requires platform permissions on macOS. |
| Global keyboard hooks | 🟢 Full | 🟡 Partial | ⚪ Unknown | ⚪ Unknown | macOS behavior is still being aligned. |
| Global mouse hooks | 🟢 Full | 🟡 Partial | ⚪ Unknown | ⚪ Unknown | Suppression/injection semantics differ by platform. |
| Hotkeys/Hotstrings | 🟢 Full | 🟡 Partial | ⚪ Unknown | ⚪ Unknown | Depends on hook and key-state parity. |
| Script-owned window management | 🟢 Full | 🟡 Partial | ⚪ Unknown | ⚪ Unknown | Built on WinForms/Eto; some controls and behavior still differ. |
| Foreign window management (non-Keysharp apps) | 🟢 Full | 🟡 Partial | ⚪ Unknown | ⚪ Unknown | On Linux, Control* functions are not supported for foreign apps; use the included AtSpi library for cross-process window/control interaction. macOS currently relies on Accessibility APIs with permission requirements. |
| Tray icon and menu | 🟢 Full | 🟡 Partial | ⚪ Unknown | ⚪ Unknown | Tray/menu behavior varies by desktop environment and platform APIs. |
| Screen capture and pixel/image functions | 🟢 Full | 🟡 Partial | ⚪ Unknown | ⚪ Unknown | Pixel/image search and screen capture depend on platform-specific backends. |
| Clipboard | 🟢 Full | 🟡 Partial | ⚪ Unknown | ⚪ Unknown | Clipboard functionality is implemented with platform-specific limitations outside Windows. |
| Sound APIs | 🟢 Full | 🟡 Partial | ⚪ Unknown | ⚪ Unknown | Audio device/endpoint support differs by platform. |
| Registry APIs | 🟢 Full | 🔴 Unsupported | 🔴 Unsupported | 🔴 Unsupported | Windows Registry APIs are Windows-only. |
| COM APIs | 🟢 Full | 🔴 Unsupported | 🔴 Unsupported | 🔴 Unsupported | COM is available on Windows only. |
<!-- CAPABILITIES_OVERVIEW:END -->

## Overview ##

Keysharp is a fork and improvement of the abandoned IronAHK project, which itself was a C# re-write of the C++ AutoHotkey project.

Keysharp runs on Windows, Linux, and macOS. Windows currently has the broadest compatibility, while Linux and macOS support continue to improve.

This project is in the alpha testing stage and is not yet recommended for production systems.

Some general notes about Keysharp's implementation of the [AutoHotkey v2 specification](https://www.autohotkey.com/docs/v2/):

* The operation of Keysharp is different than AHK. While AHK is an interpreted scripting language, Keysharp actually creates a compiled .NET executable and runs it.

* The process for reading and running a script is:
	+ Pass the script to Keysharp.exe which parses it and generates a Document Object Model (DOM) tree.
	+ The DOM compiler generates C# code for a single program.
	+ The C# program code is compiled into an in-memory executable.
	+ The executable is ran in memory as a new process.
	+ Optionally output the generated C# code to a .cs file for debugging purposes with the `-transpile` option, without running the script.
	+ Optionally output the generated executable to an .exe file for running standalone in the future with the `-compile exe` option, without running the script.

* Keysharp supports `.ahk` and `.ks` source files and `.cks` compiled scripts. Installers associate supported files with Keysharp and provide an editing action through Keyview where supported.

* Keyview is the graphical script editor included with Keysharp. It shows generated C# and validation feedback while editing, and supports opening, saving, running, and compiling source files.
	+ It gives real-time feedback so you can see immediately when you have a syntax error.
	+ It is recommended that you use this to write code.
	+ The features are very primitive at the moment, and help improving it would be greatly appreciated.

Despite our best efforts to remain compatible with the AHK v2 spec, there are differences. Some of these differences are a reduction in functionality, and others are an increase. There are also slight syntax changes.

## Differences: ##

###	Behaviors/Functionality: ###
* Linux support is partial. See [Linux Platform Support](#linux-platform-support) above for a detailed breakdown by display server and compositor.
	+ Control commands only work on windows created by the running Keysharp process. This is because "controls" don't exist in Linux the same way they do in Windows.
		+ As an alternative it's recommended to use [AtSpi.ks](https://github.com/Descolada/keysharp/blob/master/Keysharp/Scripts/AtSpi.ks): running it directly displays AtSpiViewer which can be used to inspect windows, and it also contains methods to manipulate windows and controls similarly to Acc/UIA in Windows.
	+ GUI support is mostly implemented, but some controls are missing or incomplete.
	+ Registry functions are not supported.
* Keysharp follows the .NET memory model.
	+ There is no variable caching with strings vs numbers. All variables are C# objects.
	+ Values not stored in variables are like regular variables, only eligible to be freed once they go out of scope.
```
	FileOpen("test.txt", "w").Write("hello") ; The temporary file object does not get deleted at the end of the line, only possibly at the end of the current scope.
```
	+ Object destructors/finalizers are called at a random point in time, and `Collect()` should be used if they need to be invoked predictably.
	+ Object destructors (`__Delete`) are implemented with C# finalizers, which are quite heavy-weight and are not automatically present for all objects. The finalizer state is determined at object creation based on whether `__Delete` is present in the prototype chain, or at the point `__Delete` is defined. If `__Delete` is defined later in the prototype chain then instance finalizers are not automatically activated; the activation can be forced manually by temporarily reassigning a different base for the instance.
	+ On script exit all non-local variables are enumerated, finalizers disabled, and `__Delete` called if present. This also includes class static variables.
* AHK says about the inc/dec ++/-- operators on empty variables: "Due to backward compatibility, the operators ++ and -- treat blank variables as zero, but only when they are alone on a line".
	+ Keysharp breaks this and will instead create a variable, initialize it to zero, then increment it.
	+ For example, a file with nothing but the line `x++` in it, will end with a variable named x which has the value of 1.
* Function objects behave mostly the same as in AHK.
	+ The underlying function object class is named `FuncObj`. This was named so, instead of `Func`, because C# already contains a built in class named `Func`. `MsgBox is Func` is still supported though, as is `MsgBox is FuncObj`.
	+ Function objects can be created by passing the name of the function as as a direct reference or as a string to `Func()`.
	+ Most built-in functions
* Error stack traces start from where the error was thrown, not where it was constructed.
* `StrPtr()` works slightly differently because C# strings are constant.
	+ `StrPtr(variable)` returns a custom `StringBuffer` object which is entangled with the original string. When this object is used with DllCall, NumPut etc, then the `StringBuffer` is used as the pointer, and the entangled string is updated after the function call.
	+ `StrPtr("literal")` with a literal string will pin the string from garbage collection and return the actual address of the string. This string must not be modified, and should be freed after use with `ObjFree()`.
	+ Instead of `StrPtr` it is recommended to use a `StringBuffer` instance instead.
* `CallbackCreate()` does not support the `CDecl/C` option because the program will be run in 64-bit mode.
	+ Passing string pointers to `DllCall()` when passing a created callback is recommended against. See explanation above under `StrPtr()`.
	+ Usage of the created callback will be inefficient, so usage of `CallbackCreate()` is discouraged.
* Deleting a tab via `GuiCtrl.Delete()` does not reassociate the controls that it contains with the next tab. Instead, they are all deleted.
* The size and positioning of some GUI components will be slightly different than AHK because WinForms uses different defaults.
	+ There is an additional positioning option `xc` and `yc` which position the control relative to the container. For example inside a tab `xc+10` would position the control 10 pixels from the left side of the tab control.
	+ GroupBoxes can be used as containers by calling `GuiObj.UseGroup(groupbox)`, and to exit the group call `GuiObj.UseGroup()`.
* The class name for statusbar/statusstrip objects created by Keysharp is "WindowsForms10.Window.8.app.0.2b89eaa_r3_ad1". However, for accessing a statusbar created by another, non .NET program, the class name is still "msctls_statusbar321".
* Using the class name with `ClassNN` on .NET controls gives long, version specific names such as "WindowsForms10.Window.8.app.0.2b89eaa_r3_ad1" for a statusbar/statusstrip.
	+ This is because a simpler class names can't be specified in code the way they can in AHK with calls to `CreatWindowEx()`.
	+ These long names may change from machine to machine, and may change for the same GUI if you edit its code.
	+ There is an new `NetClassNN` property alongside `ClassNN`.
	+ The class names of all GUI controls created in Keysharp are prefixed with the string "Keysharp", eg: `KeysharpButton`, `KeysharpEdit` etc...
	+ `NetClassNN` will give values like 'KeysharpButton6' (note that the final digit is the same for the `ClassNN` and the `NetClassNN`).
	+ Due to the added simplicity, `NetClassNN` is preferred over `ClassNN` for WinForms controls created with Keysharp.
	+ This is used internally in the index operator for the Gui class, where if a control with a matching `ClassNN` is not found, then controls are searched for their `NetClassNN` values.
* `TrayTip()` functions slightly differently.
	+ Muting the sound played by the tip is not supported with the `Mute` option. The sound will be whatever the user has configured in their system settings.
	+ The option `4` to use the program's tray icon is not supported. It is always shown in the title of the tip.
	+ The option `32` to use the large version of the program's tray icon is not supported. Windows will always show the small version.
* `Sleep()` works, but uses `Application.DoEvents()` internally which is not a good programming practice and can lead to hard to solve bugs.
	+ For this reason, it's recommended that users use timers for repeated execution rather than a loop with calls to `Sleep()`.
* The Optimization section of the `#HotIf` documentation doesn't apply to Keysharp because it uses compiled code, thus the expressions are never re-evaluated.
* The `#ErrorStdOut` directive will not print to the console unless piping is used. For example:
	+ `.\Keysharp.exe .\test.ahk | more`
	+ `.\Keysharp.exe .\test.ahk | more > out.txt`
* Menu items, whether shown or not, have no impact on threading.
* `AddStandard()` detects menu items by string, instead of ID, because WinForms doesn't expose the ID.
* `ControlMove()` and `ControlSetPos()` operate relative to their immediate parent, which may not be the main window if they are contained in a nested control.
* Function objects are much slower than direct function calls due to the need to use reflection. So for repeated function calls, such as those involving math, it's best to use the functions directly.
* The `File` object is internally named `KeysharpFile` so that it doesn't conflict with `System.IO.File`.
* In `SetTimer()`, the priority is not in the range -2147483648 and 2147483647, instead it is only 0-4.
* If a `ComObject` with `VarType` of `VT_DISPATCH` and a null pointer value is assinged a non-null pointer value, its type does not change. The `Ptr` member remains available.
* `A_LineNumber` is not a reliable indicator of the line number because the preprocessor condenses the code before parsing and compiling it.
* `ObjPtr()` returns an IUnknown `ComValue` with the pointer wrapped in it, whereas `ObjPtrAddRef()` returns a raw pointer.
* Pointers returned by `StrPtr()` must be freed by passing the value to a new function named `ObjFree()`.
	+ `StrPtr()` does not return the address of the string, instead it returns the address of a copy of the bytes of the string.
* `Sleep()` will not do any sleeping if shutdown has been initiated.
* The concat-assign operator `.=` is not optimized to modify the left operand inplace, meaning calling it in a loop will be very slow. If many concats are required then use a `StringBuffer` instead.
* `/Debug` command line switch is not implemented.
* If a script is compiled then none of Keysharp or AutoHotkey command parameters apply.

###	Syntax: ###
* `DllCall()` has the following caveats:
	+ Use `Ptr` and `StringBuffer` for double pointer parameters such as `LPTSTR*`. This is recommended over the use of `StrPtr()`.
* `ImageSearch()` takes an options string as a fifth parameter, rather than inserted in the string before the `imageFile` parameter.
* A leading plus sign on numeric values, such as `+123` or `+0x123` is not supported. It has no effect anyway, so just omit it.
* AHK `unset` is implemented as `null`. `IsSet(x)` is equivalent to `x == null`.
* Use of the dereference syntax `%expression%` inside functions is highly discouraged. This is because using it will cause every function call to construct an object which captures all local variables, and depending on the number of variables the performance loss may be significant.
* `Goto` statements cannot use any type of variable. They must be labels known at compile time and function just like goto statements in C#.
* `Goto` statements being called as a function like `Goto("Label")` are not supported. Instead, just use `goto Label`.
* The `#Requires` directive differs in the following ways:
	+ In addition to supporting `AutoHotkey`, it also supports `Keysharp`.
	+ Sub versions such as -alpha and -beta are not supported. Only the four numerical values values contained in the assembly version in the form of `0.0.0.0` are supported.
	+ A new `capability` form requests one or more platform permissions at script startup, before hotkeys are registered, so the user sees a single combined prompt rather than separate prompts on first use:
		```
		#Requires capability InputMonitoring, ScreenCapture
		```
		Recognised capability names (case-insensitive, aliases accepted):
		| Name | Aliases | Description |
		|---|---|---|
		| `InputMonitoring` | `hook`, `inputhook` | Monitor keyboard and mouse input (required for hotkeys/hotstrings) |
		| `InputInjection` | `synthinput`, `sendinput` | Synthesize keyboard and mouse input (`Send`, `Click`, etc.) |
		| `BlockInput` | | Suppress input events |
		| `ScreenCapture` | `capture`, `imagecapture` | Capture screen pixels (`PixelGetColor`, `ImageSearch`, `ImageCapture`) |
		| `AccessibilityAutomation` | `accessibility`, `automation` | Access UI accessibility trees (AT-SPI on Linux) |
* For any `__Enum()` class method, it should have a parameter value of 2 when returning `Array` or `Map`, since their enumerators have two fields.
* RegEx uses PCRE2 engine powered by the PCRE.NET library. There are a few limitations compared to the AutoHotkey implementation:
	+ The following options are different:
		+ S: Studies the pattern to try improve its performance.
			+ This is not supported. All RegEx objects are internally created with the `PcreOptions.Compiled` option specified, so performance should be reasonable.
		+ u: This new option disables optimizations PCRE2_NO_AUTO_POSSESS, PCRE2_NO_START_OPTIMIZE, and PCRE2_NO_DOTSTAR_ANCHOR. This option can be useful when using callouts, since these optimizations might prevent some callouts from happening.
	+ Callouts differ in a few ways:
		+ The callout function cannot be a closure, it must be a top-level function
		+ Callouts do not set `A_EventInfo`
		+ The callout function must be a top-level function
		+ A named callout must be enclosed in "", '', or {}

###	Additions/Improvements: ###
* In addition to the AHK module, a KS module has been added which contains extra variabes and methods added to Keysharp. Accessing them requires using the `import` statement.
	+ These include all new classes, functions and variables mentioned here (eg `HashMap`, `Sinh` etc)
	+ Note: class method/property additions are always included and do not need to be imported (eg `String` or `Buffer` extra methods)
* A new class named `StringBuffer` which can be used for passing string memory to `DllCall()` which will be written to inside of the call.
	+ There are two methods for creating a `StringBuffer`:
		+ `StringBuffer(str := "") => StringBuffer`: Creates a `StringBuffer` with a string of `str` and a capacity of 256.
		+ `StringBuffer(str, capacity) => StringBuffer`: Creates a `StringBuffer` with a string of `str` and a capacity of `Max(16, capacity)`.
	+ `StringBuffer` is implicitly castable to `String`.
```
	sb := StringBuffer("hello")
	MsgBox(sb) ; Shows "hello".
```
	+ As an alternative to passing a `Buffer` object with type `Ptr` to a function which will allocate and place string data into the buffer, the caller can instead use a `StringBuffer` object to hold the new string.
	+ This relieves the caller of having to create a `Buffer` object, then call `StrGet()` on the new string data.
	+ `wsprintf()` is one such example.
```
	; Using a Buffer:
	ZeroPaddedNumber := Buffer(20)
	DllCall("wsprintf", "Ptr", ZeroPaddedNumber, "Str", "%010d", "Int", 432, "Cdecl")
	MsgBox(StrGet(ZeroPaddedNumber)) ; Shows "0000000432".

	; Using a StringBuffer:
	sb := StringBuffer()
	DllCall("wsprintf", "Ptr", sb, "Str", "%010d", "Int", 432, "Cdecl")
	MsgBox(sb) ; No need to use StrGet() anymore.
```
	+ `StringBuffer` internally uses a `StringBuilder` which is how C# P/Invoke handles string pointers.

* Hyperbolic versions of the trigonometric functions:
	+ `Sinh(value) => Double`
	+ `Cosh(value) => Double`
	+ `Tanh(value) => Double`
* A new `HashMap` class has been added which extends `Map` and does not perform sorting before enumeration.
* A New function `RandomSeed(Integer)` to reinitialize the random number generator for the current thread with a specified numerical seed.
* New file functions:
	+ `FileDirName(filename) => String` to return the full path to filename, without the actual filename or trailing directory separator character.
	+ `FileFullPath(filename) => String` to return the full path to filename.
* New window functions:
	+ `WinFromPoint(x, y)` to get the window at a specific screen position.
	+ `WinMaximizeAll()` to maximize all windows.
* A new function `ShowDebug()` to show the main window and focus the debug output tab.
* A new function `OutputDebugLine()` which is the same as `OutputDebug()` but appends a linebreak at the end of the string.
* New string functions:
	+ `Base64Decode(str) => Array` to convert a Base64 string to a Buffer containing the decoded bytes.
	+ `Base64Encode(value) => String` to convert a byte array to a Base64 string.
	+ `NormalizeEol(str, eol) => String` to make all line endings in a string match the value passed in, or the default for the current environment.
	+ `Join(separator, params*) => String` to join each parameter together as a string, separated by `separator`.
		+ Pass params as `params*` if it's a collection.
* New string methods:
	+ `String.StartsWith(token [,comparison]) => Boolean` and `String.EndsWith(token [,comparison]) => Boolean` to determine if the beginning or end of a string start/end with a given string.
* New RegEx functions `RegExMatchCs()` and `RegExReplaceCs()` which use the C# style regular expression syntax rather than PCRE2.
	+ `OutputVar` in `RegExMatchCs()` will be of type `RegExMatchInfoCs`.
	+ PCRE exceptions are not thrown when there is an error, instead C# regex exceptions are thrown.
	+ To learn more about C# regular expressions, see [here](https://learn.microsoft.com/en-us/dotnet/standard/base-types/regular-expressions).
	+ The following options are different:
		+ -A: Forces the pattern to be anchored; that is, it can match only at the start of Haystack. Under most conditions, this is equivalent to explicitly anchoring the pattern by means such as `^`.
			+ -This is not supported, instead just use `^` or `\A` in your regex string.

		+ -C: Enables the auto-callout mode.
			+ -This is not supported. C# regular expressions don't support calling an event handler for each match. You must manually iterate through the matches yourself.

		+ -D: Forces dollar-sign ($) to match at the very end of Haystack, even if Haystack's last item is a newline. Without this option, $ instead matches right before the final newline (if there is one). Note: This option is ignored when the `m` option is present.
			+ -This is not supported, instead just use `$`. However, this will only match `\n`, not `\r\n`. To match the `CR/LF` character combination, include `\r?$` in the regular expression pattern.

		+ -J: Allows duplicate named subpatterns.
			+ -This is not supported.

		+ -S: Studies the pattern to try improve its performance.
			+ -This is not supported. All RegEx objects are internally created with the `RegexOptions.Compiled` option specified, so performance should be reasonable.

		+ -U: Ungreedy.
			+ -This is not supported, instead use `?` after: `*, ?, +, and {min,max}`.

		+ -X: Enables PCRE features that are incompatible with Perl.
			+ -This is not supported because it's Perl specific.

		+ ``` `a `n `r ```: Causes specific characters to be recognized as newlines.
			+ -This is not supported.

		+ `\K` is not supported, instead, try using `(?<=abc)`.
* New function `FormatCs()` is an alternative to AHK `Format`. The syntax used in `Format()` is exactly that of `string.Format()` in C#, except with 1-based indexing. Traditional AHK style formatting is not supported.
	+ Full documentation for the formatting rules can be found [here](https://learn.microsoft.com/en-us/dotnet/api/system.string.format).
* New function `RequestCapabilities(capabilities*) => Object` requests one or more platform permissions and returns an object describing the outcome.
	+ `capabilities`: zero or more capability name strings, each optionally comma- or space-delimited. Recognised names are the same as for `#Requires capability` above.
	+ When called with no arguments, returns the current status of all capabilities without prompting.
	+ Returns an `Object` with a property for each capability (`"Granted"`, `"Denied"`, `"NotApplicable"`, or `"Unsupported"`) and a `Granted` property (`1`/`0`) indicating whether every *requested* capability was granted or not applicable.
	+ On Linux, all input-related capabilities (`InputMonitoring`, `InputInjection`, `BlockInput`) plus `ScreenCapture` are batched into a single `keysharp-inputd` prompt when requested together, so the user sees at most one dialog per call.
	```
	caps := RequestCapabilities("InputMonitoring", "ScreenCapture")
	if caps.Granted
	    MsgBox "All permissions granted"
	MsgBox caps.ScreenCapture   ; "Granted", "Denied", "NotApplicable", or "Unsupported"

	; Query current status without prompting:
	caps := RequestCapabilities()
	```
	+ Prefer `#Requires capability` for scripts that need permissions from startup. Use `RequestCapabilities` directly when you need to check or request permissions at a specific point in script execution, or when you want to inspect the current status.
* New function `ImageCapture(x, y, width, height [, filename]) => Bitmap` can be used to return a bitmap screenshot of an area of the screen and optionally save it to file.
* New clipboard functions:
	+ `CopyImageToClipboard(filename [,options])` is supported which copies an image to the clipboard.
		+ Uses the same arguments as `LoadPicture()`.
		+ This is a fully separate copy and does not share any handle, or perform any file locking with the original image being read.
	+ `IsClipboardEmpty() => Boolean` returns whether the clipboard is truly empty.
* A new function `Collect()` which calls `GC.Collect()` to force a memory collection.
	+ This rarely ever has to be used in properly written code.
	+ Calling `Collect()` may not always have an immediate effect. For example if an object is assigned to a variable inside a function and then the variable is assigned an empty string then calling `Collect()` after it will not cause the object destructor to be called. Only after the function has returned will the object be considered to have no references and `Collect()` starts working.
	+ If an object destructor needs to be called immediately then it may better to call `Object.__Delete()` manually.
* A new function `RunScript(code, callbackOrAsync?, name := "*", executable?)` which dynamically parses, compiles, and runs the provided code. The default name `"*"` reflects that the script is fed to the target process via StdIn rather than loaded from disk. Optionally provide the script name; whether to run it asynchronously (non-unset non-zero `callbackOrAsync` causes async run without a callback); an executable path to run the compiled assembly (defaults to the current process).
  If `callbackOrAsync` is provided a function then it is called after the script has finished with the `ProcessInfo` as the only argument. Over multiple runs `RunScript` is faster than running the process manually and writing to StdIn because of assembly and compilation caching.
  This function returns a `ProcessInfo` object encapsulating info and I/O for the process. Available properties: `HasExited`, `ExitCode`, `ExitTime` (YYYYMMDDHH24MISS), `StdOut`, `StdErr`, `StdIn` (as `KeysharpFile`). Available methods: `Kill()`.
* New accessors:
	+ `A_AllowTimers` returns whether timers are allowed or not. It's also easier to set this value rather than call `Thread("NoTimers")`.
	+ `A_CommandLine` returns the command line string. This is preferred over passing `GetCommandLine` to `DllCall()` as noted above.
	+ `A_DefaultHotstringCaseSensitive` returns the default hotstring case sensitivity mode.
	+ `A_DefaultHotstringConformToCase` returns the default hotstring case conformity mode.
	+ `A_DefaultHotstringDetectWhenInsideWord` returns the default hotstring word detection mode.
	+ `A_DefaultHotstringDoBackspace` returns the default hotstring backspacing mode.
	+ `A_DefaultHotstringDoReset` returns the default hotstring resetting mode.
	+ `A_DefaultHotstringEndCharRequired` returns the default hotstring ending character mode.
	+ `A_DefaultHotstringEndChars` returns the default hotstring ending characters.
	+ `A_DefaultHotstringKeyDelay` returns the default hotstring key delay length in milliseconds.
	+ `A_DefaultHotstringNoMouse` returns whether mouse clicks are prevented from resetting the hotstring recognizer because `#Hotstring NoMouse` was specified.
	+ `A_DefaultHotstringOmitEndChar` returns the default hotstring ending character replacement mode.
	+ `A_DefaultHotstringPriority` returns the default hotstring priority.
	+ `A_DefaultHotstringSendMode` returns the default hotstring sending mode.
	+ `A_DefaultHotstringSendRaw` returns the default hotstring raw sending mode.
	+ `A_DirSeparator` returns the directory separator character which is `\` on Windows and `/` elsewhere.
	+ `A_GuiTheme` gets/sets the application-wide WinForms GUI theme. Accepted values: `Classic`, `System`, `Dark`. Windows only. Can be imported directly from the `Ks` module: `import { A_GuiTheme } from Ks`.
	+ `A_HasExited` returns whether shutdown has been initiated.
	+ `A_KeysharpCorePath` provides the full path to the Keysharp.Core.dll file.
	+ `A_LoopRegValue` which makes it easy to get a registry value when using `Loop Reg`.
	+ `A_MaxThreads` returns the value `n` specified with `#MaxThreads n`.
	+ `A_NoTrayIcon` returns whether the tray icon was hidden with #NoTrayIcon.
	+ `A_NowMs`/`A_NowUTCMs` returns the current local/UTC time formatted to include milliseconds like so "YYYYMMDDHH24MISS.ff".
		+ These can be used with `DateAdd()`/`DateDiff()` using `"L"` for the `TimeUnits` parameter.
	+ `A_SuspendExempt` returns whether subsequent hotkeys and hotstrings will be exmpt from suspension because `#SuspendExempt true` was specified.
	+ `A_TotalScreenHeight` returns the total height in pixels of the virtual screen.
	+ `A_TotalScreenWidth` returns the total width in pixels of the virtual screen.
	+ `A_UseHook` returns the value `n` specified with `#UseHook n`.
	+ `A_WinActivateForce` returns whether the forceful method of activating a window is in effect because `#WinActivateForce` was specified.
	+ `A_WorkAreaHeight` returns the height of the working area of the primary screen.
	+ `A_WorkAreaWidth` returns the width of the working area of the primary screen.
	+ `A_Timers` returns a `Map` of (Func, boolean) pairs where Func is the function object of the timer and boolean is the enabled state of the associated timer.
* New functions for encrypting/decrypting an object:
	+ Encrypt or decrypt an object using the AES algorithm: `AES(value, key, decrypt := false) => Buffer`.
	+ Generate hash values using various algorithms: `MD5(value) => String`, `SHA1(value) => String`, `SHA256(value) => String`, `SHA384(value) => String`, `SHA512(value) => String`.
	+ Calculate the CRC32 polynomial of an object: `CRC32(value) => Integer`.
	+ Generate a secure cryptographic random number: `SecureRandom(min, max) => Decimal`.
* New class and functions for managing real threads which are not related to the green threads that are used for the rest of the project.
	+ A `RealThread` is created by calling the `RealThread` class static instance.
```
	class RealThread
	{
		static Call(funcobj [, params*]) => RealThread` Call `funcobj` in a real thread, optionally passing `params` to it, and return a `RealThread` object.
		RealThread(Task)
		RealThread ContinueWith(funcobj [, params*]) => RealThread ; Call `funcobj` after the task completes, optionally passing `params` to it and return a new `RealThread` object for the continuation thread.
		Wait([timeout]) ; Wait until the thread object which was passed to the constructor completes. Optionally return after a specified timeout period in milliseconds elapses.
	}
```
* `LockRun(lockobj, funcobj [, params*])` Call `funcobj` inside of a lock on `lockobj`, optionally passing `params` to it.
	+ `lockobj` must be initialized to some value, such as an empty string.
* New function `Mail(recipients, subject, message, options)` to send an email.
	+ `recipients`: A list of receivers of the message.
	+ `subject`: Subject of the message.
	+ `message`: Message body.
	+ `options`: A `Map` with any the following optional key/value pairs:
		+ "attachments": A string or `Array` of strings of file paths to send as attachments.
		+ "bcc": A string or `Array` of strings of blind carbon copy recipients.
		+ "cc": A string or `Array` of strings of carbon copy recipients.
		+ "from": A string of comma separated from address.
		+ "replyto": A string of comma separated reply address.
		+ "host": The SMTP client hostname and port string in the form "hostname:port".
		+ "header": A string of additional header information.
* Experimental `Clr` class has been added which aims to provide CLR interop with regular AutoHotkey syntax, meaning easy access to CLR libraries.
	+ `Clr.Load(asmOrPath)` loads a CLR assembly from a dll file or assembly name, and returns a `ManagedAssembly` or `ManagedNamespace` object. Example: `System := Clr.Load("System")`
		+ `ManagedNamespace` can be accessed with property access syntax to get namespaces and types (`ManagedType`). Example: `linq := System.Linq.Enumerable`
		+ `ManagedType` may be accessed for static methods/properties, or called to create a new `ManagedInstance`.
		+ `ManagedInstance` may be accessed with normal AutoHotkey syntax for properties, methods, and indexer access. Example: `linq.Where(nums, isOdd)`
		+ Basic type marshalling between AutoHotkey and CLR is supported (including function objects), more complicated types may not currently work.
	+ `Clr.GetNamespaceName(ManagedNamespace)` returns the full intenal namespace name of the namespace wrapped by `ManagedNamespace`.
	+ `Clr.GetTypeName(ManagedType)` returns the full internal type name of the type wrapped by `ManagedType`.
* `Map` internally uses a real hashmap, which means item access, insertions and removals are faster, which is especially true for larger datasets. To keep at least partial compatibility with AutoHotkey the `Map` object is copied and sorted before enumeration, which means modifying the `Map` during enumeration will not have the same effect as in AHK.
* The spread operator `*` may be used multiple times in one function call. `MyFunc(arr1*, arr2*)` is allowed.
* Buffer has an `__Item[]` indexer which can be used to read a byte at a 1-based offset.
* Buffer has `ToHex()`, `ToBase64()`, and `ToByteArray()` methods which can be used to convert the contents to string (hex or base64), or a byte-array to for example write to a file.
* New methods for `Array`:
	+ `Add(value) => Integer` : Adds a single element to the array.
		+ This should be more efficient than `Push(values*)` when adding a single item because it's not variadic. It also returns the length of the array after the add completes.
	+ `Filter(callback: (value [, index]) => Boolean) => Array`: Applies a filter to each element of the array and returns a new array consisting of all elements for which `callback` returned true.
	+ `FindIndex(callback: (value [, index]) => Boolean, startIndex := 1) => Integer`: Returns the index of the first element for which `callback` returned true, starting at `startIndex`. Returns 0 if `callback` never returned true.
		+ If `startIndex` is negative, the search starts from the end of the array and moves toward the beginning.
	+ `IndexOf(value, startIndex := 1) => Integer`: Returns the index of the first item in the array which equals value, starting at `startIndex`. Returns 0 if value is not found.
		+ If `startIndex` is negative, the search starts from the end of the array and moves toward the beginning.
	+ `Join(separator := ',') => String`: Joins together the string representation of all array elements, separated by `separator`.
	+ `MapTo(callback: (value [, index]) => Any, startIndex := 1) => Array`: Maps each element of the array, starting at `startIndex`, into a new array where the mapping in `callback` performs some operation.
```
	lam := (x, i) => x * i
	arr := [10, 20, 30]
	arr2 := arr.MapTo(lam)
```
	+ `Sort(callback: (a, b) => Integer) => this`: Sorts the array in place. The callback should use the usual logic of returning -1 when `a < b`, 0 when `a == b` and 1 otherwise.
* `Run/RunWait()` can take an extra string for the argument instead of appending it to the program name string. However, the original functionality still works too.
	+ The new signature is: `Run/RunWait(target [, workingDir, options, &outputVarPID, args])`.
* When specifying colors for GUI components, the list of supported known colors can be found [here](https://learn.microsoft.com/en-us/dotnet/api/system.drawing.knowncolor).
* `ListView` supports a new method `DeleteCol(col) => Boolean` to remove a column. The value returned indicates whether the column was found and deleted.
* New methods and properties for `Menu`:
	+ `HideItem()`, `ShowItem()` and `ToggleItemVis()` which can show, hide or toggle the visibility of a specific menu item.
	+ `MenuItemName()` to get the name of a menu item, rather than having to use `DllCall()`.
	+ `SetForeColor()` to set the fore (text) color of a menu item.
	+ `MenuItemCount` to get the number of sub items within a menu.
* `Picture` supports clearing the picture by setting the `Value` property to empty.
* New options for `UpDown`:
	+ These relieve the caller of having to use native Windows API calls.
	+ `IncrementXXX` to specify an increment other than 1.
		+ `MyGui.Add("UpDown", "x5 y55 vMyNud Increment10", 1)`
	+ `Hex` to show the numeric value in hexadecimal.
* `TabControl` supports a new method `SetTabIcon(tabIndex, imageIndex)` to relieve the caller of having to use `SendMessage()`.
* `TreeView` supports a new method `GetNode(nodeIndex) => TreeNode` which retrieves a raw winforms TreeNode object based on a passed in ID.
* Gui controls support taking a boolean `Autosize` (default: `false`) argument in the `Add()` method to allow them to optimally size themselves.
* `Gui` has a new property named `Visible` which get/set whether the window is visible or not.
* `EnvUpdate()` is retained to provide for a cross platform way to update environment variables.
* The 40 character limit for hotstring abbreviations has been removed. There is no limit to the length.
* `FileGetSize()` supports `G` and `T` for gigabytes and terabytes.
* `DateAdd()` and `DateDiff()` support taking a value of `"L"` for the `TimeUnits` parameter to add miLliseconds or return the elapsed time in milliseconds, respectively.
	+ See the new accessors `A_NowMs`/`A_NowUTCMs`.
* `SubStr()` uses a default of 1 for the second parameter, `startingPos`, to relieve the caller of always having to specify it.
* The v1 `Map` methods `MaxIndex()` and `MinIndex()` are still supported. They are also supported for `Array`.
* Rich text boxes are supported by passing `RichEdit` to `Gui.Add()`. The same options from `Edit` are supported with the following caveats:
	+ `Multiline` is true by default.
	+ `WantReturn` and `Password` are not supported.
	+ `Uppercase` and `Lowercase` are supported, but only for key presses, not for pasting.
	+ The `Gui.Control.Value` property will only get/set the displayed text of the control. To get/set the raw rich text, use the new property `Gui.Control.RichText`.
		+ Use `AltSubmit` with `Submit()` to get the raw rich text.
		+ Attempting to use `Gui.Control.RichText` on any control other than `RichEdit` will throw an exception.
* Loading icons from .NET DLLs is supported by passing the name of the icon resource in place of the icon number.
	+ To set the tray icon to the built in suspended icon:
		+ `TraySetIcon(A_KeysharpCorePath, "Keysharp_s.ico")`
	+ To set a menu item to the same:
		+ `parentMenu.SetIcon("Menu caption", A_KeysharpCorePath, "Keysharp_s.ico")`
* When sending a string through `SendMessage()` using the `WM_COPYDATA` message type, the caller is no longer responsible for creating the special `COPYDATA` struct.
	+ Instead, just pass `WM_COPYDATA (0x4A)` as the message type and the string as the `lparam`, and `SendMessage()` will handle it internally.
	+ Note, this will send the string as UTF-16 Unicode. If you need to send to a program which expects ASCII, then you'll need to manually create the `COPYDATA` struct.
* In addition to using `#ClipboardTimeout`, a new accessor named `A_ClipboardTimeout` can be used at any point in the program to get or set that value.
* A compiled script can be reloaded.
	+ AHK does not support reloading a compiled script.
* `A_EventInfo` is not limited to positive values when reporting the mouse wheel scroll amount.
	+ When scrolling up, the value will be positive, and negative when scrolling down.
* `Log(number, base := 10)` is by default base 10, but it can accept a double as the second parameter to specify a custom base.
* In `SetTimer()`:
	+ In the callback function, `A_EventInfo` is set to the function object used to create the timer.
	+ This allows the handler to alter the timer by passing the function object back to another call to `SetTimer()`.
	+ Timers are not disabled when the program menu is shown.
* Reference parameters for functions using `&` are supported with the following improvements and caveats:
	+ Passing class members, array indexes and map values by reference is supported.
		+ `func(&classobj.classprop)`
		+ `func(&myarray[5])`
		+ `func(&mymap["mykey"])`
	+ Reference parameters in functions work for class methods, global functions, built in functions, lambdas and function objects.
		+ Lambdas with a single reference parameter can be declared with no parentheses:
			+ `lam := &a => a := (a * 2)`
	+ When passing a class member variable as a dynamic reference to a function from within another function of that same class, the `this` prefix must be used:
```
	class myclass
	{
		x := 11
		y11 := 0

		myclassreffunc(&val)
		{
		}

		callmyclassreffunc()
		{
			myclassreffunc(&this.y%x%) ; Use this.
		}
	}
```
* `KeysharpObject` has a new method `OwnPropCount()` which corresponds to the global function `ObjOwnPropCount()`.
* `ComObjConnect()` takes an optional third parameter as a boolean (default: `false`) which specifies whether to write additional information to the debug output tab when events are received.
* Preprocessor directives are supported using the familiar syntax of C#.
	+ `#if symbol` is used to enable a section of code if symbol is defined.
	+ By default, the following are defined:
		+ `WINDOWS` if you are running the script on Microsoft Windows.
		+ `LINUX` if you are running the script on linux.
		+ `KEYSHARP`
	+ `#else` can be used to take an alternate path if the preceding `#if` evaluates to false.
	+ `#elif symbol` can be used to evaluate another symbol if the preceding `#if` or `#elif` evaluate to false.
	+ All preprocessor blocks must end with an `#endif`
	+ New preprocessor symbols can be defined using `#define symbol`.
	+ Logical statements can be evaluated using the operators `&&`, `||` and `!`.
	+ Evaluation of preprocessor statements are case insensitive.
	+ Some examples are:
```
		#if WINDOWS
			MsgBox("Windows")
		#elif LINUX
			MsgBox("linux")
		#else
			MsgBox("Unsupported OS")
		#endif

		#if !(WINDOWS || LINUX)
			MsgBox("Unsupported OS")
		#endif

		#if 1
			MsgBox("Always true")
		#endif

		#if 0
			MsgBox("Always false")
		#endif

		#define NEW_DEFINE
		#if NEW_DEFINE
			MsgBox("True because of new definition")
		#endif
```
* New preprocessor directives have been added.
	+ `#HookMutexName <name>` allows renaming the mutex objects created to detect keyboard and mouse hooks in other running scripts. The default name is "Keysharp".
	+ Assembly description attributes may be changed with the following directives (with the desired value as the only argument of the directive):
		+ `#AssemblyName`
		+ `#AssemblyDescription`
		+ `#AssemblyConfiguration`
		+ `#AssemblyCompany`
		+ `#AssemblyProduct`
		+ `#AssemblyCopyright`
		+ `#AssemblyTrademark`
		+ `#AssemblyVersion`
* Command line switches may start with `/`, `-` or `--`, and must appear before the script or assembly input. After the input is found, all remaining arguments are passed to the script or assembly entry point.
* Command line switches
    - `--script`
	  Causes a compiled script to ignore its main code and instead executes the provided script. For this to apply, `--script` must be the first command line argument.
	  Example: `CompiledScript.exe /script /ErrorStdOut MyScript.ahk "Script's arg 1"`
	- `--version`, `-v`
	  Displays Keysharp version.
	- `--transpile`
	  Outputs a .cs file with the same name as the script containing the code which was used to compile. This is the same code displayed in Keyview. The script is not run.
	- `--compile exe [--dest <path>] <script>`
	  Outputs a .exe file which can be ran as standalone from Keysharp (but still requires .NET 10). If `--dest` is a folder, the executable is written there using the script's base name. If `--dest` is a file name, that name and folder are used for the output. The script is not run.
	- `--compile exe-min [--dest <path>] <script>`
	  Same as `--compile exe` but the number of file dependencies is reduced by embedding them in Scriptname.dll. The resulting program will have five dependencies: Scriptname.exe, Scriptname.dll, Keysharp.Core.dll, Scriptname.deps.json, and Scriptname.runtime.config. To get a truly single-file executable the script must be compiled as a C# project, for example as Keysharp.OutputTest in the Keysharp solution. The script is not run.
	- `--compile <script>`
	  Outputs the compiled raw assembly bytes to a `.cks` file with the same base name as the script. The script is not run.
	- `--compile asm [--dest <path|*>] <script>`
	  Outputs the compiled raw assembly bytes to a `.cks` file. If `--dest` is a folder, the `.cks` file is written there using the script's base name. If `--dest` is a file name, that file is used. If `--dest` is `*`, output is written to StdOut. `dll` is also accepted as an alias for `asm`. The script is not run.
	- `--validate`, `/validate`
	  Compiles but does not run the script. Can be used to check for load-time errors.
	- `--asm`, `--assembly`
	  Reads pre-compiled assembly code from the file or StdIn and runs it. If omitted, the default type `Keysharp.CompiledMain.Program` and method `Main` are used. A custom entry point can be specified with `--asm:Namespace.Type.Method`, splitting the type and method at the last dot. A `.cks` or `.dll` input is treated as an assembly even when `--asm` is omitted.
	  Examples: `Keysharp.exe --asm Script.cks arg1 arg2`, `Keysharp.exe Script.cks arg1 arg2`, `Keysharp.exe --asm:My.Namespace.Type.Main Script.dll arg1 arg2`
	- `--daemon`, `--daemon stop`, `--daemon ping <script>`
	  Starts, stops, or diagnostics-checks the background compile daemon. Plain script runs use the daemon by default in release builds, but not in debug builds. Set `KEYSHARP_DAEMON=1` (or `true`, `yes`, `on`) to force daemon use, or `KEYSHARP_DAEMON=0` (or `false`, `no`, `off`) to bypass it.

###	Removals: ###
* `ListLines()` is non-functional because C# doesn't support it.
* The `R`, `Dn` or `Tn` parameters in `FormatTime()` are not supported, except for 0x80000000 to disallow user overrides.
	+ If you want to specify a particular format or order, do it in the format argument. There is no need or reason to have one argument alter the other.
	+ [Here](https://docs.microsoft.com/en-us/dotnet/standard/base-types/custom-date-and-time-format-strings) is a list of the C# style DateTime formatters which are supported.
* Static text controls do not send the Windows `API WM_CTLCOLORSTATIC (0x0138)` message to their parent controls like they do in AHK.
* Double click handlers for buttons are not supported.
* UpDown controls with paired buddy controls are not supported. Keysharp just uses the regular NumericUpDown control in C#.
	+ The options `16`, `Horz` and `Wrap` have no effect.
	+ The min and max values cannot be swapped.
* `IL_Create()` only takes one parameter: `largeIcons`. `initialCount` and `growCount` are no longer needed because memory is handled internally.
* `LoadPicture()` does not accept a `GDI+` argument as an option.
* For slider events, the second parameter passed to the event handler will always be `0` because it's not possible to retrieve the method by which the slider was moved in C#.
* `PixelGetColor()` ignores the `mode` parameter.
* `DirSelect()`:
	+ The `1`, `3` and `5` options don't apply and the New Folder button will always be shown.
	+ Modality cannot be configured with `Gui.Opt("+OwnDialogs")` because the folder select dialog is always modal.
	+ Restricting folder navigation is not supported.
* `MsgBox()`:
	+ The modality options are ignored.
	+ The message box will block the window that launched it by default. If `+OwnDialogs` is in effect, then all GUIs in the script are blocked until it is dismissed.
	+ System modal dialog boxes are no longer supported on Windows.
	+ The help option `16384` is ignored.
* Only `Tab3` is supported, no older tab functionality is present.
* When adding a `ListView`, the `Count` option is not supported because C# can't preallocate memory for a `ListView`.
* The address of a variable cannot be taken using the reference operator.
	+ It returns a VarRef object as in AHK.
* `OnMessage()` doesn't observe any of the behavior mentioned in the documentation regarding the message check interval because it's implemented in a different way.
	+ A GUI object is required for `OnMessage()` to be used.
* Pausing a script is not supported because a Keysharp script is actually a running program.
	+ The pause menu item and `Pause()` function have been removed.
* `ObjAddRef()` and `ObjPtrAddRef()` do not have an effect for non-COM objects. Instead, use the following:
	+ `newref := theobj ; adds 1 to the reference count`
	+ `newref := "" ; subtracts 1 from the reference count`
* `#Warn` to enable/disable compiler warnings is not supported yet.
* The `/script` option for compiled scripts does not apply and is therefore not implemented.
* The Help and Window Spy menu items are not implemented yet.
* `Download()` only supports the `*0` option, and not any other numerical values.
* When passing `"Interrupt"` as the first argument to `Thread()`, the third argument for `LineCount` is not supported because Keysharp does not support line level awareness.
* Tooltips do not automatically disappear when clicking on them.

## Code acknowledgements ##

* The initial IronAHK developers 2010 - 2015
* [Logical string comparison](https://www.codeproject.com/Articles/22175/Sorting-Strings-for-Humans-with-IComparer), [cddl 1.0](https://opensource.org/licenses/cddl1.php)
* [Cross platform INI file processor](https://www.codeproject.com/articles/20053/a-complete-win-ini-file-utility-class)
* [P/Invoke calls](https://www.pinvoke.net)
* [Tuple splatter](https://github.com/iotalambda/TupleSplatter/tree/master/TupleSplatter)
* [Semver version parsing](https://github.com/WalkerCodeRanger/semver)
* [PictureBox derivation](https://www.codeproject.com/articles/717312/pixelbox-a-picturebox-with-configurable-interpolat)
* [Using SendMessage() with string](https://gist.github.com/BoyCook/5075907)
* [Program icon](https://thenounproject.com/icon/mechanical-keyboard-switch-2987081/) is a derivative of work by [Bamicon](https://thenounproject.com/bamicon/)
* [NAudio](https://github.com/naudio/NAudio)
* [Scintilla editor for .NET](https://github.com/desjarlais/Scintilla.NET)
* [Scintilla setup code in Keyview](https://github.com/robinrodricks/ScintillaNET.Demo)
* Various posts on [Stack Overflow](https://stackoverflow.com/)

## Who do I talk to? ##


Please make an account here and post a ticket.

