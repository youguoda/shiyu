# probe-stream-jank.ps1 - the translation panel stays responsive while a language
# model streams a long answer (user report 2026-10-15: while the LLM was writing,
# the output came in jerks, dragging the scrollbar stuttered, and Windows sometimes
# showed the panel as not responding).
#
# The DEBUG command "stream-jank" opens the panel on a long source text with
# SHIYU_FAKE_BACKEND=stream: a fake backend that streams a ~6000-character
# Markdown answer (headings, paragraphs, lists, quotes, code, tables) in 3-9
# character pieces, 100 pieces a second, the way a real model does. While it
# streams, a no-op is posted at Input priority every 25 ms - mouse and keyboard
# input wait at that level - and its wait is measured; dispatcher hooks record
# the longest single piece of UI-thread work. Afterwards a fresh box renders the
# whole answer at once, and its text must equal what the panel built piece by piece.
# Then the panel switches to sentence-by-sentence view and streams the answer again,
# measured the same way (the pairs list refreshes as the text grows).
#
# Before the fix (2026-10-15): input waited up to 15.2 s, one stretch of UI work
# lasted 15.2 s (Windows calls a window hung after 5 s), the UI was busy 98% of the
# time, and the stream itself slowed from 8.4 s to 16.1 s.
#
# Checks (both passes):
#   - perf:stream-input-latency      input waited at most 150 ms, 95% of the time at most 50 ms
#   - perf:stream-longest-ui-work    no single piece of UI-thread work took more than 150 ms. The
#                                    first render of a session pays the JIT warm-up; on the same
#                                    build it measured 34 ms one day and 100-120 ms the next (2026-10-16,
#                                    machine load). 150 ms stays far below the 15 s freeze it guards.
#   - flow:stream-renders-everything both streams finished; the whole-text panel shows exactly
#                                    what a fresh render of the full answer shows, and the
#                                    sentence view has pairs
#
# ASCII only (see lib.ps1 header).

param([string]$Exe = '')

. (Join-Path $PSScriptRoot 'lib.ps1')

if ($Exe -eq '') { $Exe = Get-DefaultProbeExe }
Reset-Checks

$CheckNames = @('perf:stream-input-latency', 'perf:stream-longest-ui-work', 'flow:stream-renders-everything')

# A long clipboard, the way "translate the clipboard" gets one.
$source = (1..12 | ForEach-Object {
    "Paragraph $_ of the probe source. It is long enough to wrap several times in the panel, " +
    "because people translate whole articles off the clipboard, not just one line."
}) -join ' '

$dataDir = Join-Path $env:TEMP 'shiyu-probe-run\stream-jank'
& (Join-Path $PSScriptRoot 'seed.ps1') -DataDir $dataDir -AppBin (Split-Path -Parent $Exe) | Out-Null
$log = Join-Path $dataDir 'stream-jank.log'
Remove-Item $log -ErrorAction SilentlyContinue

$p = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd 'stream-jank' -Text $source -ExtraEnv @{
    SHIYU_FAKE_BACKEND = 'stream'
}
try {
    $deadline = (Get-Date).AddSeconds(120)
    $done = $false
    while ((Get-Date) -lt $deadline -and -not $done) {
        Start-Sleep -Milliseconds 500
        if (Test-Path $log) {
            $done = @(Get-Content $log -Encoding UTF8 | Where-Object { $_ -eq 'done' }).Count -gt 0
        }
    }
} finally {
    if ($p) { Stop-ProbeApp $p }
}

$passes = @{}
if (Test-Path $log) {
    foreach ($line in Get-Content $log -Encoding UTF8) {
        if ($line -match '^stream\|(.*)$') {
            $fields = @{}
            foreach ($pair in $Matches[1].Split('|')) {
                $kv = $pair.Split('=', 2)
                if ($kv.Count -eq 2) { $fields[$kv[0]] = $kv[1] }
            }
            $passes[$fields['pass']] = $fields
        }
    }
}

if (-not $passes.ContainsKey('whole') -or -not $passes.ContainsKey('sentence')) {
    foreach ($name in $CheckNames) { Add-Check $name 'FAIL' 'stream-jank.log lacks a pass (probe did not finish within 120s)' }
    return $script:Checks
}

function Get-PassDetail($f) {
    return '{0}: input waited max {1} ms, p95 {2} ms over {3} pings; longest UI work {4} ms; UI busy {5}% of {6}s, {7} chars' -f `
        $f['pass'], $f['inputMaxMs'], $f['inputP95Ms'], $f['pings'], $f['longestOpMs'], $f['busyPct'], $f['seconds'], $f['chars']
}

$latencyOk = $true
$longestOk = $true
foreach ($f in @($passes['whole'], $passes['sentence'])) {
    if ([double]$f['inputMaxMs'] -gt 150 -or [double]$f['inputP95Ms'] -gt 50) { $latencyOk = $false }
    if ([double]$f['longestOpMs'] -gt 150) { $longestOk = $false }
}
$detail = (Get-PassDetail $passes['whole']) + ' | ' + (Get-PassDetail $passes['sentence'])

if ($latencyOk) {
    Add-Check 'perf:stream-input-latency' 'PASS' $detail
} else {
    Add-Check 'perf:stream-input-latency' 'FAIL' $detail
}

$longestDetail = 'longest UI-thread operation: whole {0} ms, sentence-by-sentence {1} ms' -f `
    $passes['whole']['longestOpMs'], $passes['sentence']['longestOpMs']
if ($longestOk) {
    Add-Check 'perf:stream-longest-ui-work' 'PASS' $longestDetail
} else {
    Add-Check 'perf:stream-longest-ui-work' 'FAIL' $longestDetail
}

$whole = $passes['whole']
$sentence = $passes['sentence']
$renderDetail = 'whole: state {0}, {1} chars, same as a fresh render: {2}; sentence-by-sentence: state {3}, {4} pairs' -f `
    $whole['state'], $whole['chars'], $whole['sameAsFresh'], $sentence['state'], $sentence['pairs']
if ($whole['state'] -eq 'Finished' -and [int]$whole['chars'] -gt 0 -and $whole['sameAsFresh'] -eq 'True' `
    -and $sentence['state'] -eq 'Finished' -and [int]$sentence['pairs'] -gt 0) {
    Add-Check 'flow:stream-renders-everything' 'PASS' $renderDetail
} else {
    Add-Check 'flow:stream-renders-everything' 'FAIL' $renderDetail
}

return $script:Checks
