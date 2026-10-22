# probe-retention.ps1 - retention boundary cleanup end-to-end (research report P0-1).
#
# Launch 1 seeds data across the retention boundary: entries 31 days old (a plain
# one, a favorite, a pinned one), one 3 days old, two 31-day-old images with
# originals (one plain, one favorite), one 31-day-old translation row - then sets
# all retention periods to 7 days with deletion protection on.
# Launch 2's startup sweep (RetentionModule.Start) must then:
#   - delete the plain old entry and the unprotected old image's ORIGINAL FILE
#   - keep the favorite, the pinned and the fresh entries, keep the favorite
#     image's original file
#   - sweep the backdated translation row
#
# Checks:
#   - flow:retention-history     plain old gone, fresh kept
#   - flow:retention-favorites   favorite/pinned entries and the favorite image kept
#   - flow:retention-images      the unprotected old image's original file deleted
#   - flow:retention-translation the backdated translation row swept
#
# ASCII only (see lib.ps1 header).

param([string]$Exe = '')

. (Join-Path $PSScriptRoot 'lib.ps1')

if ($Exe -eq '') { $Exe = Get-DefaultProbeExe }
Reset-Checks

$dataDir = Join-Path $env:TEMP 'shiyu-probe-run\retention'
& (Join-Path $PSScriptRoot 'seed.ps1') -DataDir $dataDir -AppBin (Split-Path -Parent $Exe) | Out-Null
$seedLog = Join-Path $dataDir 'retention-seed.log'
$checkLog = Join-Path $dataDir 'retention-check.log'
Remove-Item $seedLog, $checkLog -ErrorAction SilentlyContinue

# Launch 1: seed the boundary data and set the retention periods.
$p1 = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd 'retention-seed'
try {
    $deadline = (Get-Date).AddSeconds(60)
    while (-not (Test-Path $seedLog) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
} finally {
    if ($p) { Stop-ProbeApp $p }
    if ($p1) { Stop-ProbeApp $p1 }
}

Start-Sleep -Seconds 1

# Launch 2: the startup sweep decides over the seeded data.
$p2 = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd 'retention-check'
try {
    $deadline = (Get-Date).AddSeconds(60)
    while (-not (Test-Path $checkLog) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
} finally {
    if ($p2) { Stop-ProbeApp $p2 }
}

if (-not (Test-Path $checkLog)) {
    Add-Check 'flow:retention-history' 'FAIL' 'the command wrote no check log within 60s'
    Add-Check 'flow:retention-favorites' 'FAIL' 'no check log'
    Add-Check 'flow:retention-images' 'FAIL' 'no check log'
    Add-Check 'flow:retention-translation' 'FAIL' 'no check log'
    return $script:Checks
}

$check = @{}
foreach ($line in Get-Content $checkLog -Encoding UTF8) {
    if ($line -match '^check\|(.*)$') {
        foreach ($pair in $Matches[1].Split('|')) {
            $kv = $pair.Split('=', 2)
            if ($kv.Count -eq 2) { $check[$kv[0]] = $kv[1] }
        }
    }
}

function CheckValue([string]$Key) {
    if ($check.ContainsKey($Key)) { return $check[$Key] }
    return '?'
}

$ok = (CheckValue 'oldPlainGone') -eq 'True' -and (CheckValue 'freshKept') -eq 'True'
Add-Check 'flow:retention-history' $(if ($ok) { 'PASS' } else { 'FAIL' }) "old entry gone: $(CheckValue 'oldPlainGone'), fresh entry kept: $(CheckValue 'freshKept')"

$ok = (CheckValue 'oldFavKept') -eq 'True' -and (CheckValue 'oldPinKept') -eq 'True' -and (CheckValue 'imageFavKept') -eq 'True' -and (CheckValue 'imageFavFileKept') -eq 'True'
Add-Check 'flow:retention-favorites' $(if ($ok) { 'PASS' } else { 'FAIL' }) "protected entries and the favorite image's file kept: $(CheckValue 'oldFavKept')/$(CheckValue 'oldPinKept')/$(CheckValue 'imageFavKept')/$(CheckValue 'imageFavFileKept')"

$ok = (CheckValue 'imageFileGone') -eq 'True'
Add-Check 'flow:retention-images' $(if ($ok) { 'PASS' } else { 'FAIL' }) "unprotected old image's original file deleted: $(CheckValue 'imageFileGone')"

$ok = (CheckValue 'tlogRows') -eq '0'
Add-Check 'flow:retention-translation' $(if ($ok) { 'PASS' } else { 'FAIL' }) "backdated translation row swept: rows=$(CheckValue 'tlogRows')"

return $script:Checks
