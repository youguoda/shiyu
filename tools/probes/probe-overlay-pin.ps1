# probe-overlay-pin.ps1 - the pin on the translation panel and the reverse input box
# (user request 2026-10-19): pinned, they stay on screen through their auto-dismiss paths -
# the panel through Esc, the reverse box through focus loss - and the first Esc unpins
# (keeping the window), the second closes.
#
# The DEBUG command "overlay-pin" drives both windows in the probe instance: pin, steal the
# focus / press Esc, and reads back visibility at each step. Nothing touches the clipboard.
#
# Checks:
#   - flow:panel-pin     the pinned panel survives Esc once and closes on the second
#   - flow:reverse-pin   the pinned reverse box survives losing the focus and Esc once,
#                        closes on the second
#
# ASCII only (see lib.ps1 header).

param([string]$Exe = '')

. (Join-Path $PSScriptRoot 'lib.ps1')

if ($Exe -eq '') { $Exe = Get-DefaultProbeExe }
Reset-Checks

$dataDir = Join-Path $env:TEMP 'shiyu-probe-run\overlay-pin'
& (Join-Path $PSScriptRoot 'seed.ps1') -DataDir $dataDir -AppBin (Split-Path -Parent $Exe) | Out-Null
$log = Join-Path $dataDir 'overlay-pin.log'
Remove-Item $log -ErrorAction SilentlyContinue

$p = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd 'overlay-pin' -ExtraEnv @{ SHIYU_FAKE_BACKEND = '1' }
try {
    $deadline = (Get-Date).AddSeconds(60)
    while (-not (Test-Path $log) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
} finally {
    if ($p) { Stop-ProbeApp $p }
}

if (-not (Test-Path $log)) {
    Add-Check 'flow:panel-pin' 'FAIL' 'the command wrote no log within 60s'
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

$panel = $lines['panel']
$ok = $panel -and $panel['surviveEsc1'] -eq 'True' -and $panel['closedAfterEsc2'] -eq 'True'
$detail = if ($panel) { "pinned panel survives Esc {0}, closes on the second {1}" -f $panel['surviveEsc1'], $panel['closedAfterEsc2'] } else { 'no panel line' }
Add-Check 'flow:panel-pin' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

$reverse = $lines['reverse']
$ok = $reverse -and $reverse['surviveFocusLoss'] -eq 'True' -and $reverse['surviveEsc1'] -eq 'True' -and $reverse['closedAfterEsc2'] -eq 'True'
$detail = if ($reverse) { "pinned reverse box survives focus loss {0} and Esc {1}, closes on the second {2}" -f $reverse['surviveFocusLoss'], $reverse['surviveEsc1'], $reverse['closedAfterEsc2'] } else { 'no reverse line' }
Add-Check 'flow:reverse-pin' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

return $script:Checks
