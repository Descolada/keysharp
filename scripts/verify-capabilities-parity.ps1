<#
Verifies parity between `docs/capabilities.json` and the AutoHotkey docs index.

What it does:
- Loads capabilities from `docs/capabilities.json`.
- Loads docs index entries from `<DocsRoot>/static/source/data_index.js`.
- Normalizes names/paths, filters alias/concept tokens, and compares canonical keys.
- Writes machine-readable and markdown reports with missing/extra/mismatch stats.

Default paths:
- `DocsRoot` is relative (`..\..\AutoHotkeyDocs\docs`).

Outputs:
- `docs/capabilities-parity-report.json`
- `docs/capabilities-parity-report.md`
#>
param(
	[string]$CapabilitiesPath = "docs/capabilities.json",
	[string]$DocsRoot = "..\..\AutoHotkeyDocs\docs",
	[string]$OutJson = "docs/capabilities-parity-report.json",
	[string]$OutMd = "docs/capabilities-parity-report.md"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Normalize-Feature([string]$name) {
	if ([string]::IsNullOrWhiteSpace($name)) { return $null }
	$n = $name.Trim()

	switch ($n) {
		"[ ... ] / Array" { return "[]" }
		"{ ... } / Object" { return "{}" }
		"%...% / Dereference" { return "%expr%" }
		"~!=" { return "!~=" }
	}

	# Normalize simple formatting differences.
	$n = $n -replace "\s+", " "
	$n = $n -replace "\(\)$", ""
	$n = $n -replace "\s*/\s*", "/"

	return $n.ToLowerInvariant()
}

function Is-DocsEntryInScope([string]$path) {
	return $path -match '^(lib/|Variables\.htm|Objects\.htm|Structs\.htm)'
}

function Normalize-DocsPath([string]$path) {
	if ([string]::IsNullOrWhiteSpace($path)) { return $path }
	$p = $path.Trim()
	# Strip anchors/query so each path is either "lib/<name>.htm" or "<name>.htm".
	$p = ($p -split '#', 2)[0]
	$p = ($p -split '\?', 2)[0]
	return $p
}

function Is-DescriptiveAliasName([string]$name) {
	if ([string]::IsNullOrWhiteSpace($name)) { return $false }
	$n = $name.Trim()
	# Docs index contains many prose aliases (e.g. "close a window") that map to
	# canonical API pages. They should not be treated as capabilities.
	return $n -match '^[a-z][a-z0-9]*( [a-z0-9]+)+$'
}

function Is-ParentheticalAliasName([string]$name) {
	if ([string]::IsNullOrWhiteSpace($name)) { return $false }
	$n = $name.Trim()
	if ($n -notmatch '\([^)]*\)') { return $false }
	# Keep forms we normalize into canonical members/operators.
	if ($n -match '^(__[A-Za-z]+)\s+method\s+\([^)]+\)$') { return $false }
	if ($n -match '^(__[A-Za-z]+)\s+property\s+\([^)]+\)$') { return $false }
	if ($n -match '^[-&]\s+\([^)]+\)$') { return $false }
	# Remaining parenthetical entries are usually index aliases, concept labels,
	# or documentation grouping terms, not concrete capabilities.
	return $true
}

function Is-NonCapabilityDocsTerm([string]$name, [string]$path) {
	if ([string]::IsNullOrWhiteSpace($name)) { return $false }
	$n = $name.Trim()

	# Parameter/value tokens and non-callable constants used as examples.
	if ($n -in @("wParam", "lParam", "READONLY", "REG_BINARY", "REG_DWORD", "REG_EXPAND_SZ", "REG_MULTI_SZ", "REG_SZ", "YYYYMMDDHH24MISS", "WM_COPYDATA")) {
		return $true
	}
	# Command/value aliases, not standalone capabilities.
	if ($n -in @("logoff", "reboot")) { return $true }
	# Concept/index labels which point to documentation sections.
	if ($n -in @("COM", "coordinates", "debugger", "dereference", "double-deref", "expressions", "focus", "hook", "inline", "Locale", "Primitive", "variables", "appending")) {
		return $true
	}
	# Is.htm lowercase subtype names are section anchors; canonical capability
	# entries are function-style (IsAlpha, IsDigit, etc.).
	if ($path -eq "lib/Is.htm" -and $n -cmatch '^[a-z]+$') {
		return $true
	}

	return $false
}

function Expand-DocsFeatureNames([string]$rawName, [string]$path) {
	# Canonicalize directive-page aliases (lib/_Name.htm -> #Name).
	if ($path -match '^lib/_([A-Za-z0-9]+)\.htm') {
		return @("#$($matches[1])")
	}
	# Canonicalize known send-mode alias.
	if ($path -eq "lib/Send.htm#SendText" -or $rawName -match '^(?i:text-mode\s+send)$') {
		return @("SendText")
	}

	# Skip non-capability index helpers/noise.
	if ($rawName -match 'Ahk2Exe|comments|quoted strings|escape sequences') { return @() }
	# Comma/colon labels are index aliases or descriptive labels, not canonical
	# capability names (e.g. "file, writing/appending", "RegEx: ...").
	if ($rawName -match '[,:]') { return @() }
	if (Is-NonCapabilityDocsTerm $rawName $path) { return @() }
	if (Is-DescriptiveAliasName $rawName) { return @() }
	if (Is-ParentheticalAliasName $rawName) { return @() }
	if ($rawName -in @(":", "?", "` (escape sequences)", "[]", "{}")) {
		switch ($rawName) {
			"[]" { return @("[ ... ] / Array") }
			"{}" { return @("{ ... } / Object") }
			default { return @() }
		}
	}

	# Split combined entries like WinWaitActive / WinWaitNotActive.
	$parts = $rawName -split '\s*/\s*'
	$out = New-Object System.Collections.Generic.List[string]

	foreach ($partRaw in $parts) {
		$part = $partRaw.Trim()
		if ([string]::IsNullOrWhiteSpace($part)) { continue }

		# Metadata suffixes.
		if ($part -match '^(__[A-Za-z]+)\s+method\s+\(([^)]+)\)$') {
			$out.Add("$($matches[2]).$($matches[1])()")
			continue
		}
		if ($part -match '^(__[A-Za-z]+)\s+property\s+\(([^)]+)\)$') {
			$out.Add("$($matches[2]).$($matches[1])")
			continue
		}
		if ($part -match '^(__[A-Za-z]+)\s+method$') {
			$out.Add("$($matches[1])()")
			continue
		}
		if ($part -match '^(__[A-Za-z]+)\s+property$') {
			$out.Add($matches[1])
			continue
		}
		if ($part -match '^(__[A-Za-z]+)\s+meta-function$') {
			$out.Add($matches[1])
			continue
		}
		if ($part -match '^(.+?)\s+\(([^)]+)\)$') {
			# Operator annotations like "- (sign)", "& (bitwise-and)".
			$core = $matches[1].Trim()
			if ($core -in @("-", "&")) {
				$out.Add($core)
				continue
			}
		}
		# Skip prose aliases/synonyms such as "file, creating",
		# "regular expressions: RegExMatch", "Class object", etc.
		if ($part -match '[,:]') { continue }
		if ($part -match '\s') { continue }

		# Default pass-through.
		$out.Add($part)
	}

	return @($out)
}

function Get-MapBucket($map, [string]$key) {
	if (-not $map.ContainsKey($key)) { return @() }
	$bucket = $map[$key]
	return @($bucket | ForEach-Object { $_ })
}

if (-not (Test-Path $CapabilitiesPath)) {
	throw "Capabilities file not found: $CapabilitiesPath"
}

$indexPath = Join-Path $DocsRoot "static/source/data_index.js"
if (-not (Test-Path $indexPath)) {
	throw "AHK docs index not found: $indexPath"
}

$capRoot = Get-Content $CapabilitiesPath -Raw | ConvertFrom-Json
$capRows = @($capRoot.rows)

# Map capability normalized keys.
$capByNorm = @{}
foreach ($row in $capRows) {
	$feature = [string]$row.feature
	$norm = Normalize-Feature $feature
	if (-not $norm) { continue }
	if (-not $capByNorm.ContainsKey($norm)) { $capByNorm[$norm] = New-Object System.Collections.Generic.List[object] }
	$capByNorm[$norm].Add([pscustomobject]@{
		feature = $feature
		category = [string]$row.category
	})
}

# Parse docs index.
$docByNorm = @{}
$docEntries = Get-Content $indexPath
foreach ($line in $docEntries) {
	if ($line -notmatch '^\s*\["([^"]+)"\s*,\s*"([^"]+)"') { continue }
	$name = $matches[1]
	$path = Normalize-DocsPath $matches[2]
	if (-not (Is-DocsEntryInScope $path)) { continue }

	$expanded = Expand-DocsFeatureNames $name $path
	foreach ($n in $expanded) {
		$norm = Normalize-Feature $n
		if (-not $norm) { continue }
		if (-not $docByNorm.ContainsKey($norm)) { $docByNorm[$norm] = New-Object System.Collections.Generic.List[object] }
		$docByNorm[$norm].Add([pscustomobject]@{
			name = $n
			path = $path
		})
	}
}

# Differences.
$docNormKeys = @($docByNorm.Keys)
$capNormKeys = @($capByNorm.Keys)

$missingNorm = @($docNormKeys | Where-Object { -not $capByNorm.ContainsKey($_) } | Sort-Object)
$extraNorm = @($capNormKeys | Where-Object { -not $docByNorm.ContainsKey($_) } | Sort-Object)

# Ignore Keysharp-specific extras by category default.
$extra = New-Object System.Collections.Generic.List[object]
foreach ($n in $extraNorm) {
	$key = [string]$n
	$rows = @(Get-MapBucket $capByNorm $key)
	$nonKeysharpRows = @($rows | Where-Object { $_.category -ne "Keysharp Module (Ks)" })
	$allKeysharp = ($rows.Count -gt 0 -and $nonKeysharpRows.Count -eq 0)
	if ($allKeysharp) { continue }
	$extra.Add([pscustomobject]@{
		norm = $key
		capability_features = @($rows | Select-Object -ExpandProperty feature | Sort-Object -Unique)
		categories = @($rows | Select-Object -ExpandProperty category | Sort-Object -Unique)
	}) | Out-Null
}

$missing = New-Object System.Collections.Generic.List[object]
$skippedMissingMultiPath = 0
foreach ($n in $missingNorm) {
	$key = [string]$n
	$docs = @(Get-MapBucket $docByNorm $key)
	$docsPaths = @($docs | Select-Object -ExpandProperty path | Sort-Object -Unique)
	# Terms mapped to multiple docs pages are usually index aliases/group labels
	# (e.g. "directory"), not concrete capabilities.
	if ($docsPaths.Count -gt 1) {
		$skippedMissingMultiPath++
		continue
	}
	$missing.Add([pscustomobject]@{
		norm = $key
		docs_names = @($docs | Select-Object -ExpandProperty name | Sort-Object -Unique)
		docs_paths = $docsPaths
	}) | Out-Null
}

# Name/style mismatch: same norm exists, but no exact feature string overlap.
$mismatch = New-Object System.Collections.Generic.List[object]
foreach ($n in ($docNormKeys | Where-Object { $capByNorm.ContainsKey($_) } | Sort-Object)) {
	$key = [string]$n
	$docNames = @((Get-MapBucket $docByNorm $key) | Select-Object -ExpandProperty name | Sort-Object -Unique)
	$capNames = @((Get-MapBucket $capByNorm $key) | Select-Object -ExpandProperty feature | Sort-Object -Unique)
	$docNamesNorm = @($docNames | ForEach-Object { Normalize-Feature ([string]$_) } | Where-Object { $_ } | Sort-Object -Unique)
	$capNamesNorm = @($capNames | ForEach-Object { Normalize-Feature ([string]$_) } | Where-Object { $_ } | Sort-Object -Unique)
	$intersect = @($docNamesNorm | Where-Object { $capNamesNorm -contains $_ })
	if ($intersect.Count -eq 0) {
		$mismatch.Add([pscustomobject]@{
			norm = $key
			docs_names = $docNames
			capability_features = $capNames
		}) | Out-Null
	}
}

$report = @{
	generated_at_utc = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
	capabilities_file = $CapabilitiesPath
	docs_index_file = $indexPath
	stats = @{
		capability_rows = $capRows.Count
		capability_norm_keys = $capNormKeys.Count
		docs_norm_keys = $docNormKeys.Count
		missing_in_capabilities = $missing.Count
		missing_skipped_multi_path_alias = $skippedMissingMultiPath
		extra_in_capabilities = $extra.Count
		name_mismatch = $mismatch.Count
	}
	missing_in_capabilities = $missing.ToArray()
	extra_in_capabilities = $extra.ToArray()
	name_mismatch = $mismatch.ToArray()
}

$outDirJson = Split-Path -Parent $OutJson
if ($outDirJson -and -not (Test-Path $outDirJson)) { New-Item -ItemType Directory -Path $outDirJson | Out-Null }
$report | ConvertTo-Json -Depth 8 | Set-Content $OutJson

$md = New-Object System.Collections.Generic.List[string]
$md.Add("# Capabilities Parity Report")
$md.Add("")
$md.Add("- Generated: $($report.generated_at_utc)")
$md.Add("- Capabilities rows: $($report.stats.capability_rows)")
$md.Add("- Capabilities normalized keys: $($report.stats.capability_norm_keys)")
$md.Add("- Docs normalized keys: $($report.stats.docs_norm_keys)")
$md.Add("- Missing in capabilities: $($report.stats.missing_in_capabilities)")
$md.Add("- Missing skipped (multi-path aliases): $($report.stats.missing_skipped_multi_path_alias)")
$md.Add("- Extra in capabilities: $($report.stats.extra_in_capabilities)")
$md.Add("- Name mismatches: $($report.stats.name_mismatch)")
$md.Add("")

$md.Add("## Missing In Capabilities (Top 200)")
foreach ($item in @($missing | Select-Object -First 200)) {
	$docNames = ($item.docs_names -join ' | ')
	$docPaths = ($item.docs_paths -join ', ')
	$md.Add(('- `{0}` -> {1}' -f $docNames, $docPaths))
}
$md.Add("")

$md.Add("## Extra In Capabilities (Top 200)")
foreach ($item in @($extra | Select-Object -First 200)) {
	$capNames = ($item.capability_features -join ' | ')
	$cats = ($item.categories -join ', ')
	$md.Add(('- `{0}` (categories: {1})' -f $capNames, $cats))
}
$md.Add("")

$md.Add("## Name Mismatches (Top 200)")
foreach ($item in @($mismatch | Select-Object -First 200)) {
	$docNames = ($item.docs_names -join ' | ')
	$capNames = ($item.capability_features -join ' | ')
	$md.Add(('- docs: `{0}` vs capabilities: `{1}`' -f $docNames, $capNames))
}

$outDirMd = Split-Path -Parent $OutMd
if ($outDirMd -and -not (Test-Path $outDirMd)) { New-Item -ItemType Directory -Path $outDirMd | Out-Null }
Set-Content $OutMd ($md -join "`n")

Write-Host "Generated parity reports:"
Write-Host "  $OutJson"
Write-Host "  $OutMd"
Write-Host ""
Write-Host ("Summary: missing={0}, extra={1}, mismatch={2}" -f $report.stats.missing_in_capabilities, $report.stats.extra_in_capabilities, $report.stats.name_mismatch)
