# probe-bigdata.ps1 - large-history stress curve (research report P1 §3).
#
# The DEBUG command "bigdata" seeds the store up to SHIYU_PROBE_COUNT entries
# (default 20000; the data directory persists across runs, so repeat runs skip
# the seeding), then measures three things on the big store:
#   - quick-paste summon latency, 3 runs
#   - search latency on four terms (the same path the library window uses)
#   - a 7-day retention sweep over the backdated spread, timed
#
# Budgets (calibrated on the reference machine; revisit after hardware changes):
#   - summon max <= 2000 ms   (the list is virtualized: size must not matter)
#   - search max <= 500 ms    (single query, limit 20)
#   - retention sweep <= 10 s over ~17k deletions
#
# Not part of run-all: the seeding dominates the runtime (minutes on the first
# run of a fresh data directory).
#
# ASCII only (see lib.ps1 header).

param([string]$Exe = '', [int]$Count = 20000)

. (Join-Path $PSScriptRoot 'lib.ps1')

if ($Exe -eq '') { $Exe = Get-DefaultProbeExe }
Reset-Checks

$dataDir = Join-Path $env:TEMP 'shiyu-probe-run\bigdata'
& (Join-Path $PSScriptRoot 'seed.ps1') -DataDir $dataDir -AppBin (Split-Path -Parent $Exe) | Out-Null
$log = Join-Path $dataDir 'bigdata.log'
Remove-Item $log -ErrorAction SilentlyContinue

$env:SHIYU_PROBE_COUNT = "$Count"
$p = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd 'bigdata'
try {
    $deadline = (Get-Date).AddSeconds(600)
    while ((Get-Date) -lt $deadline) {
        if ((Test-Path $log) -and (Select-String -Path $log -Pattern '^done' -ErrorAction SilentlyContinue)) { break }
        Start-Sleep -Milliseconds 500
    }
} finally {
    if ($p) { Stop-ProbeApp $p }
}
Remove-Item Env:SHIYU_PROBE_COUNT -ErrorAction SilentlyContinue

if (-not (Test-Path $log)) {
    Add-Check 'flow:bigdata-completes' 'FAIL' 'the command wrote no log'
    Add-Check 'perf:bigdata-summon' 'FAIL' 'no log'
    Add-Check 'perf:bigdata-search' 'FAIL' 'no log'
    Add-Check 'perf:bigdata-retention' 'FAIL' 'no log'
    return $script:Checks
}

$summons = @()
$searches = @()
$retentionMs = -1
$retentionCount = -1
$done = $false
foreach ($line in Get-Content $log -Encoding UTF8) {
    if ($line -match '^summon\|run=\d+\|ms=([\d.]+)') { $summons += [double]$Matches[1] }
    if ($line -match '^search\|hits=\d+\|ms=([\d.]+)') { $searches += [double]$Matches[1] }
    if ($line -match '^retention\|ms=([\d.]+)\|count=(\d+)') { $retentionMs = [double]$Matches[1]; $retentionCount = [int]$Matches[2] }
    if ($line -eq 'done') { $done = $true }
}

$ok = $done -and ($summons.Count -ge 3)
$detail = if ($ok) { "seeded and measured on $((Get-Content $log | Select-String 'seed\|count=' | Select-Object -Last 1) -replace 'seed\|count=','count=')" } else { "incomplete run: done=$done, $($summons.Count) summons" }
Add-Check 'flow:bigdata-completes' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

$maxSummon = if ($summons.Count -gt 0) { ($summons | Measure-Object -Maximum).Maximum } else { -1 }
$ok = ($maxSummon -ge 0) -and ($maxSummon -le 2000)
Add-Check 'perf:bigdata-summon' $(if ($ok) { 'PASS' } else { 'FAIL' }) "summon max over 3 runs: $maxSummon ms (budget 2000)"

$maxSearch = if ($searches.Count -gt 0) { ($searches | Measure-Object -Maximum).Maximum } else { -1 }
$ok = ($maxSearch -ge 0) -and ($maxSearch -le 500)
Add-Check 'perf:bigdata-search' $(if ($ok) { 'PASS' } else { 'FAIL' }) "search max over 4 terms: $maxSearch ms (budget 500)"

$ok = ($retentionMs -ge 0) -and ($retentionMs -le 10000)
Add-Check 'perf:bigdata-retention' $(if ($ok) { 'PASS' } else { 'FAIL' }) "7-day sweep over the big store: $retentionMs ms (budget 10000), count after $retentionCount"

return $script:Checks
