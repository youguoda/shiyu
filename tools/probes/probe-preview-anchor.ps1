# probe-preview-anchor.ps1 - the wheel zoom's cursor anchor drifting after a
# pan (user report 2026-10-20 family: the zoom-to-cursor math mixes image-local
# and parent-space coordinates, so the point under the cursor slides away).
#
# The DEBUG command "preview-anchor" pins the seeded 800x500 image, waits for
# the original, zooms 4 ticks at the image centre, pans (40, 20), then zooms 4
# more ticks with the cursor parked on the SAME screen pixel. The image point
# under that pixel is read before and after: drift = |u1 - u2|.
#
# This probe moves the REAL cursor onto the probe's own preview window, so it
# stands down (SKIP) whenever someone is at the machine.
#
# Checks:
#   flow:anchor-zoom-reaches-target  4 wheel ticks land on 1.25^4
#   flow:anchor-pan-moves-origin     the pan moved the origin, scale untouched
#   defect:preview-anchor-drift      FAIL = the point under the cursor drifted
#
# ASCII only (see lib.ps1 header).

param([string]$Exe = '')

. (Join-Path $PSScriptRoot 'lib.ps1')

if ($Exe -eq '') { $Exe = Get-DefaultProbeExe }
Reset-Checks

if (Test-UserPresent) {
    Add-Check 'flow:anchor-zoom-reaches-target' 'SKIP' 'user present - cursor-stealing probe stood down'
    Add-Check 'flow:anchor-pan-moves-origin' 'SKIP' 'user present - cursor-stealing probe stood down'
    Add-Check 'defect:preview-anchor-drift' 'SKIP' 'user present - cursor-stealing probe stood down'
    return $script:Checks
}

$dataDir = Join-Path $env:TEMP 'shiyu-probe-run\preview-anchor'
& (Join-Path $PSScriptRoot 'seed.ps1') -DataDir $dataDir -AppBin (Split-Path -Parent $Exe) | Out-Null
$log = Join-Path $dataDir 'preview-anchor.log'
Remove-Item $log -ErrorAction SilentlyContinue

$p = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd 'preview-anchor' -ExtraEnv @{ SHIYU_FAKE_BACKEND = '1' }
try {
    $deadline = (Get-Date).AddSeconds(90)
    while (-not (Test-Path $log) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
    while ((Get-Date) -lt $deadline -and (Test-Path $log) -and (@(Select-String -Path $log -Pattern '^done').Count -eq 0)) {
        Start-Sleep -Milliseconds 500
    }
} finally {
    if ($p) { Stop-ProbeApp $p }
}

if (-not (Test-Path $log)) {
    Add-Check 'flow:anchor-zoom-reaches-target' 'FAIL' 'the command wrote no log within 90s'
    Add-Check 'flow:anchor-pan-moves-origin' 'FAIL' 'the command wrote no log within 90s'
    Add-Check 'defect:preview-anchor-drift' 'FAIL' 'the command wrote no log within 90s'
    return $script:Checks
}

$anchor = $null
foreach ($line in Get-Content $log -Encoding UTF8) {
    if ($line -match '^anchor\|(.*)$') {
        $fields = @{}
        foreach ($pair in $Matches[1].Split('|')) {
            $kv = $pair.Split('=', 2)
            if ($kv.Count -eq 2) { $fields[$kv[0]] = $kv[1] }
        }
        $anchor = $fields
    }
}

function Field($Step, [string]$Key, [string]$Fallback) {
    if ($null -ne $Step -and $Step.ContainsKey($Key)) { return [string]$Step[$Key] }
    return $Fallback
}

function ConvertTo-InvariantDouble([string]$Text) {
    $value = 0.0
    if ([double]::TryParse($Text, [System.Globalization.NumberStyles]::Float,
        [System.Globalization.CultureInfo]::InvariantCulture, [ref]$value)) { return $value }
    return -1.0
}

if ($null -eq $anchor) {
    Add-Check 'flow:anchor-zoom-reaches-target' 'FAIL' 'no anchor line (see preview-anchor.log)'
    Add-Check 'flow:anchor-pan-moves-origin' 'FAIL' 'no anchor line'
    Add-Check 'defect:preview-anchor-drift' 'FAIL' 'no anchor line'
    return $script:Checks
}

$scale1 = ConvertTo-InvariantDouble (Field $anchor 'scale1' '-1')
$ok = [math]::Abs($scale1 - 2.44140625) -le 0.0001
Add-Check 'flow:anchor-zoom-reaches-target' $(if ($ok) { 'PASS' } else { 'FAIL' }) "4 wheel ticks reached 1.25^4: $scale1"

$originPan = Field $anchor 'originAfterPan' '?'
$scale2 = ConvertTo-InvariantDouble (Field $anchor 'scale2' '-1')
$expectedScale2 = $scale1 * 2.44140625
$ok = ($originPan -ne '0.000;0.000') -and ([math]::Abs($scale2 - $expectedScale2) -le 0.001)
Add-Check 'flow:anchor-pan-moves-origin' $(if ($ok) { 'PASS' } else { 'FAIL' }) "pan moved the origin to $originPan, zoom continued: $scale1 -> $scale2 (expected $expectedScale2)"

$drift = ConvertTo-InvariantDouble (Field $anchor 'drift' '-1')
$ok = ($drift -ge 0) -and ($drift -le 2.0)
Add-Check 'defect:preview-anchor-drift' $(if ($ok) { 'PASS' } else { 'FAIL' }) "image point under the fixed cursor moved: $drift DIP (u1=$(Field $anchor 'u1' '?') u2=$(Field $anchor 'u2' '?'))"

return $script:Checks
