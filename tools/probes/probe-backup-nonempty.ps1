# probe-backup-nonempty.ps1 - restoring a backup into a NON-empty library
# (research report P0-2): what happens to the entries that came after the backup,
# and does the archive round-trip with its image originals intact.
#
# The DEBUG command "backup-nonempty" exports the current (seeded + one image)
# library, adds three "post-backup" entries, then imports the archive twice:
# once with overwrite=false (merge) and once with overwrite=true (replace).
# Every step is logged.
#
# Checks:
#   - flow:backup-merge-keeps-existing  the merge import keeps the post-backup entries
#   - flow:backup-replace-integrity     the replace import lands the archive exactly,
#                                       post entries gone, image original back
#
# ASCII only (see lib.ps1 header).

param([string]$Exe = '')

. (Join-Path $PSScriptRoot 'lib.ps1')

if ($Exe -eq '') { $Exe = Get-DefaultProbeExe }
Reset-Checks

$dataDir = Join-Path $env:TEMP 'shiyu-probe-run\backup-nonempty'
& (Join-Path $PSScriptRoot 'seed.ps1') -DataDir $dataDir -AppBin (Split-Path -Parent $Exe) | Out-Null
$log = Join-Path $dataDir 'backup-nonempty.log'
Remove-Item $log -ErrorAction SilentlyContinue

$p = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd 'backup-nonempty'
try {
    $deadline = (Get-Date).AddSeconds(60)
    while (-not (Test-Path $log) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
} finally {
    if ($p) { Stop-ProbeApp $p }
}

if (-not (Test-Path $log)) {
    Add-Check 'flow:backup-merge-keeps-existing' 'FAIL' 'the command wrote no log within 60s'
    Add-Check 'flow:backup-replace-integrity' 'FAIL' 'the command wrote no log within 60s'
    return $script:Checks
}

$lines = @{}
foreach ($line in Get-Content $log -Encoding UTF8) {
    if ($line -match '^(merge|replace)\|(.*)$') {
        $fields = @{}
        foreach ($pair in $Matches[2].Split('|')) {
            $kv = $pair.Split('=', 2)
            if ($kv.Count -eq 2) { $fields[$kv[0]] = $kv[1] }
        }
        $lines[$Matches[1]] = $fields
    }
    if ($line -match '^error\|') {
        Add-Check 'flow:backup-merge-keeps-existing' 'FAIL' "the command threw: $line"
        Add-Check 'flow:backup-replace-integrity' 'FAIL' "the command threw: $line"
        return $script:Checks
    }
}

function Get-Line([string]$Section, [string]$Key) {
    if ($null -ne $Section -and $lines.ContainsKey($Section) -and $lines[$Section].ContainsKey($Key)) {
        return [int]$lines[$Section][$Key]
    }
    return -999999
}

function FieldIs([string]$Section, [string]$Key, [string]$Expected) {
    $lines.ContainsKey($Section) -and $lines[$Section].ContainsKey($Key) -and ($lines[$Section][$Key] -eq $Expected)
}

$mergeAdded = Get-Line 'merge' 'added'
$mergeSkipped = Get-Line 'merge' 'skipped'
$postKept = FieldIs 'merge' 'postKept' 'True'
$ok = $postKept -and ($mergeAdded -ge 0) -and ($mergeSkipped -ge 0)
$detail = if ($postKept) { "merge import kept the post-backup entries: added $mergeAdded, skipped existing $mergeSkipped" } else { 'no merge line' }
Add-Check 'flow:backup-merge-keeps-existing' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

$replaceAdded = Get-Line 'replace' 'added'
$postGone = FieldIs 'replace' 'postGone' 'True'
$imageBack = FieldIs 'replace' 'imageBack' 'True'
$count = Get-Line 'replace' 'count'
$ok = $postGone -and $imageBack -and ($count -eq $replaceAdded) -and ($count -ge 18)
$detail = if ($postGone) { "replace import: archive entries $replaceAdded landed, post entries gone, image original back, store holds $count" } else { "replace import did not clear the post entries (postGone=$postGone, imageBack=$imageBack, count=$count)" }
Add-Check 'flow:backup-replace-integrity' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

return $script:Checks

return $script:Checks
