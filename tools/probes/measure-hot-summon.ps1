# measure-hot-summon.ps1 - the hot path (review 2026-10-16 F-21; ADR-0006's real metric):
# with Shiyu already running, how long from "quick paste" until the bar is visible and its
# list laid out.
#
# Method: one probe instance on an isolated seeded data dir runs the DEBUG command
# "summon-timing". Inside the running process it summons the bar 21 times (the first is a
# warm-up and is not counted), each time timing from the summon call until the UI thread
# is idle again (layout and render work done), then dismisses it and waits 400 ms so the
# lightweight release (drop cards, GC, trim the working set) has happened. It does this
# with lightweight mode on and again with it off, and writes P50 / P95 / max in ms.
#
# The F-21 rule: if the lightweight P95 is over 300 ms, lightweight mode should wait
# before releasing. Not part of run-all (it is a measurement, not a pass/fail check).
#
# ASCII only (see lib.ps1 header).

param([string]$Exe = '')

. (Join-Path $PSScriptRoot 'lib.ps1')

if ($Exe -eq '') { $Exe = Get-DefaultProbeExe }
if (-not (Test-Path $Exe)) { throw "probe exe not found: $Exe (build src\Shiyu.App -c Debug first)" }

$dataDir = Join-Path $env:TEMP 'shiyu-probe-run\summon-timing'
& (Join-Path $PSScriptRoot 'seed.ps1') -DataDir $dataDir -AppBin (Split-Path -Parent $Exe) | Out-Null
$log = Join-Path $dataDir 'summon-timing.log'
Remove-Item $log -ErrorAction SilentlyContinue

$p = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd 'summon-timing'
try {
    $deadline = (Get-Date).AddSeconds(120)
    while (-not (Test-Path $log) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
} finally {
    if ($p) { Stop-ProbeApp $p }
}

if (-not (Test-Path $log)) { throw 'the summon-timing command wrote no log within 120s' }

foreach ($line in Get-Content $log -Encoding UTF8) {
    if ($line -match '^(lightweight|steady)\|(.*)$') {
        $fields = @{}
        foreach ($pair in $Matches[2].Split('|')) {
            $kv = $pair.Split('=', 2)
            if ($kv.Count -eq 2) { $fields[$kv[0]] = $kv[1] }
        }
        "{0,-12} runs {1}  P50 {2} ms  P95 {3} ms  max {4} ms" -f $Matches[1], $fields['runs'], $fields['p50'], $fields['p95'], $fields['max']
    }
}
