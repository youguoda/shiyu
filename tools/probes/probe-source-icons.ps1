# probe-source-icons.ps1 - the source-app icon can be switched off, and an empty icon
# slot shows nothing (user request 2026-10-14; it used to show Shiyu's own mark).
#
# The seed gives no app any icon, so the DEBUG command "source-icons" first stores one
# for probe-browser: the bar and the library then hold rows with an icon and rows
# without. It looks at both with the switch on, turns it off through the settings
# store, looks again, and finally holds the key hints (Ctrl) on the bar.
# source-icons-bar-on/off.png and source-icons-lib-on/off.png land in the data dir.
#
# Checks:
#   - flow:source-icons-bar-on      every card keeps its slot; it draws the app icon where
#                                   there is one and nothing at all where there is none
#   - flow:source-icons-library-on  the same for library rows, and the text column stays
#                                   aligned between rows with and without an icon (image
#                                   rows carry a thumbnail column, so they align among
#                                   themselves)
#   - flow:source-icons-off         switched off, every slot is gone in both windows and
#                                   each library row's text moves left by exactly the
#                                   slot's 18 DIP plus its 6 DIP gap
#   - flow:source-icons-keycap      with the slot gone, holding Ctrl still shows the 18 DIP
#                                   number cap at the very start of the card's top line
#
# ASCII only (see lib.ps1 header).

param([string]$Exe = '')

. (Join-Path $PSScriptRoot 'lib.ps1')

if ($Exe -eq '') { $Exe = Get-DefaultProbeExe }
Reset-Checks

$dataDir = Join-Path $env:TEMP 'shiyu-probe-run\source-icons'
& (Join-Path $PSScriptRoot 'seed.ps1') -DataDir $dataDir -AppBin (Split-Path -Parent $Exe) | Out-Null
$log = Join-Path $dataDir 'source-icons.log'
Remove-Item $log -ErrorAction SilentlyContinue

$p = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd 'source-icons'
try {
    $deadline = (Get-Date).AddSeconds(45)
    while (-not (Test-Path $log) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
} finally {
    if ($p) { Stop-ProbeApp $p }
}

if (-not (Test-Path $log)) {
    Add-Check 'flow:source-icons-bar-on' 'FAIL' 'the command wrote no log within 45s'
    return $script:Checks
}

# Every line is "step|key=value|..."; several lines share a step.
$rows = @{}
foreach ($line in Get-Content $log) {
    if ($line -match '^([\w-]+)\|(.*)$') {
        $fields = @{}
        foreach ($pair in $Matches[2].Split('|')) {
            $kv = $pair.Split('=', 2)
            if ($kv.Count -eq 2) { $fields[$kv[0]] = $kv[1] }
        }
        if (-not $rows.ContainsKey($Matches[1])) { $rows[$Matches[1]] = @() }
        $rows[$Matches[1]] += , $fields
    }
}

function Rows($step) { if ($rows.ContainsKey($step)) { $rows[$step] } else { @() } }

# On: the slot is there; one image where the app has an icon, none where it has not.
function SlotsHonest($list) {
    foreach ($r in $list) {
        if ($r['slot'] -ne 'True') { return $false }
        $want = if ($r['icon'] -eq 'True') { '1' } else { '0' }
        if ($r['images'] -ne $want) { return $false }
    }
    return $true
}

function Mixed($list) {
    (@($list | Where-Object { $_['icon'] -eq 'True' }).Count -gt 0) -and (@($list | Where-Object { $_['icon'] -eq 'False' }).Count -gt 0)
}

function Summary($list) {
    $with = @($list | Where-Object { $_['icon'] -eq 'True' }).Count
    $slots = @($list | Where-Object { $_['slot'] -eq 'True' }).Count
    $images = ($list | ForEach-Object { [int]$_['images'] } | Measure-Object -Sum).Sum
    "{0} rows, {1} with an icon, {2} slots shown, {3} images drawn" -f @($list).Count, $with, $slots, $images
}

$barOn = Rows 'bar-on'
$ok = @($barOn).Count -gt 0 -and (Mixed $barOn) -and (SlotsHonest $barOn)
Add-Check 'flow:source-icons-bar-on' $(if ($ok) { 'PASS' } else { 'FAIL' }) (Summary $barOn)

$libOn = @(Rows 'lib-on')

# Where the text starts, per row kind (image rows have a thumbnail column of their own).
function Lefts($list, $thumb) {
    @($list | Where-Object { $_['thumb'] -eq $thumb } | ForEach-Object { $_['textLeft'] } | Sort-Object -Unique)
}

$plain = Lefts $libOn 'False'
$images = Lefts $libOn 'True'
$ok = $libOn.Count -gt 0 -and (Mixed $libOn) -and (SlotsHonest $libOn) -and $plain.Count -eq 1 -and $images.Count -le 1
Add-Check 'flow:source-icons-library-on' $(if ($ok) { 'PASS' } else { 'FAIL' }) `
    ("{0}; text starts at {1} (rows without a thumbnail) and {2} (image rows)" -f (Summary $libOn), ($plain -join '/'), ($images -join '/'))

$barOff = @(Rows 'bar-off')
$libOff = @(Rows 'lib-off')

# The library rebuilds the same rows in the same order, so row i before and after compare directly.
$shifts = @()
for ($i = 0; $i -lt [Math]::Min($libOn.Count, $libOff.Count); $i++) {
    $shifts += [int]$libOn[$i]['textLeft'] - [int]$libOff[$i]['textLeft']
}
$uniqueShifts = @($shifts | Sort-Object -Unique)
$ok = $barOff.Count -gt 0 -and $libOff.Count -eq $libOn.Count `
    -and @($barOff | Where-Object { $_['slot'] -ne 'False' }).Count -eq 0 `
    -and @($libOff | Where-Object { $_['slot'] -ne 'False' }).Count -eq 0 `
    -and $uniqueShifts.Count -eq 1 -and $uniqueShifts[0] -eq 24
Add-Check 'flow:source-icons-off' $(if ($ok) { 'PASS' } else { 'FAIL' }) `
    ("bar: {0}; library: {1}; library text moved left by {2} DIP" -f (Summary $barOff), (Summary $libOff), ($uniqueShifts -join '/'))

$cap = (Rows 'keycap') | Select-Object -First 1
$ok = $cap -and $cap['visible'] -eq 'True' -and $cap['width'] -eq '18' -and $cap['left'] -eq '0' -and $cap['text'] -eq '1'
$detail = if ($cap) { "visible {0}, {1} DIP wide, {2} DIP from the line start, reads '{3}'" -f $cap['visible'], $cap['width'], $cap['left'], $cap['text'] } else { 'no keycap line' }
Add-Check 'flow:source-icons-keycap' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

return $script:Checks
