# probe-preview-pin.ps1 - the pin on the hover preview (user request 2026-10-19): pinned,
# the preview stays on screen through its auto-dismiss paths and shows only the clean
# content (the meta line and the teaching row collapse); Esc-like takedowns are refused,
# and unpinning takes it down.
#
# The DEBUG command "preview-pin" opens the quick paste on seeded data, shows the preview
# for the first card through the real hover path, pins it, and checks every step. Nothing
# touches the clipboard.
#
# Checks:
#   - flow:preview-pin   pinned: clean mode holds, a takedown is refused, unpin closes
#
# ASCII only (see lib.ps1 header).

param([string]$Exe = '')

. (Join-Path $PSScriptRoot 'lib.ps1')

if ($Exe -eq '') { $Exe = Get-DefaultProbeExe }
Reset-Checks

$dataDir = Join-Path $env:TEMP 'shiyu-probe-run\preview-pin'
& (Join-Path $PSScriptRoot 'seed.ps1') -DataDir $dataDir -AppBin (Split-Path -Parent $Exe) | Out-Null
$log = Join-Path $dataDir 'preview-pin.log'
Remove-Item $log -ErrorAction SilentlyContinue

$p = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd 'preview-pin'
try {
    $deadline = (Get-Date).AddSeconds(60)
    while (-not (Test-Path $log) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
} finally {
    if ($p) { Stop-ProbeApp $p }
}

if (-not (Test-Path $log)) {
    Add-Check 'flow:preview-pin' 'FAIL' 'the command wrote no log within 60s'
    return $script:Checks
}

$preview = $null
foreach ($line in Get-Content $log -Encoding UTF8) {
    if ($line -match '^preview\|(.*)$') {
        $preview = @{}
        foreach ($pair in $Matches[1].Split('|')) {
            $kv = $pair.Split('=', 2)
            if ($kv.Count -eq 2) { $preview[$kv[0]] = $kv[1] }
        }
    }
}

$ok = $preview -and $preview['pinned'] -eq 'True' -and $preview['clean'] -eq 'True' `
    -and $preview['surviveTakeDown'] -eq 'True' -and $preview['closedAfterUnpin'] -eq 'True'
$detail = if ($preview) { "pinned {0}, clean mode {1}, takedown refused {2}, unpin closes {3}" -f $preview['pinned'], $preview['clean'], $preview['surviveTakeDown'], $preview['closedAfterUnpin'] } else { 'no preview line' }
Add-Check 'flow:preview-pin' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

return $script:Checks
