param(
    [string] $Configuration = "Release",
    [string] $RuntimeIdentifier = "win-x64",
    [string] $Version = "",
    [string] $DevenvPath = "",
    [switch] $SkipPublish
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = (Resolve-Path (Join-Path $scriptDir "..")).Path
$solution = Join-Path $root "Keysharp.sln"
$installerProject = "Keysharp.Install"
$distDir = Join-Path $root "dist"
$publishRoot = Join-Path $distDir "publish\$RuntimeIdentifier"
$stagingDir = Join-Path $distDir "staging\$RuntimeIdentifier"
$packageName = "Keysharp-$RuntimeIdentifier"
$packageDir = Join-Path $stagingDir $packageName
$appDir = Join-Path $packageDir "app"
$zipPath = Join-Path $distDir "$packageName.zip"
$etoDir = Join-Path (Split-Path -Parent $root) "Eto"
$pathMap = "$root=/_/keysharp"
if (Test-Path $etoDir) {
    $etoDir = (Resolve-Path $etoDir).Path
    $pathMap = "$pathMap%2c$etoDir=/_/Eto"
}
$installerProjectPath = Join-Path $root "Keysharp.Install\Keysharp.Install.vdproj"
$installerProjectOriginalContent = $null

if ($RuntimeIdentifier -ne "win-x64") {
    throw "The Visual Studio installer project is configured for win-x64 publish output. Use -RuntimeIdentifier win-x64."
}

function Resolve-KeysharpVersion {
    param([string] $ExplicitVersion)

    if ($ExplicitVersion) {
        return $ExplicitVersion
    }

    $propsPath = Join-Path $root "Directory.Build.props"
    if (Test-Path $propsPath) {
        $props = Get-Content -LiteralPath $propsPath -Raw
        $match = [regex]::Match($props, '<KeysharpVersion[^>]*>([^<]+)</KeysharpVersion>')
        if ($match.Success) {
            return $match.Groups[1].Value.Trim()
        }
    }

    throw "Could not determine KeysharpVersion. Pass -Version explicitly."
}

function Convert-ToMsiProductVersion {
    param([string] $AssemblyVersion)

    $parts = $AssemblyVersion.Split(".")
    if ($parts.Length -ne 4) {
        throw "Windows package version must have four numeric parts, for example 0.0.0.15. Got '$AssemblyVersion'."
    }

    foreach ($part in $parts) {
        if ($part -notmatch '^\d+$') {
            throw "Windows package version must have four numeric parts, for example 0.0.0.15. Got '$AssemblyVersion'."
        }
    }

    # Windows Installer ProductVersion has three fields. Preserve the historical
    # mapping from assembly 0.0.0.14 to MSI 0.0.14 by folding patch+revision.
    return "$($parts[0]).$($parts[1]).$([int]$parts[2] * 1000 + [int]$parts[3])"
}

function Update-InstallerProjectVersion {
    param(
        [string] $ProjectPath,
        [string] $AssemblyVersion
    )

    if (-not (Test-Path $ProjectPath)) {
        throw "Installer project was not found: $ProjectPath"
    }

    $msiVersion = Convert-ToMsiProductVersion $AssemblyVersion
    $content = Get-Content -LiteralPath $ProjectPath -Raw
    $content = [regex]::Replace($content, '"ProductVersion" = "8:[^"]+"', """ProductVersion"" = ""8:$msiVersion""")
    $content = [regex]::Replace($content, '"ProductCode" = "8:\{[^}]+\}"', """ProductCode"" = ""8:{$([guid]::NewGuid().ToString().ToUpperInvariant())}""")
    $content = [regex]::Replace($content, '"PackageCode" = "8:\{[^}]+\}"', """PackageCode"" = ""8:{$([guid]::NewGuid().ToString().ToUpperInvariant())}""")

    # The Keysharp/Keysharp.Core/Keyview product assemblies are packaged from their SourcePath, but the
    # setup project also stores a cached fusion display name whose Version is the assembly version captured
    # at authoring time. Left stale, a release built at a newer version registers a mismatched version string
    # in the MsiAssemblyName table, so rewrite the Version token for exactly those three (matched by name;
    # the framework/third-party entries keep their own, unrelated versions).
    $content = [regex]::Replace($content, '("AssemblyAsmDisplayName" = "8:(?:Keysharp\.Core|Keysharp|Keyview), Version=)[^,]+', "`${1}$AssemblyVersion")

    Set-Content -LiteralPath $ProjectPath -Value $content -NoNewline
}

function Assert-NoLocalPaths {
    param(
        [string] $ScanRoot,
        [string[]] $Patterns
    )

    $files = Get-ChildItem -Path $ScanRoot -Recurse -File -ErrorAction SilentlyContinue
    if (-not $files) {
        return
    }

    $matches = $files | Select-String -SimpleMatch -Pattern $Patterns -List -ErrorAction SilentlyContinue
    if ($matches) {
        $matches | Select-Object -First 20 | ForEach-Object {
            Write-Error "Local absolute path found in $($_.Path): $($_.Pattern)"
        }

        throw "Package payload contains local absolute paths. Rebuild with path mapping before packaging."
    }
}

function Find-Devenv {
    param([string] $ExplicitPath)

    if ($ExplicitPath) {
        if (Test-Path $ExplicitPath) {
            return (Resolve-Path $ExplicitPath).Path
        }

        throw "The supplied devenv.com path does not exist: $ExplicitPath"
    }

    if ($env:VSINSTALLDIR) {
        $candidate = Join-Path $env:VSINSTALLDIR "Common7\IDE\devenv.com"
        if (Test-Path $candidate) {
            return $candidate
        }
    }

    $programFilesX86 = [Environment]::GetFolderPath("ProgramFilesX86")
    if (-not $programFilesX86) {
        $programFilesX86 = ${env:ProgramFiles(x86)}
    }

    if ($programFilesX86) {
        $vswhere = Join-Path $programFilesX86 "Microsoft Visual Studio\Installer\vswhere.exe"
        if (Test-Path $vswhere) {
            $installPaths = & $vswhere -all -products * -requires Microsoft.Component.MSBuild -property installationPath
            foreach ($installPath in $installPaths) {
                if (-not $installPath) {
                    continue
                }

                $candidate = Join-Path $installPath "Common7\IDE\devenv.com"
                if (Test-Path $candidate) {
                    return $candidate
                }
            }
        }
    }

    $commonPaths = @()
    if ($env:ProgramFiles) {
        $commonPaths += @(
            "${env:ProgramFiles}\Microsoft Visual Studio\2022\Community\Common7\IDE\devenv.com",
            "${env:ProgramFiles}\Microsoft Visual Studio\2022\Professional\Common7\IDE\devenv.com",
            "${env:ProgramFiles}\Microsoft Visual Studio\2022\Enterprise\Common7\IDE\devenv.com"
        )
    }

    foreach ($candidate in $commonPaths) {
        if (Test-Path $candidate) {
            return $candidate
        }
    }

    throw "Could not find devenv.com. Install Visual Studio 2022 with Microsoft Visual Studio Installer Projects 2022, or pass -DevenvPath."
}

function Copy-DirectoryContents {
    param(
        [string] $Source,
        [string] $Destination
    )

    if (-not (Test-Path $Source)) {
        throw "Expected publish directory does not exist: $Source"
    }

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Copy-Item -Path (Join-Path $Source "*") -Destination $Destination -Recurse -Force
}

function Normalize-NativeAssets {
	param([string] $AppRoot)

	# Native assets have two different, loader-specific layout requirements:
	#
	#  * PCRE.NET.Native.dll is a NuGet "native" runtime asset listed in the
	#    .deps.json (runtimes/<rid>/native/...). For a RID-specific publish the host
	#    resolves that entry by probing the app ROOT (it strips the
	#    runtimes/<rid>/native prefix), so this DLL must live in the root. Putting it
	#    only under runtimes/<rid>/native fails with
	#    "Unable to load DLL 'PCRE.NET.Native'".
	#
	#  * Scintilla.dll / Lexilla.dll ship as MSBuild "Content" (NOT deps.json native
	#    assets) and Scintilla.NET locates them via a hard-coded relative path:
	#    <appbase>\runtimes\win-<arch>\native\. It never probes the root, so these
	#    must stay under runtimes/<rid>/native/.
	#
	# Merging the Keyview and Keysharp publishes can scatter copies between the root
	# and runtimes/<rid>/native/ (and the Scintilla.NET Content copy also drags in
	# the non-target RIDs). Stage each asset where its loader expects it, then rebuild
	# a clean runtimes tree containing only the target RID.
	$runtimesDir = Join-Path $AppRoot "runtimes"
	$nativeDir = Join-Path $runtimesDir "$RuntimeIdentifier\native"
	$ridAssets = @("Lexilla.dll", "Scintilla.dll")

	# Stash the Scintilla satellite libraries (prefer the target-RID copy).
	$tempNativeDir = Join-Path ([System.IO.Path]::GetTempPath()) "KeysharpNative_$([guid]::NewGuid().ToString('N'))"
	New-Item -ItemType Directory -Path $tempNativeDir -Force | Out-Null
	foreach ($name in $ridAssets) {
		$ridNative = Join-Path $nativeDir $name
		$rootNative = Join-Path $AppRoot $name
		if (Test-Path $ridNative) {
			Copy-Item -Path $ridNative -Destination (Join-Path $tempNativeDir $name) -Force
		}
		elseif (Test-Path $rootNative) {
			Copy-Item -Path $rootNative -Destination (Join-Path $tempNativeDir $name) -Force
		}
	}

	# Ensure PCRE.NET.Native.dll is in the app root.
	$pcreRoot = Join-Path $AppRoot "PCRE.NET.Native.dll"
	$pcreRid = Join-Path $nativeDir "PCRE.NET.Native.dll"
	if ((-not (Test-Path $pcreRoot)) -and (Test-Path $pcreRid)) {
		Copy-Item -Path $pcreRid -Destination $pcreRoot -Force
	}

	# Rebuild runtimes/<rid>/native containing only the target-RID Scintilla libraries.
	if (Test-Path $runtimesDir) {
		Remove-Item -Path $runtimesDir -Recurse -Force
	}
	New-Item -ItemType Directory -Path $nativeDir -Force | Out-Null
	foreach ($name in $ridAssets) {
		$staged = Join-Path $tempNativeDir $name
		if (Test-Path $staged) {
			Move-Item -Path $staged -Destination (Join-Path $nativeDir $name) -Force
		}
		# Drop any stray root copy of the Scintilla libraries (it never loads from root).
		Remove-Item -Path (Join-Path $AppRoot $name) -Force -ErrorAction SilentlyContinue
	}

	Remove-Item -Path $tempNativeDir -Recurse -Force -ErrorAction SilentlyContinue
}

function Compress-WindowsPackage {
    param(
        [string] $SourceRoot,
        [string] $DestinationPath
    )

    if (-not (Test-Path $SourceRoot)) {
        throw "Expected package app directory does not exist: $SourceRoot"
    }

    Remove-Item -LiteralPath $DestinationPath -Force -ErrorAction SilentlyContinue
    Compress-Archive -Path (Join-Path $SourceRoot "*") -DestinationPath $DestinationPath -Force
}

function Add-CloseInstancesCustomAction {
    # Windows refuses to overwrite a running .exe or a loaded .dll, so an upgrade or uninstall performed while
    # any Keysharp process is running (a script launched through Keysharp.exe, the compile daemon, or Keyview)
    # fails to replace Keysharp.exe / Keysharp.Core.dll, or defers them to a reboot - which can leave a
    # stale-version compile daemon serving against the new binaries. The Visual Studio setup project can only
    # sequence its managed custom actions after InstallFinalize (too late), so we inject an immediate custom
    # action directly into the built MSI, sequenced before InstallValidate, that closes those processes first.
    #
    # The action is an inline VBScript that terminates by image name via WMI rather than invoking the installed
    # Keysharp.exe: on an upgrade the binary on disk at this point is still the OLD build, and older builds do
    # not understand "--close-instances" (they would treat it as a missing script and pop a modal error,
    # hanging an unattended upgrade). Terminating by name is version-independent and never blocks.
    #
    # The invisible compile daemon ("Keysharp.exe --daemon") is always closed without prompting. The user's
    # visible processes (running scripts and the Keyview editor) are confirmed first in a full-UI install via
    # Session.Message; choosing No fails the action (it runs before InstallValidate, so nothing has changed yet),
    # and Windows Installer ends Setup with its own message - no extra dialog of our own. Silent/unattended
    # installs (UILevel < 5) cannot show a dialog, so they close everything unconditionally.
    param([string] $MsiPath)

    if (-not (Test-Path $MsiPath)) {
        throw "Built MSI not found for custom-action injection: $MsiPath"
    }

    Write-Host "Injecting close-instances custom action into $MsiPath..."

    # Set a 1-based MSI record field, choosing the integer or string overload by value type. Indexed COM
    # properties must be set through InvokeMember (PowerShell cannot assign them directly).
    $setField = {
        param($Record, [int] $Index, $Value)
        if ($Value -is [int]) {
            [void] $Record.GetType().InvokeMember('IntegerData', [System.Reflection.BindingFlags]::SetProperty, $null, $Record, @([int] $Index, [int] $Value))
        } else {
            [void] $Record.GetType().InvokeMember('StringData', [System.Reflection.BindingFlags]::SetProperty, $null, $Record, @([int] $Index, [string] $Value))
        }
    }

    # Run an INSERT/DELETE whose values are bound via '?' markers and an MSI Record, so nothing has to be
    # escaped into the SQL text. (The VBScript Target has newlines and quotes that MSI's SQL literal parser
    # rejects, which is why literal VALUES(...) fail here.)
    $exec = {
        param($Database, $Installer, [string] $Sql, [object[]] $Values)
        $view = $Database.OpenView($Sql)
        if ($Values -and $Values.Count -gt 0) {
            $record = $Installer.CreateRecord($Values.Count)
            for ($i = 0; $i -lt $Values.Count; $i++) { & $setField $record ($i + 1) $Values[$i] }
            $view.Execute($record)
            [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) | Out-Null
        } else {
            $view.Execute()
        }
        $view.Close()
        [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) | Out-Null
    }

    # NOTE: the MSI script-CA host does not expose WScript, so no Sleep/wait is available; Win32_Process.Terminate
    # tears the process down synchronously enough that its file locks are released before InstallValidate runs.
    # The prompt text is passed as record field 1 and emitted via the "[1]" template, NOT placed in the template
    # (field 0) itself - MsiFormatRecord would otherwise reparse it and drop part of the message. Session.Message
    # with INSTALLMESSAGE_USER (0x03000000) only shows the box when there is real UI; in silent installs it
    # returns without displaying, so the script closes everything unconditionally.
    $vbs = @'
On Error Resume Next
Dim wmi, procs, p, userCount, ui, rec, answer
Set wmi = GetObject("winmgmts:{impersonationLevel=impersonate}!\\.\root\cimv2")
Set procs = wmi.ExecQuery("SELECT ProcessId, Name, CommandLine FROM Win32_Process WHERE Name='Keysharp.exe' OR Name='Keyview.exe'")

' The compile daemon ("Keysharp.exe --daemon") is an invisible background process that must always match the
' installed binary, so close it unconditionally and without prompting. Count the visible user-facing processes
' (running scripts and the Keyview editor) so only those gate on the confirmation below.
userCount = 0
For Each p In procs
  If (p.Name = "Keysharp.exe") And (InStr(1, p.CommandLine, " --daemon", 1) > 0) Then
    p.Terminate
  Else
    userCount = userCount + 1
  End If
Next

If userCount > 0 Then
  ui = 0
  ui = CLng(Session.Property("UILevel"))
  If ui >= 5 Then
    Set rec = Session.Installer.CreateRecord(1)
    rec.StringData(0) = "[1]"
    rec.StringData(1) = "One or more Keysharp scripts (or the Keyview editor) are still running and must be closed to finish installing." & vbCrLf & vbCrLf & "Yes: close them and continue." & vbCrLf & "No: cancel the installation."
    answer = Session.Message(&H03000024, rec)
    If answer = 7 Then
      ' User declined: fail this action so Windows Installer ends Setup with its own message. This runs before
      ' InstallValidate, so no files have changed and the existing installation stays intact.
      On Error Goto 0
      Err.Raise 1602
    End If
  End If
  For Each p In procs
    If Not ((p.Name = "Keysharp.exe") And (InStr(1, p.CommandLine, " --daemon", 1) > 0)) Then
      p.Terminate
    End If
  Next
End If
'@

    $installer = New-Object -ComObject WindowsInstaller.Installer
    $db = $installer.OpenDatabase($MsiPath, 1)  # msiOpenDatabaseModeTransact
    try {
        # Run on uninstall (REMOVE="ALL") and on upgrades that REMOVE an older product (the Upgrade table's
        # ActionProperty). Skip "detect only" rows (msidbUpgradeAttributesOnlyDetect = 0x2, e.g. the
        # newer-version guard) so a blocked downgrade does not needlessly close a running newer install. On a
        # clean first install none are set, so the action is skipped and unrelated installs are left alone.
        $conditions = @('REMOVE="ALL"')
        try {
            $view = $db.OpenView('SELECT `ActionProperty`, `Attributes` FROM `Upgrade`')
            $view.Execute()
            while ($null -ne ($rec = $view.Fetch())) {
                $prop = $rec.StringData(1)
                $attr = $rec.IntegerData(2)
                if ($prop -and (($attr -band 0x2) -eq 0)) { $conditions += $prop }
                # Release each record now; a leaked view/record RCW keeps the database (and the .msi file
                # handle) open after this function returns, locking the built MSI until the host process GCs.
                [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($rec)
            }
            $view.Close()
            [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($view)
        } catch { }
        $condition = (($conditions | Select-Object -Unique) -join ' OR ')

        $action = 'KeysharpCloseInstances'
        # Type 38 = 0x26 inline VBScript (Target holds the script), immediate (no in-script bit). The
        # continue-on-error bit is deliberately NOT set: a runtime error - which the script raises only when the
        # user answers No - fails the action and ends Setup. Errors in the WMI/terminate code are swallowed by
        # "On Error Resume Next", so the only thing that aborts is the explicit No.
        $type = 38
        # Sequence 1395: after CostFinalize (paths costed) and before InstallValidate (1400), so the running
        # instances are gone before in-use detection and before RemoveFiles/InstallFiles touch the locked files.
        $sequence = 1395

        & $exec $db $installer 'DELETE FROM `CustomAction` WHERE `Action` = ?' @($action)
        & $exec $db $installer 'DELETE FROM `InstallExecuteSequence` WHERE `Action` = ?' @($action)
        & $exec $db $installer 'INSERT INTO `CustomAction` (`Action`, `Type`, `Target`) VALUES (?, ?, ?)' @($action, $type, $vbs)
        & $exec $db $installer 'INSERT INTO `InstallExecuteSequence` (`Action`, `Condition`, `Sequence`) VALUES (?, ?, ?)' @($action, $condition, $sequence)

        $db.Commit()
        Write-Host "  injected '$action' (condition: $condition; sequence $sequence, before InstallValidate)."
    }
    finally {
        [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($db) | Out-Null
        [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer) | Out-Null
        [GC]::Collect()
        [GC]::WaitForPendingFinalizers()
    }
}

function Move-RemoveExistingProductsEarly {
    # Visual Studio setup projects sequence RemoveExistingProducts AFTER InstallFiles (~6550). On a major upgrade
    # that lets InstallFiles skip any file already present at an equal version, and then the old product's removal
    # deletes those skipped files - corrupting the install (e.g. unchanged third-party DLLs such as Eto/Roslyn,
    # whose versions don't move between Keysharp releases, simply vanish). Re-sequence it to just after
    # InstallInitialize so the previous version is fully removed BEFORE InstallFiles reinstalls everything fresh.
    param([string] $MsiPath)

    if (-not (Test-Path $MsiPath)) {
        throw "Built MSI not found for RemoveExistingProducts re-sequencing: $MsiPath"
    }

    Write-Host "Re-sequencing RemoveExistingProducts in $MsiPath..."
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $db = $installer.OpenDatabase($MsiPath, 1)  # msiOpenDatabaseModeTransact
    try {
        # Sequence is not a primary key of InstallExecuteSequence (Action is), so a plain UPDATE is allowed.
        $view = $db.OpenView('UPDATE `InstallExecuteSequence` SET `Sequence` = 1525 WHERE `Action` = ''RemoveExistingProducts''')
        $view.Execute()
        $view.Close()
        [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($view)
        $db.Commit()
        Write-Host "  RemoveExistingProducts moved to 1525 (just after InstallInitialize)."
    }
    finally {
        [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($db) | Out-Null
        [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer) | Out-Null
        [GC]::Collect()
        [GC]::WaitForPendingFinalizers()
    }
}

Push-Location $root
try {
    $Version = Resolve-KeysharpVersion $Version
    Write-Host "Packaging Keysharp version $Version."

    if (-not $SkipPublish) {
        Write-Host "Publishing $solution ($Configuration, $RuntimeIdentifier)..."
        $publishProjectDirs = @(
            (Join-Path $root "dist\publish\$RuntimeIdentifier\Keysharp"),
            (Join-Path $root "dist\publish\$RuntimeIdentifier\Keyview")
        )
        Remove-Item -Path $publishProjectDirs -Recurse -Force -ErrorAction SilentlyContinue

        dotnet publish $solution -c $Configuration -r $RuntimeIdentifier `
            -p:KeysharpVersion=$Version `
            -p:Deterministic=true `
            -p:ContinuousIntegrationBuild=true `
            -p:PathMap=$pathMap
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish failed with exit code $LASTEXITCODE."
        }
    }

    Write-Host "Staging package at $packageDir..."
    Remove-Item -Path $packageDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $appDir -Force | Out-Null

    Copy-DirectoryContents (Join-Path $publishRoot "Keyview") $appDir
    Copy-DirectoryContents (Join-Path $publishRoot "Keysharp") $appDir
    Normalize-NativeAssets $appDir

    $localPathPatterns = @($root)
    if ($env:USERPROFILE) {
        $localPathPatterns += $env:USERPROFILE
    }
    if ($etoDir -and (Test-Path $etoDir)) {
        $localPathPatterns += $etoDir
    }

    Write-Host "Checking staged files for local absolute paths..."
    Assert-NoLocalPaths $appDir $localPathPatterns

    $devenv = Find-Devenv $DevenvPath
    $solutionConfig = "$Configuration|x64"
    $installerProjectOriginalContent = Get-Content -LiteralPath $installerProjectPath -Raw
    Update-InstallerProjectVersion $installerProjectPath $Version

    Write-Host "Building MSI with $devenv ($solutionConfig, project $installerProject)..."
    & $devenv $solution /Build $solutionConfig /Project $installerProject
    if ($LASTEXITCODE -ne 0) {
        throw "Installer build failed with exit code $LASTEXITCODE."
    }

    Add-CloseInstancesCustomAction (Join-Path $root "dist\Keysharp-win-x64.msi")
    Move-RemoveExistingProductsEarly (Join-Path $root "dist\Keysharp-win-x64.msi")

    Write-Host "Creating zip package at $zipPath..."
    Compress-WindowsPackage $appDir $zipPath

    Write-Host "Windows package ready at $(Join-Path $root 'dist\Keysharp-win-x64.msi')"
    Write-Host "Windows zip ready at $zipPath"
}
finally {
    if ($installerProjectOriginalContent -ne $null) {
        Set-Content -LiteralPath $installerProjectPath -Value $installerProjectOriginalContent -NoNewline
    }

    Pop-Location
}
