# probe-credentials.ps1 - own-key credentials are kept per provider, shown masked,
# revealed on request, and switching provider takes its address, model and key along
# (user request 2026-10-10).
#
# The DEBUG command "credentials" stores two providers in the probe's settings
# (DeepSeek on its alternate model, Zhipu - the current one), opens Settings on the
# credential row, and drives it: reveal and hide, switch to DeepSeek from the saved
# list, pick a provider never saved from the preset dropdown. The log never carries a
# key; the last line checks the settings file on disk holds none in plain text.
#
# Checks:
#   - flow:credential-masked    a saved key shows masked, with the reveal button
#   - flow:credential-reveal    reveal shows the whole key; a second click masks it again
#   - flow:credential-marks     the preset dropdown marks both saved providers, Zhipu selected
#   - flow:credential-list      the saved list has both, Zhipu marked as in use
#   - flow:credential-switch    switching to DeepSeek brings its address, model and key;
#                               dropdown, card and list follow
#   - flow:credential-fresh     a provider never saved shows the entry box and the hint
#   - flow:credential-disk      the settings file holds the keys protected, never plain
#
# ASCII only (see lib.ps1 header).

param([string]$Exe = '')

. (Join-Path $PSScriptRoot 'lib.ps1')

if ($Exe -eq '') { $Exe = Get-DefaultProbeExe }
Reset-Checks

$dataDir = Join-Path $env:TEMP 'shiyu-probe-run\credentials'
& (Join-Path $PSScriptRoot 'seed.ps1') -DataDir $dataDir -AppBin (Split-Path -Parent $Exe) | Out-Null
$log = Join-Path $dataDir 'credentials.log'
Remove-Item $log -ErrorAction SilentlyContinue

$p = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd 'credentials'
try {
    $deadline = (Get-Date).AddSeconds(40)
    while (-not (Test-Path $log) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
} finally {
    if ($p) { Stop-ProbeApp $p }
}

if (-not (Test-Path $log)) {
    Add-Check 'flow:credential-masked' 'FAIL' 'the command wrote no log within 40s'
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

function Test-Card($card, [bool]$saved) {
    if (-not $card) { return $false }
    if ($saved) {
        return $card['saved'] -eq 'True' -and $card['entry'] -eq 'False' -and $card['masked'] -eq 'True' -and $card['reveal'] -eq 'show'
    }
    return $card['saved'] -eq 'False' -and $card['entry'] -eq 'True'
}

function Show-Card($card) {
    if (-not $card) { return 'missing' }
    return "saved {0}, entry {1}, masked {2}, button {3}" -f $card['saved'], $card['entry'], $card['masked'], $card['reveal']
}

$card = $lines['card']
Add-Check 'flow:credential-masked' $(if (Test-Card $card $true) { 'PASS' } else { 'FAIL' }) (Show-Card $card)

$revealed = $lines['revealed']
$hidden = $lines['hidden']
$ok = $revealed -and $revealed['full'] -eq 'True' -and $revealed['reveal'] -eq 'hide' -and (Test-Card $hidden $true)
$detail = if ($revealed) { "revealed whole {0} (button {1}); then {2}" -f $revealed['full'], $revealed['reveal'], (Show-Card $hidden) } else { 'no revealed line' }
Add-Check 'flow:credential-reveal' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

$picker = $lines['picker']
$ok = $picker -and $picker['selected'] -eq 'zhipu' -and $picker['marked'] -eq 'deepseek+zhipu'
$detail = if ($picker) { "selected {0}, marked {1}" -f $picker['selected'], $picker['marked'] } else { 'no picker line' }
Add-Check 'flow:credential-marks' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

$list = $lines['list']
$ok = $list -and $list['lines'] -eq '2' -and $list['current'] -eq '1'
$detail = if ($list) { "{0} line(s), in use: #{1}" -f $list['lines'], $list['current'] } else { 'no list line' }
Add-Check 'flow:credential-list' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

$switched = $lines['switched']
$ok = $switched -and $switched['url'] -eq 'https://api.deepseek.com' -and $switched['model'] -eq 'deepseek-v4-pro' `
    -and $switched['configured'] -eq 'True' -and (Test-Card $lines['switchedCard'] $true) `
    -and $lines['switchedPicker']['selected'] -eq 'deepseek' -and $lines['switchedList']['current'] -eq '0'
$detail = if ($switched) {
    "url {0}, model {1}, configured {2}; picker {3}; in use #{4}; card {5}" -f $switched['url'], $switched['model'], `
        $switched['configured'], $lines['switchedPicker']['selected'], $lines['switchedList']['current'], (Show-Card $lines['switchedCard'])
} else { 'no switched line' }
Add-Check 'flow:credential-switch' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

$fresh = $lines['fresh']
$ok = $fresh -and $fresh['configured'] -eq 'False' -and $fresh['hint'] -eq 'True' -and (Test-Card $lines['freshCard'] $false)
$detail = if ($fresh) { "configured {0}, hint {1}; card {2}" -f $fresh['configured'], $fresh['hint'], (Show-Card $lines['freshCard']) } else { 'no fresh line' }
Add-Check 'flow:credential-fresh' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

$disk = $lines['disk']
$ok = $disk -and $disk['plain'] -eq 'False' -and $disk['protected'] -eq 'True'
$detail = if ($disk) { "plain key on disk {0}, protected {1}" -f $disk['plain'], $disk['protected'] } else { 'no disk line' }
Add-Check 'flow:credential-disk' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

return $script:Checks
