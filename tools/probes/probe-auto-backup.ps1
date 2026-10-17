# probe-auto-backup.ps1 - the weekly backup (review 2026-10-16 F-20) writes a sealed
# archive into the chosen folder, keeps only the newest few of its own files, and leaves
# everything else in that folder alone.
#
# The DEBUG command "auto-backup" builds a folder inside the probe's own data directory:
# five old automatic backups, one manually exported backup, one unrelated file. It sets a
# password and presses "back up now"; then it swaps the password for one this machine
# cannot open and presses again. Nothing outside the probe directory is touched.
#
# Checks:
#   - flow:auto-backup-written   a new automatic backup appears, sealed with the password
#   - flow:auto-backup-pruned    only 4 automatic backups remain; the manual export and the
#                                unrelated file are still there
#   - flow:auto-backup-locked    a password this machine cannot open is refused in plain
#                                words, and no file is written
#
# ASCII only (see lib.ps1 header).

param([string]$Exe = '')

. (Join-Path $PSScriptRoot 'lib.ps1')

if ($Exe -eq '') { $Exe = Get-DefaultProbeExe }
Reset-Checks

$dataDir = Join-Path $env:TEMP 'shiyu-probe-run\auto-backup'
& (Join-Path $PSScriptRoot 'seed.ps1') -DataDir $dataDir -AppBin (Split-Path -Parent $Exe) | Out-Null
$log = Join-Path $dataDir 'auto-backup.log'
Remove-Item $log -ErrorAction SilentlyContinue
$folder = Join-Path $dataDir 'auto-backup'
if (Test-Path $folder) { Remove-Item $folder -Recurse -Force }

$p = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd 'auto-backup'
try {
    $deadline = (Get-Date).AddSeconds(60)
    while (-not (Test-Path $log) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
} finally {
    if ($p) { Stop-ProbeApp $p }
}

if (-not (Test-Path $log)) {
    Add-Check 'flow:auto-backup-written' 'FAIL' 'the command wrote no log within 60s'
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
    Add-Check 'flow:auto-backup-written' 'FAIL' ("the command threw {0}" -f $lines['error']['type'])
    return $script:Checks
}

$run = $lines['run']
$ok = $run -and $run['ok'] -eq 'True' -and $run['fresh'] -eq 'True' -and $run['sealed'] -eq 'True'
$detail = if ($run) { "reported ok {0}, newest is fresh {1}, sealed {2}" -f $run['ok'], $run['fresh'], $run['sealed'] } else { 'no run line' }
Add-Check 'flow:auto-backup-written' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

$ok = $run -and $run['ours'] -eq '4' -and $run['manual'] -eq 'True' -and $run['other'] -eq 'True'
$detail = if ($run) { "{0} automatic backup(s) left, manual export kept {1}, unrelated file kept {2}" -f $run['ours'], $run['manual'], $run['other'] } else { 'no run line' }
Add-Check 'flow:auto-backup-pruned' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

$locked = $lines['locked']
$ok = $locked -and $locked['said'] -eq 'True' -and $locked['unchanged'] -eq 'True'
$detail = if ($locked) { "names the password {0}, folder unchanged {1}" -f $locked['said'], $locked['unchanged'] } else { 'no locked line' }
Add-Check 'flow:auto-backup-locked' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

# The sealed archive round-trips: importing it into a fresh store with the password brings
# the entries back (acceptance 2026-10-17, delegated by the user).
$import = $lines['import']
$ok = $import -and [int]$import['added'] -gt 0 -and [int]$import['entriesInStore'] -eq [int]$import['added']
$detail = if ($import) { "sealed archive imported into a fresh store: {0} entr(ies), {1} image(s), store holds {2}" -f $import['added'], $import['images'], $import['entriesInStore'] } else { 'no import line' }
Add-Check 'flow:auto-backup-import' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

return $script:Checks
