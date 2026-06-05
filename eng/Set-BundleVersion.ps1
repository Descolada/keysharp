param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string] $Version
)

$ErrorActionPreference = 'Stop'

$plistPaths = @(
    'Keysharp/Info.plist',
    'Keyview/Info.plist',
    'Keysharp.OutputTest/Info.plist'
)

foreach ($path in $plistPaths) {
    if (-not (Test-Path $path)) {
        continue
    }

    $content = Get-Content -LiteralPath $path -Raw
    $content = [regex]::Replace(
        $content,
        '(<key>CFBundleShortVersionString</key>\s*<string>)[^<]+(</string>)',
        "`${1}$Version`${2}")
    $content = [regex]::Replace(
        $content,
        '(<key>CFBundleVersion</key>\s*<string>)[^<]+(</string>)',
        "`${1}$Version`${2}")
    Set-Content -LiteralPath $path -Value $content -NoNewline
}
