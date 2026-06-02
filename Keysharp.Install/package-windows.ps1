param(
    [string] $Configuration = "Release",
    [string] $RuntimeIdentifier = "win-x64",
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
$etoDir = Join-Path (Split-Path -Parent $root) "Eto"
$pathMap = "$root=/_/keysharp"
if (Test-Path $etoDir) {
    $etoDir = (Resolve-Path $etoDir).Path
    $pathMap = "$pathMap%2c$etoDir=/_/Eto"
}

if ($RuntimeIdentifier -ne "win-x64") {
    throw "The Visual Studio installer project is configured for win-x64 publish output. Use -RuntimeIdentifier win-x64."
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

Push-Location $root
try {
    if (-not $SkipPublish) {
        Write-Host "Publishing $solution ($Configuration, $RuntimeIdentifier)..."
        $publishProjectDirs = @(
            (Join-Path $root "dist\publish\$RuntimeIdentifier\Keysharp"),
            (Join-Path $root "dist\publish\$RuntimeIdentifier\Keyview")
        )
        Remove-Item -Path $publishProjectDirs -Recurse -Force -ErrorAction SilentlyContinue

        dotnet publish $solution -c $Configuration -r $RuntimeIdentifier `
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

    Write-Host "Building MSI with $devenv ($solutionConfig, project $installerProject)..."
    & $devenv $solution /Build $solutionConfig /Project $installerProject
    if ($LASTEXITCODE -ne 0) {
        throw "Installer build failed with exit code $LASTEXITCODE."
    }

    Write-Host "Windows package ready at $(Join-Path $root 'dist\Keysharp-win-x64.msi')"
}
finally {
    Pop-Location
}
