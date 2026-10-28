# probe-bar-keys2.ps1 - the key routing matrix across WINDOW states (research
# report P1 section 2): pinned search, pinned pick, unpinned search, unpinned pick,
# and the edge-docked collapsed strip (keys must change nothing there).
#
# The DEBUG command "bar-keys2" flips the window state between rows (pinned /
# unpinned / edge-docked) and drives P / K / Down / S / Esc through the same
# synthetic input pipeline as probe-bar-keys. Each row logs focus, the search
# query and the target card's pinned state.
#
# Checks:
#   - flow:barkeys2-pinned-search-typing   pinned + search: P types "p"
#   - flow:barkeys2-pinned-pick-acts       pinned + pick: P pins the clicked card, types nothing,
#                                          and the card stays selected through a list rebuild
#   - flow:barkeys2-unpinned-search-typing unpinned + search: P types "p"
#   - flow:barkeys2-unpinned-pick          unpinned: Down with an empty search picks, P then acts
#   - flow:barkeys2-docked-stays           the docked strip ignores the keys and stays
#
# ASCII only (see lib.ps1 header).

param([string]$Exe = '')

. (Join-Path $PSScriptRoot 'lib.ps1')

if ($Exe -eq '') { $Exe = Get-DefaultProbeExe }
Reset-Checks

$names = 'flow:barkeys2-pinned-search-typing', 'flow:barkeys2-pinned-pick-acts', 'flow:barkeys2-unpinned-search-typing', 'flow:barkeys2-unpinned-pick', 'flow:barkeys2-docked-stays'

# Every key goes through WPF's own input pipeline inside the probe's window (the same synthetic
# path as probe-bar-keys): nothing reaches the real keyboard or the foreground window, so the
# probe runs while someone uses the machine.

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

# Each row presses P, K, Down, S, Esc in that order and logs every step under its own key
# (afterP_focus, afterK_focus, ...). Down comes after K has typed "k": by the rules a Down with
# a query stays in the search box, so the picking checks read the state right after P.
$ok = (FieldIs 'pinned-search' 'afterP_query' 'p') -and (FieldIs 'pinned-search' 'afterP_focus' 'Search')
$detail = "pinned search after P: query '$(RowValue 'pinned-search' 'afterP_query')', focus $(RowValue 'pinned-search' 'afterP_focus')"
Add-Check 'flow:barkeys2-pinned-search-typing' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

# The card was clicked right after the previous row's Esc started the search debounce: when the
# debounce rebuilt the list it used to reset the selection to the first row, and P pinned that
# one instead (QA F-06). Picking keeps the clicked card selected through a rebuild.
$ok = (FieldIs 'pinned-pick' 'afterP_query' '') -and (FieldIs 'pinned-pick' 'afterP_focus' 'List') -and (FieldIs 'pinned-pick' 'afterP_pinned' 'True') -and (FieldIs 'pinned-pick' 'afterP_selected' 'True')
$detail = "pinned pick after P: query '$(RowValue 'pinned-pick' 'afterP_query')', focus $(RowValue 'pinned-pick' 'afterP_focus'), clicked card pinned $(RowValue 'pinned-pick' 'afterP_pinned'), still selected $(RowValue 'pinned-pick' 'afterP_selected')"
Add-Check 'flow:barkeys2-pinned-pick-acts' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

$ok = (FieldIs 'unpinned-search' 'afterP_query' 'p') -and (FieldIs 'unpinned-search' 'afterP_focus' 'Search')
$detail = "unpinned search after P: query '$(RowValue 'unpinned-search' 'afterP_query')', focus $(RowValue 'unpinned-search' 'afterP_focus')"
Add-Check 'flow:barkeys2-unpinned-search-typing' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

# The orchestrator pressed Down with an empty search before this row: that picks, so P acts.
$ok = (FieldIs 'unpinned-pick' 'afterP_focus' 'List') -and (FieldIs 'unpinned-pick' 'afterP_query' '')
$detail = "unpinned, Down with an empty search then P: focus $(RowValue 'unpinned-pick' 'afterP_focus'), query '$(RowValue 'unpinned-pick' 'afterP_query')'"
Add-Check 'flow:barkeys2-unpinned-pick' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

# The docked line is parsed into its own table ($docked), not into $rows.
$collapsedBefore = $docked['collapsedBefore'] -eq 'True'
$stillCollapsed = $docked['stillCollapsed'] -eq 'True'
$queryEmpty = $docked['queryEmpty'] -eq 'True'
$ok = $collapsedBefore -and $stillCollapsed -and $queryEmpty
$detail = "docked strip ignored the keys: collapsed before=$collapsedBefore, still=$stillCollapsed, query empty=$queryEmpty"
Add-Check 'flow:barkeys2-docked-stays' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

return $script:Checks
