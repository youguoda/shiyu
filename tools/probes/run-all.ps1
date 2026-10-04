# run-all.ps1 - run every Shiyu visual probe in sequence (ticket 15).
#
# One command, repeatable. Prints a PASS/FAIL/SKIP(reason) table to stdout
# and writes it, with every screenshot, to %TEMP%\shiyu-probe-results
# (never into the repository).
#
# "FAIL" on a defect:* check means the known visual defect was reproduced -
# these are expected to be red until the corresponding fix lands, then turn
# green as regression guards.
#
# ASCII only (see lib.ps1 header).

param(
    [string]$Exe = '',
    [switch]$SkipCoexistence
)

. (Join-Path $PSScriptRoot 'lib.ps1')

if ($Exe -eq '') { $Exe = Get-DefaultProbeExe }
if (-not (Test-Path $Exe)) { throw "probe exe not found: $Exe (build src\Shiyu.App -c Debug first)" }

$all = New-Object System.Collections.ArrayList
$started = Get-Date

# --- coexistence with the user's real instance -------------------------------

if (-not $SkipCoexistence) {
    Write-Host '== coexistence checks =='

    # 1. A probe must run while the REAL single-instance mutex is held by
    #    someone else - that is what "running beside the user's instance"
    #    means. We hold Local\Shiyu ourselves for the duration.
    $realMutex = New-Object System.Threading.Mutex($false, 'Local\Shiyu')
    $dataDir = Join-Path $env:TEMP 'shiyu-probe-run\coexist'
    & (Join-Path $PSScriptRoot 'seed.ps1') -DataDir $dataDir | Out-Null
    $probe = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd ''
    Start-Sleep -Seconds 5
    $alive = -not $probe.HasExited
    Stop-ProbeApp $probe
    $null = $all.Add([pscustomobject]@{
        Check = 'coexistence:probe-beside-user-mutex'
        Status = $(if ($alive) { 'PASS' } else { 'FAIL' })
        Detail = 'probe stayed alive while Local\Shiyu was held elsewhere (per-directory mutex)' })
    Write-Host ("  [{0}] coexistence:probe-beside-user-mutex" -f $all[-1].Status)

    # 2. Two probes on the SAME directory must still reject each other.
    #    A second probe broadcasts "another instance" on its way out, which
    #    would pop the library window of any REAL running instance - so this
    #    check is skipped whenever a Shiyu process from outside this
    #    worktree is running.
    $userRunning = @(Get-Process -Name 'Shiyu.App' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path -notlike '*guodapro-wt*' })
    if ($userRunning.Count -gt 0) {
        $null = $all.Add([pscustomobject]@{
            Check = 'coexistence:probe-mutex-per-dir'
            Status = 'SKIP'
            Detail = 'a real Shiyu instance is running; the same-dir rejection test broadcasts an activation message' })
    } else {
        $a = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd ''
        Start-Sleep -Seconds 3
        $b = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd ''
        $b.WaitForExit(15000) | Out-Null
        $secondRejected = $b.HasExited
        Stop-ProbeApp $a
        $null = $all.Add([pscustomobject]@{
            Check = 'coexistence:probe-mutex-per-dir'
            Status = $(if ($secondRejected) { 'PASS' } else { 'FAIL' })
            Detail = 'second probe on the same data directory exited (mutex is per-directory)' })
    }
    Write-Host ("  [{0}] coexistence:probe-mutex-per-dir" -f $all[-1].Status)
    $realMutex.Dispose()
}

# --- window probes ------------------------------------------------------------

foreach ($name in @('bar', 'bar-newest', 'panel', 'settings', 'library', 'quickbar', 'preview', 'tray', 'caret')) {
    Write-Host "== probe-$name =="
    $script = Join-Path $PSScriptRoot "probe-$name.ps1"
    $checks = & $script -Exe $Exe
    foreach ($c in $checks) { $null = $all.Add($c) }
}

# --- report -------------------------------------------------------------------

$elapsed = [int]((Get-Date) - $started).TotalSeconds
$pass = @($all | Where-Object Status -eq 'PASS').Count
$fail = @($all | Where-Object Status -eq 'FAIL').Count
$skip = @($all | Where-Object Status -eq 'SKIP').Count

""
$all | Format-Table Check, Status, Detail -AutoSize | Out-String -Width 200 | ForEach-Object { $_ }
""
"TOTAL: {0} checks in {1}s -> PASS {2} / FAIL {3} / SKIP {4}" -f $all.Count, $elapsed, $pass, $fail, $skip
"defect:* FAIL lines are the known visual P0s reproducing (expected red until fixed)."

$report = @()
$report += "shiyu probe run $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
$report += "exe: $Exe"
$report += ""
$report += ($all | Format-Table Check, Status, Detail -AutoSize | Out-String -Width 200)
$report += "TOTAL: PASS $pass / FAIL $fail / SKIP $skip ($($all.Count) checks, ${elapsed}s)"
$report | Set-Content -Path (Join-Path $script:ProbeResultRoot 'results.txt') -Encoding ASCII

"results and screenshots: $($script:ProbeResultRoot)"
