# probe-bar-keys2.ps1 - the key routing matrix across WINDOW states (research
# report P1 §2): pinned search, pinned pick, unpinned search, unpinned pick,
# and the edge-docked collapsed strip (keys must change nothing there).
#
# The DEBUG command "bar-keys2" flips the window state between rows (pinned /
# unpinned / edge-docked) and drives P / K / Down / S / Esc through the same
# synthetic input pipeline as probe-bar-keys. Each row logs focus, the search
# query and the target card's pinned state.
#
# Checks:
#   - flow:barkeys2-pinned-search-typing   pinned + search: P types "p"
#   - flow:barkeys2-pinned-pick-acts       pinned + pick: P pins, types nothing
#   - flow:barkeys2-unpinned-search-typing unpinned + search: P types "p"
#   - flow:barkeys2-unpinned-pick          unpinned: Down picks into the list
#   - flow:barkeys2-docked-stays           the docked strip ignores the keys and stays
#
# ASCII only (see lib.ps1 header).

param([string]$Exe = '')

. (Join-Path $PSScriptRoot 'lib.ps1')

if ($Exe -eq '') { $Exe = Get-DefaultProbeExe }
Reset-Checks

$names = 'flow:barkeys2-pinned-search-typing', 'flow:barkeys2-pinned-pick-acts', 'flow:barkeys2-unpinned-search-typing', 'flow:barkeys2-unpinned-pick', 'flow:barkeys2-docked-stays'

# Every row drives real synthesized keystrokes into the bar's focus: with someone
# at the machine their own typing/clicks fight the probe's, so the whole probe
# stands down (the same rule as probe-bar-dock / probe-reverse).
$userPresent = Test-UserPresent
if ($userPresent) {
    foreach ($n in $names) { Add-Check $n 'SKIP' 'user present (cursor moved between samples) - the whole probe stood down' }
    return $script:Checks
}

$names = 'flow:barkeys2-pinned-search-typing', 'flow:barkeys2-pinned-pick-acts', 'flow:barkeys2-unpinned-search-typing', 'flow:barkeys2-unpinned-pick', 'flow:barkeys2-docked-stays'

$dataDir = Join-Path $env:TEMP 'shiyu-probe-run\bar-keys2'
& (Join-Path $PSScriptRoot 'seed.ps1') -DataDir $dataDir -AppBin (Split-Path -Parent $Exe) | Out-Null
$log = Join-Path $dataDir 'bar-keys2.log'
Remove-Item $log -ErrorAction SilentlyContinue

$p = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd 'bar-keys2'
try {
    $deadline = (Get-Date).AddSeconds(120)
    while (-not (Test-Path $log) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
} finally {
    if ($p) { Stop-ProbeApp $p }
}

if (-not (Test-Path $log)) {
    foreach ($n in $names) { Add-Check $n 'FAIL' 'the command wrote no log within 120s' }
    return $script:Checks
}

$rows = @{}
$docked = @{}
foreach ($line in Get-Content $log -Encoding UTF8) {
    if ($line -match '^row\|state=([\w-]+)\|(.*)$') {
        $fields = @{}
        foreach ($pair in $Matches[2].Split('|')) {
            $kv = $pair.Split('=', 2)
            if ($kv.Count -eq 2) { $fields[$kv[0]] = $kv[1] }
        }
        $rows[$Matches[1]] = $fields
    }
    if ($line -match '^docked\|(.*)$') {
        foreach ($pair in $Matches[1].Split('|')) {
            $kv = $pair.Split('=', 2)
            if ($kv.Count -eq 2) { $docked[$kv[0]] = $kv[1] }
        }
    }
    if ($line -match '^error\|') {
        foreach ($n in $names) { Add-Check $n 'FAIL' "the command threw: $line" }
        return $script:Checks
    }
}

function RowValue([string]$State, [string]$Key) {
    if ($rows.ContainsKey($State) -and $rows[$State].ContainsKey($Key)) { return [string]$rows[$State][$Key] }
    return '?'
}

function FieldIs([string]$State, [string]$Key, [string]$Expected) {
    return (RowValue $State $Key) -eq $Expected
}

$ok = (FieldIs 'pinned-search' 'afterP' 'query=p') -and (FieldIs 'pinned-search' 'focus' 'Search')
$detail = "pinned search after P: afterP='$(RowValue 'pinned-search' 'afterP')', focus='$(RowValue 'pinned-search' 'focus')'"
Add-Check 'flow:barkeys2-pinned-search-typing' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

$ok = (FieldIs 'pinned-pick' 'afterP' 'query=')
$detail = "pinned pick after P: afterP='$(RowValue 'pinned-pick' 'afterP' '?')' (the letter acted, nothing typed)"
Add-Check 'flow:barkeys2-pinned-pick-acts' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

$ok = (FieldIs 'unpinned-search' 'afterP' 'query=p') -and (FieldIs 'unpinned-search' 'focus' 'Search')
$detail = "unpinned search after P: afterP='$(RowValue 'unpinned-search' 'afterP')', focus='$(RowValue 'unpinned-search' 'focus')'"
Add-Check 'flow:barkeys2-unpinned-search-typing' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

$ok = (FieldIs 'unpinned-pick' 'afterDown' 'focus=List')
$detail = "unpinned Down picked into the list: afterDown='$(RowValue 'unpinned-pick' 'afterDown' '?')'"
Add-Check 'flow:barkeys2-unpinned-pick' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

$collapsedBefore = FieldIs 'docked' 'collapsedBefore' 'True'
$stillCollapsed = FieldIs 'docked' 'stillCollapsed' 'True'
$queryEmpty = FieldIs 'docked' 'queryEmpty' 'True'
$ok = $collapsedBefore -and $stillCollapsed -and $queryEmpty
$detail = "docked strip ignored the keys: collapsed before=$collapsedBefore, still=$stillCollapsed, query empty=$queryEmpty"
Add-Check 'flow:barkeys2-docked-stays' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

return $script:Checks
