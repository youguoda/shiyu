# probe-settings-cycle.ps1 - the settings toggles' change-restart-takes-effect loop
# (research report P1 section 1, settings): four representative toggles across four domains
# (bar edge-dock on, image recording off, translate badge on, scheduled backup on) are flipped
# in launch 1, and launch 2 reads them back from a fresh process - the persisted
# settings must survive the restart exactly.
#
# Checks:
#   - flow:settings-cycle-flipped   launch 1 flipped all four toggles
#   - flow:settings-cycle-persisted launch 2 read the flipped values back
#
# ASCII only (see lib.ps1 header).

param([string]$Exe = '')

. (Join-Path $PSScriptRoot 'lib.ps1')

if ($Exe -eq '') { $Exe = Get-DefaultProbeExe }
Reset-Checks

$names = 'flow:settings-cycle-flipped', 'flow:settings-cycle-persisted'

$dataDir = Join-Path $env:TEMP 'shiyu-probe-run\settings-cycle'
& (Join-Path $PSScriptRoot 'seed.ps1') -DataDir $dataDir -AppBin (Split-Path -Parent $Exe) | Out-Null
$cycleLog = Join-Path $dataDir 'settings-cycle.log'
$verifyLog = Join-Path $dataDir 'settings-verify.log'
Remove-Item $cycleLog, $verifyLog -ErrorAction SilentlyContinue

# Launch 1: flip the four toggles.
$p1 = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd 'settings-cycle'
try {
    $deadline = (Get-Date).AddSeconds(60)
    while (-not (Test-Path $cycleLog) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
} finally {
    if ($p1) { Stop-ProbeApp $p1 }
}

Start-Sleep -Seconds 1

# Launch 2: a fresh process reads the persisted values back.
$p2 = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd 'settings-verify'
try {
    $deadline = (Get-Date).AddSeconds(60)
    while (-not (Test-Path $verifyLog) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
} finally {
    if ($p2) { Stop-ProbeApp $p2 }
}

if (-not (Test-Path $cycleLog) -or -not (Test-Path $verifyLog)) {
    foreach ($n in $names) { Add-Check $n 'FAIL' "no log from launch 1 or 2 within 60s (cycle log $(Test-Path $cycleLog), verify log $(Test-Path $verifyLog))" }
    return $script:Checks
}

function Get-SetLines([string]$Path) {
    $result = @{}
    if (Test-Path $Path) {
        foreach ($line in Get-Content $Path -Encoding UTF8) {
            if ($line -match '^set\|(\w+)=(.*)$') { $result[$Matches[1]] = $Matches[2] }
        }
    }
    return $result
}

$flipped = Get-SetLines $cycleLog
$verified = Get-SetLines $verifyLog

function FieldIs([hashtable]$Table, [string]$Key, [string]$Expected) {
    return ($Table.ContainsKey($Key)) -and ($Table[$Key] -eq $Expected)
}

function Get-Line([string]$Path, [string]$Key) {
    $hit = Select-String -Path $Path -Pattern ('set\|' + $Key + '=(.*)') -ErrorAction SilentlyContinue | Select-Object -Last 1
    if ($null -ne $hit -and $hit.Matches.Count -gt 0) { return $hit.Matches[0].Groups[1].Value }
    return '?'
}

$ok = (Get-Line $cycleLog 'BarEdgeDock') -eq 'True' -and (Get-Line $cycleLog 'RecordImages') -eq 'False' -and (Get-Line $cycleLog 'TranslateBadge') -eq 'True' -and (Get-Line $cycleLog 'AutoBackupEnabled') -eq 'True'
Add-Check 'flow:settings-cycle-flipped' $(if ($ok) { 'PASS' } else { 'FAIL' }) "launch 1 flipped: BarEdgeDock=$(Get-Line $cycleLog 'BarEdgeDock' '?'), RecordImages=$(Get-Line $cycleLog 'RecordImages' '?'), TranslateBadge=$(Get-Line $cycleLog 'TranslateBadge' '?'), AutoBackup=$(Get-Line $cycleLog 'AutoBackupEnabled' '?')"

$ok = (Get-Line $verifyLog 'BarEdgeDock') -eq 'True' -and (Get-Line $verifyLog 'RecordImages') -eq 'False' -and (Get-Line $verifyLog 'TranslateBadge') -eq 'True' -and (Get-Line $verifyLog 'AutoBackupEnabled') -eq 'True' -and ((Get-Line $verifyLog 'AutoBackupFolder') -like '*backup-folder*')
Add-Check 'flow:settings-cycle-persisted' $(if ($ok) { 'PASS' } else { 'FAIL' }) "launch 2 read the flipped values back: $(Get-Line $verifyLog 'BarEdgeDock' '?')/$(Get-Line $verifyLog 'RecordImages' '?')/$(Get-Line $verifyLog 'TranslateBadge' '?')/$(Get-Line $verifyLog 'AutoBackupEnabled' '?'), folder=$(Get-Line $verifyLog 'AutoBackupFolder' '?')"

return $script:Checks
