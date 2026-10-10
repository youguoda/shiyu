# probe-content-size.ps1 - reading text follows the content size setting, controls
# do not (user request 2026-10-09; ADR-0012 typography 2-3: "bigger" only ever goes
# through the content size, the 14 DIP control text stays put).
#
# The DEBUG command "content-size" opens the bar and the reverse input box, logs the
# sizes at the standard level, switches the probe's setting to "Larger" (22) and logs
# again, then to "Smaller" (14, added 2026-10-10) and logs once more. The
# content-larger-* / content-smaller-* PNGs land in the data dir for a human look at
# whether anything got squeezed or cramped.
#
# Checks:
#   - data:content-size-logged     all three phases were logged
#   - flow:content-size-standard   card body 18/31, reverse box (compact) 16
#   - flow:content-size-follows    at Larger: card body 22/38, reverse box input and output 20
#   - flow:content-size-smaller    at Smaller: card body 14/24, reverse box input and output 12
#   - flow:controls-stay-14        the bar's search box stays 14 at every level
#
# ASCII only (see lib.ps1 header).

param([string]$Exe = '')

. (Join-Path $PSScriptRoot 'lib.ps1')

if ($Exe -eq '') { $Exe = Get-DefaultProbeExe }
Reset-Checks

$dataDir = Join-Path $env:TEMP 'shiyu-probe-run\content-size'
& (Join-Path $PSScriptRoot 'seed.ps1') -DataDir $dataDir -AppBin (Split-Path -Parent $Exe) | Out-Null
$log = Join-Path $dataDir 'content-size.log'
Remove-Item $log -ErrorAction SilentlyContinue

$p = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd 'content-size'
try {
    $deadline = (Get-Date).AddSeconds(40)
    while (-not (Test-Path $log) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
} finally {
    if ($p) { Stop-ProbeApp $p }
}

if (-not (Test-Path $log)) {
    Add-Check 'data:content-size-logged' 'FAIL' 'the command wrote no log within 40s'
    return $script:Checks
}

# size|<phase>|card=..|cardLine=..|search=..|reverseInput=..|reverseOutput=..
$phases = @{}
foreach ($line in Get-Content $log) {
    if ($line -match '^size\|(\w+)\|(.*)$') {
        $fields = @{}
        foreach ($pair in $Matches[2].Split('|')) {
            $kv = $pair.Split('=', 2)
            $fields[$kv[0]] = $kv[1]
        }
        $phases[$Matches[1]] = $fields
    }
}

if (-not ($phases.ContainsKey('standard') -and $phases.ContainsKey('larger') -and $phases.ContainsKey('smaller'))) {
    Add-Check 'data:content-size-logged' 'FAIL' ("phases logged: {0}" -f (($phases.Keys | Sort-Object) -join ', '))
    return $script:Checks
}
Add-Check 'data:content-size-logged' 'PASS' 'standard, larger and smaller'

function Show-Phase($f) {
    "card {0}/{1}, reverse input {2}, output {3}, search {4}" -f $f['card'], $f['cardLine'], $f['reverseInput'], $f['reverseOutput'], $f['search']
}

$s = $phases['standard']
$ok = $s['card'] -eq '18' -and $s['cardLine'] -eq '31' -and $s['reverseInput'] -eq '16' -and $s['reverseOutput'] -eq '16'
Add-Check 'flow:content-size-standard' $(if ($ok) { 'PASS' } else { 'FAIL' }) (Show-Phase $s)

$l = $phases['larger']
$ok = $l['card'] -eq '22' -and $l['cardLine'] -eq '38' -and $l['reverseInput'] -eq '20' -and $l['reverseOutput'] -eq '20'
Add-Check 'flow:content-size-follows' $(if ($ok) { 'PASS' } else { 'FAIL' }) (Show-Phase $l)

$m = $phases['smaller']
$ok = $m['card'] -eq '14' -and $m['cardLine'] -eq '24' -and $m['reverseInput'] -eq '12' -and $m['reverseOutput'] -eq '12'
Add-Check 'flow:content-size-smaller' $(if ($ok) { 'PASS' } else { 'FAIL' }) (Show-Phase $m)

$ok = $s['search'] -eq '14' -and $l['search'] -eq '14' -and $m['search'] -eq '14'
Add-Check 'flow:controls-stay-14' $(if ($ok) { 'PASS' } else { 'FAIL' }) `
    ("bar search box {0} at standard, {1} at larger, {2} at smaller" -f $s['search'], $l['search'], $m['search'])

return $script:Checks
