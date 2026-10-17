# probe-degraded-shell.ps1 - the Win10 degradation look (review F-13; a user found on a
# Windows 10 VM on 2026-10-17 that the three overlay shells showed the desktop through
# them). The DEBUG command "degraded-shell" forces DegradeShell on the quick-paste bar -
# the exact path a Windows 10 machine takes when DWM refuses the material - and renders
# a PNG for the eye.
#
# Checks:
#   - flow:degraded-shell-opaque   the shell carries the 12 DIP margin, the 1 DIP stroke,
#                                  and an opaque (alpha 255) fill
#
# ASCII only (see lib.ps1 header).

param([string]$Exe = '')

. (Join-Path $PSScriptRoot 'lib.ps1')

if ($Exe -eq '') { $Exe = Get-DefaultProbeExe }
Reset-Checks

$dataDir = Join-Path $env:TEMP 'shiyu-probe-run\degraded-shell'
& (Join-Path $PSScriptRoot 'seed.ps1') -DataDir $dataDir -AppBin (Split-Path -Parent $Exe) | Out-Null
$log = Join-Path $dataDir 'degraded-shell.log'
Remove-Item $log -ErrorAction SilentlyContinue

$p = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd 'degraded-shell'
try {
    $deadline = (Get-Date).AddSeconds(60)
    while (-not (Test-Path $log) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
} finally {
    if ($p) { Stop-ProbeApp $p }
}

if (-not (Test-Path $log)) {
    Add-Check 'flow:degraded-shell-opaque' 'FAIL' 'the command wrote no log within 60s'
    return $script:Checks
}

$degraded = $null
foreach ($line in Get-Content $log -Encoding UTF8) {
    if ($line -match '^degraded\|(.*)$') {
        $degraded = @{}
        foreach ($pair in $Matches[1].Split('|')) {
            $kv = $pair.Split('=', 2)
            if ($kv.Count -eq 2) { $degraded[$kv[0]] = $kv[1] }
        }
    }
}

$ok = $degraded -and $degraded['margin'] -eq 'True' -and $degraded['stroke'] -eq 'True' -and $degraded['opaque'] -eq 'True'
$detail = if ($degraded) { "margin {0}, stroke {1}, opaque fill {2}" -f $degraded['margin'], $degraded['stroke'], $degraded['opaque'] } else { 'no degraded line' }
Add-Check 'flow:degraded-shell-opaque' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

return $script:Checks
