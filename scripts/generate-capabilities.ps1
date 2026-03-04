param(
	[string]$Source = "docs/capabilities.json",
	[string]$DocsOut = "docs/capabilities.md",
	[string]$ReadmePath = "README.md"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function StatusIcon([string]$status) {
    $green = [char]::ConvertFromUtf32(0x1F7E2)
    $yellow = [char]::ConvertFromUtf32(0x1F7E1)
    $orange = [char]::ConvertFromUtf32(0x1F7E0)
    $red = [char]::ConvertFromUtf32(0x1F534)
    $white = [char]::ConvertFromUtf32(0x26AA)
    switch ($status) {
        "full" { return "$green Full" }
        "partial" { return "$yellow Partial" }
        "planned" { return "$orange Planned" }
        "unsupported" { return "$red Unsupported" }
        default { return "$white Unknown" }
    }
}

function EscapeMdCell([string]$value) {
	if ($null -eq $value) { return "" }
	$v = $value -replace "`r?`n", "<br>"
	# Escape pipe so operators like "|" and "||" do not break table columns.
	$v = $v -replace "\|", "\\|"
	return $v
}

function BuildTableLines($inputRows) {
	$header = @(
		"| Capability | Windows | Linux (X11) | Linux (Wayland) | macOS | Notes |",
		"|---|---|---|---|---|---|"
	)

	$out = New-Object System.Collections.Generic.List[string]
	foreach ($line in $header) { $out.Add($line) }
	foreach ($row in $inputRows) {
		$feature = EscapeMdCell([string]$row.feature)
		$notes = EscapeMdCell([string]$row.notes)
		$out.Add("| $feature | $(StatusIcon $row.windows) | $(StatusIcon $row.linux_x11) | $(StatusIcon $row.linux_wayland) | $(StatusIcon $row.macos) | $notes |")
	}
	return $out
}

if (-not (Test-Path $Source)) {
	throw "Source file not found: $Source"
}

$root = Get-Content $Source -Raw | ConvertFrom-Json

$allRows = $root.rows | Sort-Object @{ Expression = { $_.feature.ToString().ToLowerInvariant() } }, @{ Expression = { $_.feature.ToString() } }
$tableAll = BuildTableLines $allRows

$legendLines = @(
	"Status legend:",
	"- `Full`: $($root.legend.full)",
	"- `Partial`: $($root.legend.partial)",
	"- `Planned`: $($root.legend.planned)",
	"- `Unsupported`: $($root.legend.unsupported)",
	"- `Unknown`: $($root.legend.unknown)"
)

$docsLines = New-Object System.Collections.Generic.List[string]
$docsLines.Add("# Capability Matrix")
$docsLines.Add("")
$docsLines.Add("Generated from `docs/capabilities.json` via `scripts/generate-capabilities.ps1`.")
$docsLines.Add("")
foreach ($line in $legendLines) { $docsLines.Add($line) }
$docsLines.Add("")
foreach ($line in $tableAll) { $docsLines.Add($line) }

$docsSection = [string]::Join("`n", $docsLines)

$docsDir = Split-Path -Parent $DocsOut
if ($docsDir -and -not (Test-Path $docsDir)) { New-Item -ItemType Directory -Path $docsDir | Out-Null }

[System.IO.File]::WriteAllText($DocsOut, $docsSection, [System.Text.UTF8Encoding]::new($false))

# Build concise overview matrix for README injection.
$overviewFeatures = @(
	"Parser and runtime execution",
	"Directives and preprocessing",
	"File and directory operations",
	"Keyboard/Mouse send (synthetic input)",
	"Global keyboard hooks",
	"Global mouse hooks",
	"Hotkeys/Hotstrings",
	"Script-owned window management",
	"Foreign window management (non-Keysharp apps)",
	"Tray icon and menu",
	"Screen capture and pixel/image functions",
	"Clipboard",
	"Sound APIs",
	"Registry APIs",
	"COM APIs"
)

$overviewRows = foreach ($name in $overviewFeatures) {
	$root.rows | Where-Object { $_.feature -eq $name } | Select-Object -First 1
}

$missingOverview = @($overviewFeatures | Where-Object {
	$target = $_
	-Not ($overviewRows | Where-Object { $_ -and $_.feature -eq $target })
})
if ($missingOverview.Count -gt 0) {
	Write-Warning "Some overview features were not found in capabilities.json:"
	$missingOverview | ForEach-Object { Write-Warning "  $_" }
}

$overviewLines = New-Object System.Collections.Generic.List[string]
foreach ($line in $legendLines) { $overviewLines.Add($line) }
$overviewLines.Add("")
foreach ($line in (BuildTableLines ($overviewRows | Where-Object { $_ }))) { $overviewLines.Add($line) }
$overviewSection = [string]::Join("`n", $overviewLines)

$startMarker = "<!-- CAPABILITIES_OVERVIEW:START -->"
$endMarker = "<!-- CAPABILITIES_OVERVIEW:END -->"
if (Test-Path $ReadmePath) {
	$readmeText = Get-Content $ReadmePath -Raw
	$readmeNewline = if ($readmeText -match "`r`n") { "`r`n" } else { "`n" }
	$pattern = [regex]::Escape($startMarker) + ".*?" + [regex]::Escape($endMarker)
	$replacement = $startMarker + $readmeNewline + ($overviewSection -replace "`n", $readmeNewline) + $readmeNewline + $endMarker
	if ([regex]::IsMatch($readmeText, $pattern, [System.Text.RegularExpressions.RegexOptions]::Singleline)) {
		$newReadme = [regex]::Replace($readmeText, $pattern, $replacement, [System.Text.RegularExpressions.RegexOptions]::Singleline)
		[System.IO.File]::WriteAllText($ReadmePath, $newReadme, [System.Text.UTF8Encoding]::new($false))
	}
	else {
		Write-Warning "README markers not found; skipped README injection."
	}
}
else {
	Write-Warning "README not found at $ReadmePath; skipped README injection."
}

Write-Host "Generated:"
Write-Host "  $DocsOut"
Write-Host "Injected concise overview into:"
Write-Host "  $ReadmePath"
