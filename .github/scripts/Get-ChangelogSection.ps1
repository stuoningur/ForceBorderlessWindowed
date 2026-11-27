# Prints the CHANGELOG.md section of one version (the lines below its "## [version] - date" heading, up to the next
# "## " heading), or fails if there is none or it is empty. Used by the release workflow, see CHANGELOG.md.
param(
    [Parameter(Mandatory)] [string] $Version,
    [string] $Path = (Join-Path $PSScriptRoot '..' '..' 'CHANGELOG.md')
)

$ErrorActionPreference = 'Stop'
$lines = Get-Content -LiteralPath $Path -Encoding utf8
$heading = '^## \[' + [regex]::Escape($Version) + '\](\s|$)'

$start = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match $heading) { $start = $i + 1; break }
}
if ($start -lt 0) {
    Write-Error "CHANGELOG.md has no section for $Version (expected a heading like '## [$Version] - yyyy-mm-dd')"
    exit 1
}

$end = $lines.Count
for ($i = $start; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '^## ') { $end = $i; break }
}

$section = ($lines[$start..($end - 1)] -join "`n").Trim()
if ($section.Length -eq 0) {
    Write-Error "The CHANGELOG.md section for $Version is empty"
    exit 1
}

$section
