# Keysharp

Keysharp is a cross-platform C# implementation of [AutoHotkey v2](https://www.autohotkey.com/docs/v2/). It parses AutoHotkey-style scripts, compiles them through .NET, and runs them on Windows, Linux, and macOS. 

Keysharp is under active development and is not yet recommended for production systems. Windows has the broadest compatibility; Linux and macOS support continue to improve.

Please note that while Keysharp is AutoHotkey v2 compatible, most existing AutoHotkey scripts are not usable cross-platform. Most scripts will run without modifications on Windows, but anything using DllCall, COM, or registry will not work on Linux and Mac.

- [Download releases](https://github.com/Descolada/keysharp/releases)
- [Platform compatibility](#platform-compatibility)
- [AutoHotkey v2 differences](docs/reference.md#differences)
- [Build from source](#building-from-source)

## Quick Start

Install Keysharp (platform-specific instructions are below), create a script named `hello.ks`:

```ahk
MsgBox("Hello from Keysharp!")
```

Double-click the file to run, or run it from a terminal:

```sh
keysharp hello.ks
```

Keysharp supports:

- `.ahk` and `.ks` source scripts
- `.cks` compiled scripts
- `Keyview`, a lightweight script editor with live validation, saving, running, and `.cks` compilation

## Windows

### Install and Run

Download and run the Windows MSI installer from the [Releases](https://github.com/Descolada/keysharp/releases) page. The installer:

- Associates `.ks` and `.cks` files with Keysharp.
- Adds an **Edit** action for opening scripts in Keyview.
- Adds a **Compile** action for `.ahk` and `.ks` source scripts.
- Can optionally add Keysharp to `PATH`.

Double-click a script or, if you added Keysharp to `PATH`, run:

```powershell
Keysharp.exe hello.ks
```

For a portable installation, download the ZIP release and run `Keysharp.exe` from the extracted folder (note that .NET 10 runtime is still required).

### Uninstall

Remove Keysharp through **Settings → Apps → Installed apps**.

### Limitations

Windows currently provides the broadest AutoHotkey v2 compatibility. See the [compatibility matrix](#platform-compatibility) and [detailed differences](docs/reference.md#differences).

## Linux

### Install and Run

Download and extract the Linux installer tarball from the [Releases](https://github.com/Descolada/keysharp/releases) page, then run:

```sh
sudo bash ./install.sh
keysharp hello.ks
```

The root installation adds desktop file associations, terminal commands, and privileged helpers for reliable input hooks, input synthesis, and Wayland screen capture. Run `bash ./install.sh` without `sudo` for a user-local installation under `~/.local`; this skips privileged helpers.

### Uninstall

Run the uninstaller included with the extracted release, using the same privilege level used during installation:

```sh
sudo bash ./uninstall.sh   # root installation
bash ./uninstall.sh        # user-local installation
```

### Limitations

Linux behavior depends on the display server and compositor. Root helpers substantially improve input handling; Wayland automation and capture support vary by desktop. See [Linux platform details](#linux-platform-details).

## macOS

### Install and Run

Download either package from the [Releases](https://github.com/Descolada/keysharp/releases) page:

- **DMG:** double-click `Install.command` to copy `Keysharp.app` and `Keyview.app` into Applications, then optionally install the `keysharp`/`keyview` terminal commands and the VS Code AutoHotkey v2 compatibility shim. (You can also drag the apps into Applications manually; this does not require administrator priviledges.)
- **PKG:** installs both apps system-wide and prompts to install the `keysharp`/`keyview` terminal commands and the VS Code AutoHotkey v2 compatibility shim.

Because current packages are not notarized, first launch each app using **right-click → Open**. Alternatively:

```sh
xattr -dr com.apple.quarantine /Applications/Keysharp.app
xattr -dr com.apple.quarantine /Applications/Keyview.app
```

Keysharp requests **Input Monitoring**, **Accessibility**, and **Screen Recording** permissions when features require them.

### Uninstall

- **DMG installation:** double-click `Uninstall.command` in the mounted DMG.
- **PKG installation:** run `sudo keysharp-uninstall`.

### Limitations

macOS support is under active development. Window automation depends on Accessibility permission, and some GUI behavior differs from Windows. See [macOS platform details](#macos-platform-details).

## Using Keysharp

Common commands:

```sh
keysharp script.ahk
keysharp --validate script.ahk
keysharp --transpile script.ahk
keysharp --compile script.ahk
keysharp --compile exe script.ahk
```

`--compile` creates a `.cks` compiled script beside the source file. `--compile exe` creates a standalone launcher and its required .NET files. Keysharp compiles scripts into .NET assemblies before running them.

### Keyview

Keyview provides a graphical editing workflow with live validation and generated C# output. Open a source file in Keyview to edit, save, run, or compile it into a `.cks` file beside the source. Opening Keyview without a file starts an autosaved scratchpad.

### Visual Studio Code

For thqby's **AutoHotkey v2 Language Support** extension, open the VS Code Command Palette (`Ctrl+Shift+P` on Windows/Linux or `Cmd+Shift+P` on macOS, or use **View → Command Palette…**), then select **Preferences: Open User Settings (JSON)**. Add this entry to make VS Code treat `.ks` files as AutoHotkey v2 files:

```json
"files.associations": {
  "*.ks": "ahk2"
}
```

In the extension settings, set **AutoHotkey2: Interpreter Path** to the appropriate executable:

- **Windows:** use the installed `Keysharp.exe` path directly, usually `C:\Program Files\Keysharp\Keysharp.exe`.
- **Linux:** the extension requires an `.exe` filename. Create one by running `mkdir -p ~/.local/bin && ln -sf "$(command -v keysharp)" ~/.local/bin/AutoHotkey.exe` in the terminal, then use `/home/YOUR_USERNAME/.local/bin/AutoHotkey.exe`.
- **macOS DMG:** answer "Yes" to the VS Code shim prompt in `Install.command`, then use `/Users/YOUR_USERNAME/.local/bin/AutoHotkey.exe`.
- **macOS PKG or manual setup:** create the same compatibility name with `mkdir -p ~/.local/bin && ln -sf /Applications/Keysharp.app/Contents/MacOS/Keysharp ~/.local/bin/AutoHotkey.exe`.

thqby's VS Code extension is designed for AutoHotkey on Windows. Static language features and running scripts are the most compatible features on Linux and macOS; Windows-specific debugging, help, and compiler integration will not work.

## Platform Compatibility

This is a concise overview. See [the full capability matrix](docs/capabilities.md) and [detailed compatibility differences](docs/reference.md#differences).

`Full` means the feature is generally usable on that platform. `Partial` means known functionality or compatibility gaps remain.

| Capability | Windows | Linux | macOS |
|---|---|---|---|
| Parser and script execution | Full | Full | Full |
| File and directory operations | Full | Partial | Partial |
| Hotkeys and hotstrings | Full | Partial | Partial |
| Keyboard and mouse input | Full | Partial | Partial |
| GUI windows | Full | Partial | Partial |
| Foreign window automation | Full | Partial | Partial |
| Screen capture and pixel functions | Full | Partial | Partial |
| Registry and COM APIs | Full | Unsupported | Unsupported |

### Linux Platform Details

| Platform / compositor | Summary |
|---|---|
| **X11** | Strongest Linux window-management support; root helpers improve hooks and input synthesis but can be used without root. |
| **Wayland — GNOME** | Uses a GNOME Shell extension and privileged helpers for broader automation and capture support. |
| **Wayland — KDE Plasma** | Uses KWin integration and privileged helpers for broader automation and capture support. |
| **Other Wayland compositors** | Support depends on available foreign-toplevel and screencopy protocols. |

### macOS Platform Details

| Permission | Required for |
|---|---|
| **Input Monitoring** | Hotkeys, hotstrings, and reading keyboard/mouse input |
| **Accessibility** | Sending input and controlling or querying other apps |
| **Screen Recording** | Screen capture, pixel functions, and image search |

Permissions can be requested explicitly in a script:

```ahk
#Requires capability InputMonitoring, ScreenCapture
```

## Building From Source

Keysharp targets .NET 10. Linux and macOS builds also require the sibling [Keysharp Eto fork](https://github.com/Descolada/Eto/tree/Keysharp).

### Windows

Open the project with Visual Studio 2022 with the .NET 10 SDK (recommended option), or with Visual Studio Code (lighter option), open `Keysharp.sln`, and build the solution.

To build the release MSI installer and portable ZIP from PowerShell:

```powershell
.\Keysharp.Install\package-windows.ps1
```

Building the MSI requires Visual Studio with the **Microsoft Visual Studio Installer Projects** extension. Packaging output is written to `dist\`.

### Linux

```sh
git clone -b Keysharp https://github.com/Descolada/Eto.git ../Eto
bash ./Keysharp.Install/package-linux.sh
```

### macOS

```sh
git clone -b Keysharp https://github.com/Descolada/Eto.git ../Eto
bash ./Keysharp.Install/package-macos.sh
```

Packaging output is written to `dist/`.

## AutoHotkey v2 Compatibility

Keysharp aims for AutoHotkey v2 compatibility while using a different architecture and .NET runtime. Some behavior differs, some APIs are platform-specific, and Keysharp also provides additions.

See the [complete compatibility and behavioral reference](docs/reference.md#differences), including:

- Behavioral and runtime differences
- Syntax differences
- Additions and improvements
- Removed or unsupported functionality

## Acknowledgements

Keysharp builds on work from IronAHK, AutoHotkey, Eto.Forms, Scintilla, NAudio, and many other open-source projects and community contributors. See the [full acknowledgements](docs/reference.md#code-acknowledgements).

## Contributing and Support

Please use the repository issue tracker for bug reports, compatibility gaps, and feature requests.
