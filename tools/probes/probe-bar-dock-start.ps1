# probe-bar-dock-start.ps1 - with the bar pinned and edge-docked, a fresh start puts the strip
# back at the top of the screen on its own (user request 2026-10-21: after a restart the strip
# was gone until the first summon) - but only when the bar was resting at the top edge when
# Shiyu last closed (user request 2026-10-22: edge-dock acts only on a bar at the top edge).
#
# The settings file gets BarPinned, BarEdgeDock and the remembered bar position before the probe
# instance starts with the DEBUG command "bar-dock-start", which summons nothing: it waits for
# the bar to appear, reads it, then simulates the pointer entering the strip.
#
# Checks:
#   - flow:bar-dock-start-strip     remembered at the top: the bar is up as a strip of at most
#                                   4 DIP, without focus
#   - flow:bar-dock-start-hover     the pointer entering the strip slides it out (not a summon)
#   - flow:bar-dock-start-away      remembered mid-screen: no strip comes up on start
#
# ASCII only (see lib.ps1 header).

param([string]$Exe = '')

. (Join-Path $PSScriptRoot 'lib.ps1')

if ($Exe -eq '') { $Exe = Get-DefaultProbeExe }
Reset-Checks

function Invoke-DockStart([string]$Name, [double]$BarTop) {
    $dataDir = Join-Path $env:TEMP "shiyu-probe-run\$Name"
    & (Join-Path $PSScriptRoot 'seed.ps1') -DataDir $dataDir -AppBin (Split-Path -Parent $Exe) | Out-Null

    $settingsFile = Join-Path $dataDir 'settings.json'
    $settings = Get-Content $settingsFile -Raw -Encoding UTF8 | ConvertFrom-Json
    $settings | Add-Member -NotePropertyName BarPinned -NotePropertyValue $true -Force
    $settings | Add-Member -NotePropertyName BarEdgeDock -NotePropertyValue $true -Force
    $settings | Add-Member -NotePropertyName BarLeft -NotePropertyValue 300 -Force
    $settings | Add-Member -NotePropertyName BarTop -NotePropertyValue $BarTop -Force
    [System.IO.File]::WriteAllText($settingsFile, ($settings | ConvertTo-Json -Depth 10), (New-Object System.Text.UTF8Encoding($false)))

    $log = Join-Path $dataDir 'bar-dock-start.log'
    Remove-Item $log -ErrorAction SilentlyContinue

    $p = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd 'bar-dock-start'
    try {
        $deadline = (Get-Date).AddSeconds(60)
        while (-not (Test-Path $log) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
    } finally {
        if ($p) { Stop-ProbeApp $p }
    }

    if (-not (Test-Path $log)) { return $null }

    $lines = @{}
    foreach ($line in Get-Content $log -Encoding UTF8) {
        if ($line -match '^(\w+)\|(.*)$') {
            $fields = @{}
            foreach ($pair in $Matches[2].Split('|')) {
                $kv = $pair.Split('=', 2)
                if ($kv.Count -eq 2) { $fields[$kv[0]] = $kv[1] }
            }
            $lines[$Matches[1]] = $fields
        }
    }
    return $lines
}

$lines = Invoke-DockStart 'bar-dock-start' 0
if ($null -eq $lines) {
    Add-Check 'flow:bar-dock-start-strip' 'FAIL' 'the command wrote no log within 60s'
} elseif ($lines['error']) {
    Add-Check 'flow:bar-dock-start-strip' 'FAIL' ("the command threw {0}" -f $lines['error']['type'])
} else {
    $start = $lines['start']
    $ok = $start -and $start['visible'] -eq 'True' -and ([double]$start['strip'] -le 4) -and $start['active'] -eq 'False'
    $detail = if ($start -and $start['visible'] -eq 'True') { "strip after start: {0} DIP, took focus {1}" -f $start['strip'], $start['active'] } else { 'the bar did not come up on start' }
    Add-Check 'flow:bar-dock-start-strip' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

    $hover = $lines['starthover']
    $ok = $hover -and $hover['expanded'] -eq 'True' -and $hover['top'] -eq '0'
    $detail = if ($hover) { "pointer entering the strip: expanded {0}, top {1} (work-area top)" -f $hover['expanded'], $hover['top'] } else { 'no starthover line' }
    Add-Check 'flow:bar-dock-start-hover' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail
}

$away = Invoke-DockStart 'bar-dock-start-away' 300
if ($null -eq $away) {
    Add-Check 'flow:bar-dock-start-away' 'FAIL' 'the command wrote no log within 60s'
} else {
    $start = $away['start']
    $ok = $start -and $start['visible'] -eq 'False'
    $detail = if ($start) { "remembered 300 DIP down: bar visible on start {0}" -f $start['visible'] } else { 'no start line' }
    Add-Check 'flow:bar-dock-start-away' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail
}

return $script:Checks
