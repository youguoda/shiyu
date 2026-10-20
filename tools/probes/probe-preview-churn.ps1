# probe-preview-churn.ps1 - show/takedown churn stress on the preview panel
# (30 cycles over a text, an image and a files card) with memory and handle
# accounting around the whole run.
#
# The DEBUG command "preview-churn" resolves three cards, forces a full GC,
# cycles show -> settle -> takedown -> fade-wait 30 times, forces a GC again
# and reports working set / private-bytes-via-GC / handle deltas plus the
# panel's original-decode cache size.
#
# Checks:
#   flow:churn-completes      every cycle showed the panel (done + no error)
#   flow:churn-originals-cap  the decode cache holds its 4-entry cap
#   perf:churn-workingset     working set grew <= 120 MB (green expected)
#   perf:churn-handles        handle count grew <= 500 (green expected)
#
# ASCII only (see lib.ps1 header).

param([string]$Exe = '')

. (Join-Path $PSScriptRoot 'lib.ps1')

if ($Exe -eq '') { $Exe = Get-DefaultProbeExe }
Reset-Checks

$dataDir = Join-Path $env:TEMP 'shiyu-probe-run\preview-churn'
& (Join-Path $PSScriptRoot 'seed.ps1') -DataDir $dataDir -AppBin (Split-Path -Parent $Exe) | Out-Null
$log = Join-Path $dataDir 'preview-churn.log'
Remove-Item $log -ErrorAction SilentlyContinue

$p = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd 'preview-churn' -ExtraEnv @{ SHIYU_FAKE_BACKEND = '1' }
try {
    $deadline = (Get-Date).AddSeconds(120)
    while (-not (Test-Path $log) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
    while ((Get-Date) -lt $deadline -and (Test-Path $log) -and (@(Select-String -Path $log -Pattern '^done').Count -eq 0)) {
        Start-Sleep -Milliseconds 500
    }
} finally {
    if ($p) { Stop-ProbeApp $p }
}

if (-not (Test-Path $log)) {
    Add-Check 'flow:churn-completes' 'FAIL' 'the command wrote no log within 120s'
    Add-Check 'flow:churn-originals-cap' 'FAIL' 'the command wrote no log within 120s'
    Add-Check 'perf:churn-workingset' 'FAIL' 'the command wrote no log within 120s'
    Add-Check 'perf:churn-handles' 'FAIL' 'the command wrote no log within 120s'
    return $script:Checks
}

$churn = $null
$done = $false
$errored = $false
foreach ($line in Get-Content $log -Encoding UTF8) {
    if ($line -eq 'done') { $done = $true }
    if ($line -match '^error\|') { $errored = $true }
    if ($line -match '^churn\|(.*)$') {
        $fields = @{}
        foreach ($pair in $Matches[1].Split('|')) {
            $kv = $pair.Split('=', 2)
            if ($kv.Count -eq 2) { $fields[$kv[0]] = $kv[1] }
        }
        $churn = $fields
    }
}

function Field($Step, [string]$Key, [string]$Fallback) {
    if ($null -ne $Step -and $Step.ContainsKey($Key)) { return [string]$Step[$Key] }
    return $Fallback
}

function ConvertTo-InvariantDouble([string]$Text) {
    $value = 0.0
    if ([double]::TryParse($Text, [System.Globalization.NumberStyles]::Float,
        [System.Globalization.CultureInfo]::InvariantCulture, [ref]$value)) { return $value }
    return -1.0
}

if ($null -eq $churn) {
    Add-Check 'flow:churn-completes' 'FAIL' "no churn line (done=$done errorLine=$errored)"
    Add-Check 'flow:churn-originals-cap' 'FAIL' 'no churn line'
    Add-Check 'perf:churn-workingset' 'FAIL' 'no churn line'
    Add-Check 'perf:churn-handles' 'FAIL' 'no churn line'
    return $script:Checks
}

$ok = $done -and (-not $errored)
Add-Check 'flow:churn-completes' $(if ($ok) { 'PASS' } else { 'FAIL' }) "30 show/takedown cycles: done=$done errorLine=$errored"

$originals = ConvertTo-InvariantDouble (Field $churn 'originals' '-1')
$ok = ($originals -ge 0) -and ($originals -le 4)
Add-Check 'flow:churn-originals-cap' $(if ($ok) { 'PASS' } else { 'FAIL' }) "original-decode cache holds: originals=$originals (cap 4)"

$wsDelta = ConvertTo-InvariantDouble (Field $churn 'wsDeltaMB' '-1')
$ok = ($wsDelta -ge 0) -and ($wsDelta -le 120)
Add-Check 'perf:churn-workingset' $(if ($ok) { 'PASS' } else { 'FAIL' }) "working set grew ${wsDelta}MB ($(Field $churn 'wsBeforeMB' '?') -> $(Field $churn 'wsAfterMB' '?'))"

$handles = ConvertTo-InvariantDouble (Field $churn 'handlesDelta' '-1')
$ok = ($handles -ge 0) -and ($handles -le 500)
Add-Check 'perf:churn-handles' $(if ($ok) { 'PASS' } else { 'FAIL' }) "handles grew $handles ($(Field $churn 'handlesBefore' '?') -> +$handles)"

return $script:Checks
