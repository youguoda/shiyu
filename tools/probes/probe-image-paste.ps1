# probe-image-paste.ps1 - reproduction for a user report (2026-10-19): pressing Enter on an
# IMAGE card in the quick-paste bar pastes the label text ("图片 W×H") instead of the image.
# NOT part of run-all: it writes the REAL clipboard (saves/restores clipboard text; images
# are not saved).
#
# The DEBUG command "image-paste" creates a real image entry (thumbnail + original + size)
# in the probe's own data dir, opens the quick paste, selects the image card, and runs the
# paste path - then reads the clipboard back.
#
# Checks:
#   - flow:image-paste-pastes-the-image   the clipboard holds the label TEXT and no image - the
#                                   reported defect, reproduced
#
# ASCII only (see lib.ps1 header).

param([string]$Exe = '')

. (Join-Path $PSScriptRoot 'lib.ps1')

if ($Exe -eq '') { $Exe = Get-DefaultProbeExe }
Reset-Checks

$saved = $null
try { $saved = Get-Clipboard -Format Text -TextFormatType UnicodeText -ErrorAction SilentlyContinue } catch { }

$dataDir = Join-Path $env:TEMP 'shiyu-probe-run\image-paste'
& (Join-Path $PSScriptRoot 'seed.ps1') -DataDir $dataDir -AppBin (Split-Path -Parent $Exe) | Out-Null
$log = Join-Path $dataDir 'image-paste.log'
Remove-Item $log -ErrorAction SilentlyContinue

$p = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd 'image-paste'
try {
    $deadline = (Get-Date).AddSeconds(60)
    while (-not (Test-Path $log) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
} finally {
    if ($p) { Stop-ProbeApp $p }
    if ($null -ne $saved) { Set-Clipboard -Value $saved }
}

if (-not (Test-Path $log)) {
    Add-Check 'flow:image-paste-pastes-the-image' 'FAIL' 'the command wrote no log within 60s'
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

$ok = $repro -and $repro['kind'] -eq 'Image' -and $repro['containsImage'] -eq 'True' -and $repro['label'] -eq 'False'
$detail = if ($repro) { "kind {0}: clipboard holds image {1}, label text {2} - {3}" -f $repro['kind'], $repro['containsImage'], $repro['label'], $(if ($ok) { 'REPRODUCED' } else { 'NOT reproduced' }) } else { 'no repro line' }
Add-Check 'flow:image-paste-pastes-the-image' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

return $script:Checks
