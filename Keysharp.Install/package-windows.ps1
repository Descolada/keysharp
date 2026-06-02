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

    $publishRoot = Join-Path $root "dist\publish\$RuntimeIdentifier"
    $localPathPatterns = @($root)
    if ($env:USERPROFILE) {
        $localPathPatterns += $env:USERPROFILE
    }
    if ($etoDir -and (Test-Path $etoDir)) {
        $localPathPatterns += $etoDir
    }

    Write-Host "Checking published files for local absolute paths..."
    Assert-NoLocalPaths $publishRoot $localPathPatterns

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
