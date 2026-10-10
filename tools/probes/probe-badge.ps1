# probe-badge.ps1 - the translate badge is a single glyph in a circle (user request
# 2026-10-10: drop the words beside it, keep only the one character).
#
# The DEBUG command "badge" shows the badge the way a copy or a drag-select does,
# logs its size and the text runs inside it, and renders badge.png into the data dir.
#
# Checks:
#   - flow:badge-single-glyph   exactly one visible text run: the glyph alone
#   - flow:badge-circle         the pill is square (a circle with the pill radius)
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

$ok = $fields['width'] -eq $fields['height'] -and [int]$fields['width'] -gt 0
Add-Check 'flow:badge-circle' $(if ($ok) { 'PASS' } else { 'FAIL' }) `
    ("pill {0}x{1} DIP" -f $fields['width'], $fields['height'])

return $script:Checks
