# probe-bar-slide.ps1 - the edge-docked bar's slide-out cadence (user report 2026-10-22: the
# bar stutters coming out of the strip).
#
# The DEBUG command "bar-slide" parks the pinned, edge-docked bar at the top, then collapses
# and slides it out ten times, recording when each frame landed and how long each window move
# took. With system animations off the slide is a single jump and the check stands down.
#
# Checks:
#   - perf:bar-slide-cadence   at least 8 frames per slide, median frame gap <= 20 ms, no gap
#                              over 50 ms (one missed vsync is tolerated; the old 16 ms
#                              DispatcherTimer measured ~5 frames, 31 ms median, 77-91 ms max)
#
# ASCII only (see lib.ps1 header).

param([string]$Exe = '')

. (Join-Path $PSScriptRoot 'lib.ps1')

if ($Exe -eq '') { $Exe = Get-DefaultProbeExe }
Reset-Checks

$dataDir = Join-Path $env:TEMP 'shiyu-probe-run\bar-slide'
& (Join-Path $PSScriptRoot 'seed.ps1') -DataDir $dataDir -AppBin (Split-Path -Parent $Exe) | Out-Null
$log = Join-Path $dataDir 'bar-slide.log'
Remove-Item $log -ErrorAction SilentlyContinue

$p = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd 'bar-slide'
try {
    $deadline = (Get-Date).AddSeconds(60)
    while (-not (Test-Path $log) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
} finally {
    if ($p) { Stop-ProbeApp $p }
}

$line = if (Test-Path $log) { Get-Content $log -Encoding UTF8 | Where-Object { $_ -like 'slide|*' } | Select-Object -First 1 } else { $null }
if (-not $line) {
    Add-Check 'perf:bar-slide-cadence' 'FAIL' 'no slide line (the command wrote no log, or threw)'
    return $script:Checks
}

$f = @{}
foreach ($pair in $line.Substring('slide|'.Length).Split('|')) {
    $kv = $pair.Split('=', 2)
    if ($kv.Count -eq 2) { $f[$kv[0]] = $kv[1] }
}

$detail = "frames/slide {0}, gap median {1} ms, gap max {2} ms, window move avg {3} ms / max {4} ms, slide span {5} ms" -f $f['frames'], $f['gapMedian'], $f['gapMax'], $f['moveAvg'], $f['moveMax'], $f['span']
if ($f['animated'] -ne 'True') {
    Add-Check 'perf:bar-slide-cadence' 'SKIP' "system animations are off - the slide is one jump ($detail)"
} else {
    $ok = ([double]$f['frames'] -ge 8) -and ([double]$f['gapMedian'] -le 20) -and ([double]$f['gapMax'] -le 50)
    Add-Check 'perf:bar-slide-cadence' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail
}

return $script:Checks
