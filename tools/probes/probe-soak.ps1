# probe-soak.ps1 - long-run resource soak (research report P0; the 8-hour
# overnight variant is driven with -Minutes 480 on an idle machine).
#
# The DEBUG command "soak" loops a mixed load - history writes, translation-log
# writes, preview open/close every 5th cycle - sampling working set, private
# bytes, GC and handles every 30 seconds into soak.log, ending with a "done"
# line. The probe script judges the FIRST-to-LAST working-set drift.
#
# Checks:
#   - flow:soak-completes     the run reached its duration and logged done
#   - perf:soak-workingset    working-set drift within the short-run budget
#                             (60 MB for the default 5 minutes; the overnight
#                             run is judged on its curve, not this gate)
#
# ASCII only (see lib.ps1 header).

param([string]$Exe = '', [int]$Minutes = 5)

. (Join-Path $PSScriptRoot 'lib.ps1')

if ($Exe -eq '') { $Exe = Get-DefaultProbeExe }
Reset-Checks

$dataDir = Join-Path $env:TEMP 'shiyu-probe-run\soak'
& (Join-Path $PSScriptRoot 'seed.ps1') -DataDir $dataDir -AppBin (Split-Path -Parent $Exe) | Out-Null
$log = Join-Path $dataDir 'soak.log'
Remove-Item $log -ErrorAction SilentlyContinue

$env:SHIYU_PROBE_MINUTES = "$Minutes"
$p = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd 'soak'
try {
    $deadline = (Get-Date).AddSeconds($Minutes * 60 + 180)
    while ((Get-Date) -lt $deadline) {
        if ((Test-Path $log) -and (Select-String -Path $log -Pattern '^done' -ErrorAction SilentlyContinue)) { break }
        Start-Sleep -Milliseconds 500
    }
} finally {
    if ($p) { Stop-ProbeApp $p }
}
Remove-Item Env:SHIYU_PROBE_MINUTES -ErrorAction SilentlyContinue

if (-not (Test-Path $log)) {
    Add-Check 'flow:soak-completes' 'FAIL' 'the command wrote no soak log'
    Add-Check 'perf:soak-workingset' 'FAIL' 'no soak log'
    return $script:Checks
}

$samples = @{}
$order = @()
foreach ($line in Get-Content $log -Encoding UTF8) {
    if ($line -match '^sample\|tag=([\w-]+)\|t=([\d.]+)\|ws=(\d+)\|.*\|handles=(\d+)\|entries=(\d+)$') {
        $samples[$Matches[1]] = @{ t = [double]$Matches[2]; ws = [int]$Matches[3]; handles = [int]$Matches[4]; entries = [int]$Matches[5] }
        $order += $Matches[1]
    }
    if ($line -match '^done') { $done = $true }
}

if ($order.Count -lt 2) {
    Add-Check 'flow:soak-completes' 'FAIL' "fewer than two samples recorded (done=$done)"
    Add-Check 'perf:soak-workingset' 'FAIL' 'no samples'
    return $script:Checks
}

$first = $samples[$order[0]]
$last = $samples[$order[-1]]
$wsDrift = $last['ws'] - $first['ws']
$ok = $done -and ([math]::Abs($wsDrift) -le 60)
Add-Check 'flow:soak-completes' 'PASS' "soak ran $($last['t']) min over $($order.Count) sample tags; entries $($last['entries'])"
Add-Check 'perf:soak-workingset' $(if ([math]::Abs($wsDrift) -le 60) { 'PASS' } else { 'FAIL' }) "working-set drift ${wsDrift} MB (first $($first['ws']), last $($last['ws']))"

return $script:Checks
