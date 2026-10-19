# probe-folder-open-locate.ps1 - reproduction for a user report (2026-10-19): with a copied
# FOLDER, the bar's Open and Locate actions do nothing. No clipboard, no Explorer launch -
# the DEBUG command "folder-open-locate" creates a files entry pointing at a temp DIRECTORY,
# opens the quick paste, and re-runs the exact target resolution Open and Locate use.
#
# Checks:
#   - flow:folder-open-locate-reproduced   the resolution yields no target for a directory
#                                          (File.Exists is false for folders), while the
#                                          directory itself is alive
#
# ASCII only (see lib.ps1 header).

param([string]$Exe = '')

. (Join-Path $PSScriptRoot 'lib.ps1')

if ($Exe -eq '') { $Exe = Get-DefaultProbeExe }
Reset-Checks

$dataDir = Join-Path $env:TEMP 'shiyu-probe-run\folder-open-locate'
& (Join-Path $PSScriptRoot 'seed.ps1') -DataDir $dataDir -AppBin (Split-Path -Parent $Exe) | Out-Null
$log = Join-Path $dataDir 'folder-open-locate.log'
Remove-Item $log -ErrorAction SilentlyContinue

$p = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd 'folder-open-locate'
try {
    $deadline = (Get-Date).AddSeconds(60)
    while (-not (Test-Path $log) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
} finally {
    if ($p) { Stop-ProbeApp $p }
}

if (-not (Test-Path $log)) {
    Add-Check 'flow:folder-open-locate-reproduced' 'FAIL' 'the command wrote no log within 60s'
    return $script:Checks
}

$repro = $null
foreach ($line in Get-Content $log -Encoding UTF8) {
    if ($line -match '^repro\|(.*)$') {
        $repro = @{}
        foreach ($pair in $Matches[1].Split('|')) {
            $kv = $pair.Split('=', 2)
            if ($kv.Count -eq 2) { $repro[$kv[0]] = $kv[1] }
        }
    }
}

$ok = $repro -and $repro['kind'] -eq 'Files' -and $repro['resolved'] -eq 'False' -and $repro['directoryAlive'] -eq 'True'
$detail = if ($repro) { "kind {0}: open/locate resolve to nothing {1}, while the directory is alive {2} - {3}" -f $repro['kind'], $repro['resolved'], $repro['directoryAlive'], $(if ($ok) { 'REPRODUCED' } else { 'NOT reproduced' }) } else { 'no repro line' }
Add-Check 'flow:folder-open-locate-reproduced' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

return $script:Checks
