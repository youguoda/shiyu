# probe-ocr.ps1 - screenshots are read offline (review 2026-10-16 F-07): Windows.Media.Ocr
# recognises the text in an image without network, and recognising in the background does
# not stall the UI thread.
#
# The DEBUG command "ocr" renders a PNG that says a known phrase, recognises it once
# (the text must come back), then recognises six copies while pinging the dispatcher at
# input priority every 25 ms - the waits must stay small.
#
# Checks:
#   - flow:ocr-reads-the-image    the rendered phrase is recognised offline (zh + latin)
#   - flow:ocr-keeps-ui-responsive    the longest input-priority wait during recognition
#                                 stays under 150 ms
#
# ASCII only (see lib.ps1 header).

param([string]$Exe = '')

. (Join-Path $PSScriptRoot 'lib.ps1')

if ($Exe -eq '') { $Exe = Get-DefaultProbeExe }
Reset-Checks

$dataDir = Join-Path $env:TEMP 'shiyu-probe-run\ocr'
& (Join-Path $PSScriptRoot 'seed.ps1') -DataDir $dataDir -AppBin (Split-Path -Parent $Exe) | Out-Null
$log = Join-Path $dataDir 'ocr.log'
Remove-Item $log -ErrorAction SilentlyContinue

$p = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd 'ocr'
try {
    $deadline = (Get-Date).AddSeconds(90)
    while (-not (Test-Path $log) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
} finally {
    if ($p) { Stop-ProbeApp $p }
}

if (-not (Test-Path $log)) {
    Add-Check 'flow:ocr-reads-the-image' 'FAIL' 'the command wrote no log within 90s'
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

if ($lines['error']) {
    Add-Check 'flow:ocr-reads-the-image' 'FAIL' ("the command threw {0}" -f $lines['error']['type'])
    return $script:Checks
}

$real = $lines['real']
$ok = $real -and $real['available'] -eq 'True' -and $real['ok'] -eq 'True'
$detail = if ($real) { "recogniser available {0}, phrase read back {1}, first pass {2} ms" -f $real['available'], $real['ok'], $real['ms'] } else { 'no real line' }
Add-Check 'flow:ocr-reads-the-image' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

$latency = $lines['latency']
$ok = $latency -and [double]$latency['max'] -lt 150
$detail = if ($latency) { "{0} input-priority pings over a 1.2 s recognition run; longest wait {1} ms (bound 150)" -f $latency['pings'], $latency['max'] } else { 'no latency line' }
Add-Check 'flow:ocr-keeps-ui-responsive' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

return $script:Checks
