# probe-bar-newest.ps1 - the bar opens at the newest entry (user report 2026-10-04:
# "opening the bar sometimes lands at the very bottom, on the oldest entries").
#
# The DEBUG probe command "bar-newest" drives the real paths inside the app. It
# makes the list three pages long, and each path first scrolls it to the bottom:
#   reopen             dismiss + summon, lightweight teardown on (the default)
#   reopen-kept-cards  the same with the teardown off: cards and offset survive
#                      the hide (the old "the bar remembers its scroll" design)
#   copy-while-away    the bar stays open, another window holds the focus, and a
#                      write lands from a pool thread (the clipboard pipeline's
#                      road) - the cause the user actually hit
#   paste-summon       the paste-mode summon over the open resident bar
#   copy-while-in-use  the bar holds the focus when the same write lands: this
#                      one must KEEP the place (skipped when the foreground lock
#                      refused the probe the focus)
# The command logs the scroll state per path into <data dir>\bar-newest.log.
#
# Checks:
#   - window:bar-newest-found          the bar window appeared
#   - data:bar-newest-log              the command walked every path
#   - data:bar-newest-deep:<path>      the list really was scrolled down first
#   - flow:bar-newest:<path>           afterwards the list is back at the top
#   - flow:bar-newest-one-page         back at the top without paging the
#                                      whole history in again on the way
#   - flow:bar-keeps-place-in-use      the in-use write left the place alone
#
# ASCII only (see lib.ps1 header).

param([string]$Exe = '')

. (Join-Path $PSScriptRoot 'lib.ps1')

if ($Exe -eq '') { $Exe = Get-DefaultProbeExe }
Reset-Checks

$dataDir = Join-Path $env:TEMP 'shiyu-probe-run\bar-newest'
& (Join-Path $PSScriptRoot 'seed.ps1') -DataDir $dataDir | Out-Null
$log = Join-Path $dataDir 'bar-newest.log'
Remove-Item $log -ErrorAction SilentlyContinue

$p = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd 'bar-newest'
try {
    $hwnd = Wait-ProbeWindow $p.Id 384
    if ($hwnd -eq [IntPtr]::Zero) {
        Add-Check 'window:bar-newest-found' 'FAIL' 'no 384-DIP window of the probe pid'
        return $script:Checks
    }
    Add-Check 'window:bar-newest-found' 'PASS' ("hwnd={0}" -f $hwnd)

    $deadline = (Get-Date).AddSeconds(60)
    while (-not (Test-Path $log) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
} finally {
    if ($p) { Stop-ProbeApp $p }
}

if (-not (Test-Path $log)) {
    Add-Check 'data:bar-newest-log' 'FAIL' 'the command wrote no log within 60s'
    return $script:Checks
}

# "<path> before=<n> offset=<n> loaded=<n> total=<n> active=<True|False>"
$rows = @{}
foreach ($line in Get-Content $log) {
    if ($line -match '^(\S+) before=(-?\d+) offset=(-?\d+) loaded=(\d+) total=(\d+) active=(\w+)$') {
        $rows[$Matches[1]] = @{
            Before = [double]$Matches[2]; Offset = [double]$Matches[3]
            Loaded = [int]$Matches[4]; Total = [int]$Matches[5]; Active = ($Matches[6] -eq 'True') }
    }
}

$paths = @('reopen', 'reopen-kept-cards', 'copy-while-away', 'paste-summon', 'copy-while-in-use')
$missing = @($paths | Where-Object { -not $rows.ContainsKey($_) })
if ($missing.Count -gt 0) {
    Add-Check 'data:bar-newest-log' 'FAIL' ("log lacks: {0}" -f ($missing -join ', '))
    return $script:Checks
}
Add-Check 'data:bar-newest-log' 'PASS' ("{0} paths logged" -f $paths.Count)

foreach ($path in $paths) {
    $r = $rows[$path]
    $deep = $r.Before -gt 0
    Add-Check "data:bar-newest-deep:$path" $(if ($deep) { 'PASS' } else { 'FAIL' }) `
        ("offset before the step {0} DIP" -f $r.Before)
    if ($path -eq 'copy-while-in-use') { continue }

    $detail = "offset after {0} DIP, {1} of {2} entries loaded" -f $r.Offset, $r.Loaded, $r.Total
    if (-not $deep) {
        Add-Check "flow:bar-newest:$path" 'SKIP' 'precondition not met'
    } elseif ($r.Offset -eq 0) {
        Add-Check "flow:bar-newest:$path" 'PASS' $detail
    } else {
        Add-Check "flow:bar-newest:$path" 'FAIL' $detail
    }
}

# Back at the top must not mean "paged everything back in first": the stale
# bottom offset, if ever reported, pulls every page in on its way.
$away = $rows['copy-while-away']
$onePage = $away.Loaded -lt $away.Total
Add-Check 'flow:bar-newest-one-page' $(if ($onePage) { 'PASS' } else { 'FAIL' }) `
    ("after the away write {0} of {1} entries loaded" -f $away.Loaded, $away.Total)

$inUse = $rows['copy-while-in-use']
$detail = "offset {0} -> {1} DIP" -f $inUse.Before, $inUse.Offset
if (-not $inUse.Active) {
    Add-Check 'flow:bar-keeps-place-in-use' 'SKIP' "the bar never got the focus (foreground lock); $detail"
} elseif ($inUse.Before -gt 0 -and $inUse.Offset -gt 0) {
    Add-Check 'flow:bar-keeps-place-in-use' 'PASS' $detail
} else {
    Add-Check 'flow:bar-keeps-place-in-use' 'FAIL' $detail
}

return $script:Checks
