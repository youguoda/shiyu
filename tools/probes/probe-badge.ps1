# probe-badge.ps1 - the translate badge is a single glyph on a rounded square (user
# requests 2026-10-10: drop the words beside it, keep only the one character;
# 2026-10-13: the circle becomes a rounded square).
#
# The DEBUG command "badge" shows the badge the way a copy or a drag-select does,
# logs its size, corner radius and the text runs inside it, and renders badge.png
# into the data dir.
#
# Checks:
#   - flow:badge-single-glyph     exactly one visible text run: the glyph alone
#   - flow:badge-rounded-square   square, with the overlay radius - rounded corners,
#                                 well short of the half-width that makes a circle
#
# ASCII only (see lib.ps1 header).

param([string]$Exe = '')

. (Join-Path $PSScriptRoot 'lib.ps1')

if ($Exe -eq '') { $Exe = Get-DefaultProbeExe }
Reset-Checks

$dataDir = Join-Path $env:TEMP 'shiyu-probe-run\badge'
& (Join-Path $PSScriptRoot 'seed.ps1') -DataDir $dataDir -AppBin (Split-Path -Parent $Exe) | Out-Null
$log = Join-Path $dataDir 'badge.log'
Remove-Item $log -ErrorAction SilentlyContinue

$p = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd 'badge'
try {
    $deadline = (Get-Date).AddSeconds(30)
    while (-not (Test-Path $log) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300 }
} finally {
    if ($p) { Stop-ProbeApp $p }
}

$fields = @{}
if (Test-Path $log) {
    foreach ($line in Get-Content $log -Encoding UTF8) {
        if ($line -match '^badge\|(.*)$') {
            foreach ($pair in $Matches[1].Split('|')) {
                $kv = $pair.Split('=', 2)
                if ($kv.Count -eq 2) { $fields[$kv[0]] = $kv[1] }
            }
        }
    }
}

if ($fields.Count -eq 0) {
    Add-Check 'flow:badge-single-glyph' 'FAIL' 'the badge never showed (no log line)'
    return $script:Checks
}

$ok = $fields['texts'] -eq '1' -and $fields['glyph'] -eq 'True'
Add-Check 'flow:badge-single-glyph' $(if ($ok) { 'PASS' } else { 'FAIL' }) `
    ("{0} text run(s), glyph alone {1}" -f $fields['texts'], $fields['glyph'])

# -as converts culture-invariantly and gives $null (a failed check) for a missing field.
$width = $fields['width'] -as [double]
$radius = $fields['radius'] -as [double]
$ok = $fields['width'] -eq $fields['height'] -and $width -gt 0 `
    -and $fields['radius'] -eq $fields['radiusExpect'] -and $radius -gt 0 -and $radius -lt ($width / 2)
Add-Check 'flow:badge-rounded-square' $(if ($ok) { 'PASS' } else { 'FAIL' }) `
    ("tile {0}x{1} DIP, corner radius {2} (expected {3})" -f $fields['width'], $fields['height'], $fields['radius'], $fields['radiusExpect'])

return $script:Checks
