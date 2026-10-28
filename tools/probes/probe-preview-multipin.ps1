# probe-preview-multipin.ps1 - pin previews with Ctrl+T and keep several pinned at once (user
# request 2026-10-28).
#
# The DEBUG command "preview-multipin" selects a card and calls what Ctrl+T calls (modifier state
# cannot be synthesized), selects another and does it again, opens a third through the preview
# policy's Space path, unpins the oldest, pins ten more to hit the cap, deletes a pinned card and
# finally presses Ctrl+T on the pinned selected card again. No real input is used.
#
# Checks:
#   - flow:preview-pinkey-opens        Ctrl+T with no preview open pins the selected card
#   - flow:preview-multipin-two        a second Ctrl+T keeps the first pinned beside the new one
#   - flow:preview-multipin-hover      a preview for a third card opens in a new, unpinned window
#   - flow:preview-multipin-unpin      unpinning a set-aside preview closes its window
#   - flow:preview-multipin-cap        at most 8 pinned previews stay open
#   - flow:preview-multipin-delete     deleting a pinned card takes its preview down
#   - flow:preview-pinkey-toggle       Ctrl+T on the pinned selected card unpins it
#
# ASCII only (see lib.ps1 header).

param([string]$Exe = '')

. (Join-Path $PSScriptRoot 'lib.ps1')

if ($Exe -eq '') { $Exe = Get-DefaultProbeExe }
Reset-Checks

$names = 'flow:preview-pinkey-opens', 'flow:preview-multipin-two', 'flow:preview-multipin-hover', 'flow:preview-multipin-unpin', 'flow:preview-multipin-cap', 'flow:preview-multipin-delete', 'flow:preview-pinkey-toggle'

$dataDir = Join-Path $env:TEMP 'shiyu-probe-run\preview-multipin'
& (Join-Path $PSScriptRoot 'seed.ps1') -DataDir $dataDir -AppBin (Split-Path -Parent $Exe) | Out-Null
$log = Join-Path $dataDir 'preview-multipin.log'
Remove-Item $log -ErrorAction SilentlyContinue

$p = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd 'preview-multipin'
try {
    $deadline = (Get-Date).AddSeconds(90)
    while (-not (Test-Path $log) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
} finally {
    if ($p) { Stop-ProbeApp $p }
}

if (-not (Test-Path $log)) {
    foreach ($n in $names) { Add-Check $n 'FAIL' 'the command wrote no log within 90s' }
    return $script:Checks
}

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

if ($lines['error'] -or ($lines['multipin'])) {
    $why = if ($lines['error']) { "the command threw {0}: {1}" -f $lines['error']['type'], $lines['error']['message'] } else { "not enough cards: {0}" -f $lines['multipin']['cards'] }
    foreach ($n in $names) { Add-Check $n 'FAIL' $why }
    return $script:Checks
}

function Show($Key) { if ($lines[$Key]) { ($lines[$Key].GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ', ' } else { "no $Key line" } }

$l = $lines['pinkey']
$ok = $l -and $l['visible'] -eq '1' -and $l['pinned'] -eq '1' -and $l['shows'] -eq 'True'
Add-Check 'flow:preview-pinkey-opens' $(if ($ok) { 'PASS' } else { 'FAIL' }) (Show 'pinkey')

$l = $lines['second']
$ok = $l -and $l['visible'] -eq '2' -and $l['pinned'] -eq '2' -and $l['aside'] -eq '1' -and $l['shows'] -eq 'True'
Add-Check 'flow:preview-multipin-two' $(if ($ok) { 'PASS' } else { 'FAIL' }) (Show 'second')

$l = $lines['hover']
$ok = $l -and $l['visible'] -eq '3' -and $l['pinned'] -eq '2' -and $l['hoverPinned'] -eq 'False' -and $l['shows'] -eq 'True'
Add-Check 'flow:preview-multipin-hover' $(if ($ok) { 'PASS' } else { 'FAIL' }) (Show 'hover')

# The hover step set the second pin aside too: after unpinning the oldest, one pinned remains.
$l = $lines['unpin']
$ok = $l -and $l['closed'] -eq 'True' -and $l['pinned'] -eq '1' -and $l['aside'] -eq '1'
Add-Check 'flow:preview-multipin-unpin' $(if ($ok) { 'PASS' } else { 'FAIL' }) (Show 'unpin')

# Ten more pins on top of the one still pinned: the attached newest plus at most 8 set aside.
$l = $lines['cap']
$ok = $l -and $l['aside'] -eq '8' -and $l['pinned'] -eq '9' -and $l['newest'] -eq 'True'
Add-Check 'flow:preview-multipin-cap' $(if ($ok) { 'PASS' } else { 'FAIL' }) (Show 'cap')

$l = $lines['delete']
$ok = $l -and $l['stillShown'] -eq 'False'
Add-Check 'flow:preview-multipin-delete' $(if ($ok) { 'PASS' } else { 'FAIL' }) (Show 'delete')

$l = $lines['toggle']
$ok = $l -and $l['newestPinned'] -eq 'False'
Add-Check 'flow:preview-pinkey-toggle' $(if ($ok) { 'PASS' } else { 'FAIL' }) (Show 'toggle')

return $script:Checks
