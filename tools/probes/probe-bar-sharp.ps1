# probe-bar-sharp.ps1 - pictures in the bar are decoded for the screen they are on
# (user report 2026-10-14: images in the bar look blurry).
#
# The stored thumbnail is only 240 pixels wide (the seed's is 320), while a card's image
# box is the bar's width: about 580 screen pixels at 150%. Blowing the thumbnail up is the
# blur. Now a card that comes on screen swaps in its original, decoded at the width the
# box needs (CardImageFit), as long as the original is still on disk.
#
# The DEBUG command "bar-sharp" first deletes the newest image's original, as if its
# retention ran out, then opens the bar and scrolls each image card into view in turn.
# For each one it logs the original size, whether it is still on disk, whether the sharp
# decode landed, the decoded pixel width, the width CardImageFit asks for, and the box's
# width in screen pixels. bar-sharp.png lands in the data dir.
#
# Checks:
#   - flow:bar-sharp-originals    every card whose original is on disk shows it, decoded
#                                 at exactly the asked width and wider than the thumbnail
#   - flow:bar-sharp-fills-screen that decode covers the box's screen pixels (or is the
#                                 whole picture, when the picture itself is smaller)
#   - flow:bar-sharp-expired      the card whose original is gone keeps the thumbnail,
#                                 decoded at its stored width rather than blown up first
#
# ASCII only (see lib.ps1 header).

param([string]$Exe = '')

. (Join-Path $PSScriptRoot 'lib.ps1')

if ($Exe -eq '') { $Exe = Get-DefaultProbeExe }
Reset-Checks

$dataDir = Join-Path $env:TEMP 'shiyu-probe-run\bar-sharp'
& (Join-Path $PSScriptRoot 'seed.ps1') -DataDir $dataDir -AppBin (Split-Path -Parent $Exe) | Out-Null
$log = Join-Path $dataDir 'bar-sharp.log'
Remove-Item $log -ErrorAction SilentlyContinue

$p = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd 'bar-sharp'
try {
    $deadline = (Get-Date).AddSeconds(40)
    while (-not (Test-Path $log) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
} finally {
    if ($p) { Stop-ProbeApp $p }
}

if (-not (Test-Path $log)) {
    Add-Check 'flow:bar-sharp-originals' 'FAIL' 'the command wrote no log within 40s'
    return $script:Checks
}

$cards = @()
foreach ($line in Get-Content $log) {
    if ($line -match '^image\|(.*)$') {
        $fields = @{}
        foreach ($pair in $Matches[1].Split('|')) {
            $kv = $pair.Split('=', 2)
            if ($kv.Count -eq 2) { $fields[$kv[0]] = $kv[1] }
        }
        $cards += , $fields
    }
}

function Describe($c) {
    "{0}: decoded {1} px (asked {2}, box {3} px, original on disk {4}, sharp {5})" -f `
        $c['original'], $c['decoded'], $c['expected'], $c['shown'], $c['hasOriginal'], $c['sharp']
}

$withOriginal = @($cards | Where-Object { $_['hasOriginal'] -eq 'True' })
$ok = $withOriginal.Count -gt 0
foreach ($c in $withOriginal) {
    if ($c['sharp'] -ne 'True' -or $c['decoded'] -ne $c['expected'] -or [int]$c['decoded'] -le 320) { $ok = $false }
}
Add-Check 'flow:bar-sharp-originals' $(if ($ok) { 'PASS' } else { 'FAIL' }) `
    ((@($withOriginal | ForEach-Object { Describe $_ }) -join '; ') + $(if ($withOriginal.Count -eq 0) { 'no image card with its original' } else { '' }))

$ok = $withOriginal.Count -gt 0
foreach ($c in $withOriginal) {
    $originalWidth = [int]($c['original'].Split('x')[0])
    if ([int]$c['shown'] -le 0) { $ok = $false; continue }
    if ([int]$c['decoded'] -lt [int]$c['shown'] -and [int]$c['decoded'] -ne $originalWidth) { $ok = $false }
}
Add-Check 'flow:bar-sharp-fills-screen' $(if ($ok) { 'PASS' } else { 'FAIL' }) `
    (@($withOriginal | ForEach-Object { "{0} px decoded for a {1} px box" -f $_['decoded'], $_['shown'] }) -join '; ')

$expired = @($cards | Where-Object { $_['expired'] -eq 'True' })
$ok = $expired.Count -eq 1 -and $expired[0]['hasOriginal'] -eq 'False' -and $expired[0]['sharp'] -eq 'False' -and $expired[0]['decoded'] -eq '320'
$detail = if ($expired.Count -eq 1) { Describe $expired[0] } else { "{0} expired card(s) in the log" -f $expired.Count }
Add-Check 'flow:bar-sharp-expired' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

return $script:Checks
