# probe-settings-more.ps1 - low-frequency settings fold into a "more" section at the end of
# their section (review 2026-10-16 F-15): the first screen holds the common ones, nothing
# is deleted, and search or a deep link still lands on a folded one.
#
# The DEBUG command "settings-more" opens Settings on the quick-paste page, checks that
# "pinned" is on screen while "text lines" is folded away, then deep-links to "text lines"
# and checks that its fold opened. settings-more-closed.png and settings-more-open.png
# land in the data dir.
#
# Checks:
#   - flow:settings-more-folded    the common item shows; the low-frequency one is folded
#   - flow:settings-more-deeplink  a deep link to a folded item opens its fold
#
# ASCII only (see lib.ps1 header).

param([string]$Exe = '')

. (Join-Path $PSScriptRoot 'lib.ps1')

if ($Exe -eq '') { $Exe = Get-DefaultProbeExe }
Reset-Checks

$dataDir = Join-Path $env:TEMP 'shiyu-probe-run\settings-more'
& (Join-Path $PSScriptRoot 'seed.ps1') -DataDir $dataDir -AppBin (Split-Path -Parent $Exe) | Out-Null
$log = Join-Path $dataDir 'settings-more.log'
Remove-Item $log -ErrorAction SilentlyContinue

$p = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd 'settings-more'
try {
    $deadline = (Get-Date).AddSeconds(60)
    while (-not (Test-Path $log) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
} finally {
    if ($p) { Stop-ProbeApp $p }
}

if (-not (Test-Path $log)) {
    Add-Check 'flow:settings-more-folded' 'FAIL' 'the command wrote no log within 60s'
    return $script:Checks
}

$more = $null
foreach ($line in Get-Content $log -Encoding UTF8) {
    if ($line -match '^more\|(.*)$') {
        $more = @{}
        foreach ($pair in $Matches[1].Split('|')) {
            $kv = $pair.Split('=', 2)
            if ($kv.Count -eq 2) { $more[$kv[0]] = $kv[1] }
        }
    }
}

$ok = $more -and $more['common'] -eq 'True' -and $more['folded'] -eq 'True'
$detail = if ($more) { "common item on screen {0}, low-frequency item folded {1}" -f $more['common'], $more['folded'] } else { 'no more line' }
Add-Check 'flow:settings-more-folded' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

$ok = $more -and $more['unfolded'] -eq 'True'
$detail = if ($more) { "after the deep link the folded item is visible {0}" -f $more['unfolded'] } else { 'no more line' }
Add-Check 'flow:settings-more-deeplink' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

return $script:Checks
