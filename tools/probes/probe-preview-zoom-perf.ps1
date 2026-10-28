# probe-preview-zoom-perf.ps1 - wheel zoom / pan responsiveness of the pinned
# image preview (user report 2026-10-20: zoom jank, pan jank).
#
# The DEBUG command "preview-zoom-perf" synthesizes wheel bursts on two images
# (a runtime-generated 3840x2160 screenshot and the seeded 900x300), one pan
# drag on the large one, and a wind-down back to 1x. Per segment it reports:
#   - input-priority pings every 25 ms (UI-thread latency, MeasureStream style)
#   - Dispatcher.Hooks longest operation and busy share
#   - CompositionTarget.Rendering frame gaps (render-thread stalls - the
#     HighQuality resample never touches the dispatcher, so only frame gaps
#     can see it)
#
# Budgets follow the stream-jank precedent (probe-stream-jank.ps1): UI ops
# <= 150 ms max / 50 ms p95, frame gaps <= 150 ms max / 50 ms p95. A defect:*
# check FAILs when a budget breaks; the small-image one may legitimately pass
# (not reproduced - that is a finding too).
#
# ASCII only (see lib.ps1 header).

param([string]$Exe = '')

. (Join-Path $PSScriptRoot 'lib.ps1')

if ($Exe -eq '') { $Exe = Get-DefaultProbeExe }
Reset-Checks

$dataDir = Join-Path $env:TEMP 'shiyu-probe-run\preview-zoom-perf'
& (Join-Path $PSScriptRoot 'seed.ps1') -DataDir $dataDir -AppBin (Split-Path -Parent $Exe) | Out-Null
$log = Join-Path $dataDir 'preview-zoom-perf.log'
Remove-Item $log -ErrorAction SilentlyContinue

$p = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd 'preview-zoom-perf' -ExtraEnv @{ SHIYU_FAKE_BACKEND = '1' }
try {
    $deadline = (Get-Date).AddSeconds(150)
    while (-not (Test-Path $log) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
    while ((Get-Date) -lt $deadline -and (Test-Path $log) -and (@(Select-String -Path $log -Pattern '^done').Count -eq 0)) {
        Start-Sleep -Milliseconds 500
    }
} finally {
    if ($p) { Stop-ProbeApp $p }
}

if (-not (Test-Path $log)) {
    Add-Check 'defect:preview-zoom-jank-large' 'FAIL' 'the command wrote no log within 150s'
    Add-Check 'defect:preview-pan-jank-large' 'FAIL' 'the command wrote no log within 150s'
    Add-Check 'defect:preview-zoom-jank-small' 'FAIL' 'the command wrote no log within 150s'
    Add-Check 'flow:preview-zoom-clamps' 'FAIL' 'the command wrote no log within 150s'
    Add-Check 'perf:preview-transform-ui-cost' 'FAIL' 'the command wrote no log within 150s'
    return $script:Checks
}

$lines = @{}
foreach ($line in Get-Content $log -Encoding UTF8) {
    if ($line -match '^(zoom|pan|wind)\|img=(\w+)\|(.*)$') {
        $fields = @{}
        foreach ($pair in $Matches[3].Split('|')) {
            $kv = $pair.Split('=', 2)
            if ($kv.Count -eq 2) { $fields[$kv[0]] = $kv[1] }
        }
        $lines["$($Matches[1]):$($Matches[2])"] = $fields
    }
    if ($line -match '^clamps\|img=(\w+)\|(.*)$') {
        $fields = @{}
        foreach ($pair in $Matches[2].Split('|')) {
            $kv = $pair.Split('=', 2)
            if ($kv.Count -eq 2) { $fields[$kv[0]] = $kv[1] }
        }
        $lines["clamps:$($Matches[1])"] = $fields
    }
}

function Get-Line([string]$Key) {
    if ($lines.ContainsKey($Key)) { return $lines[$Key] }
    return @{}
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

# A segment holds when every budget is met; -1 (missing) counts as broken data.
function Test-Bounds($Step) {
    if ($null -eq $Step -or $Step.Count -eq 0) { return $false }
    foreach ($name in @('inputMaxMs', 'longestOpMs', 'gapMaxMs')) {
        $value = ConvertTo-InvariantDouble (Field $Step $name '-1')
        if (($value -lt 0) -or ($value -gt 150)) { return $false }
    }
    foreach ($name in @('inputP95Ms', 'gapP95Ms')) {
        $value = ConvertTo-InvariantDouble (Field $Step $name '-1')
        if (($value -lt 0) -or ($value -gt 50)) { return $false }
    }
    return $true
}

function Describe($Step) {
    if ($null -eq $Step -or $Step.Count -eq 0) { return 'no segment line' }
    return "inputMax=$(Field $Step 'inputMaxMs' '?') inputP95=$(Field $Step 'inputP95Ms' '?') longestOp=$(Field $Step 'longestOpMs' '?') gapMax=$(Field $Step 'gapMaxMs' '?') gapP95=$(Field $Step 'gapP95Ms' '?') ms=$(Field $Step 'ms' '?')"
}

$zoomLarge = Get-Line 'zoom:large'
$panLarge = Get-Line 'pan:large'
$zoomSmall = Get-Line 'zoom:small'

$ok = Test-Bounds $zoomLarge
Add-Check 'defect:preview-zoom-jank-large' $(if ($ok) { 'PASS' } else { 'FAIL' }) "3840x2160 zoom budgets: $(Describe $zoomLarge)"

$ok = Test-Bounds $panLarge
Add-Check 'defect:preview-pan-jank-large' $(if ($ok) { 'PASS' } else { 'FAIL' }) "3840x2160 pan budgets: $(Describe $panLarge)"

$ok = Test-Bounds $zoomSmall
Add-Check 'defect:preview-zoom-jank-small' $(if ($ok) { 'PASS' } else { 'FAIL' }) "900x300 zoom budgets: $(Describe $zoomSmall)"

$clamps = Get-Line 'clamps:large'
$up = ConvertTo-InvariantDouble (Field $clamps 'scaleAfterUp' '-1')
$down = ConvertTo-InvariantDouble (Field $clamps 'scaleAfterDown' '-1')
$origin = Field $clamps 'origin' '?'
# Up clamps at 8x the fitted size; down goes below fit now (the window shrinks with the picture,
# user request 2026-10-28) and stops at a 64-DIP long side, with nothing left to pan.
$ok = ([math]::Abs($up - 8.0) -le 0.001) -and ($down -gt 0) -and ($down -lt 1.0) -and ($origin -eq '0.000;0.000')
Add-Check 'flow:preview-zoom-clamps' $(if ($ok) { 'PASS' } else { 'FAIL' }) "120 ticks up -> $up, 40 down -> $down (below fit), origin=$origin"

# User report 2026-10-22: zooming in only magnified the box-sized decode and got blurrier.
# Once zoomed, the panel must swap in the original's full pixels.
$sharp = @{}
$actual = @{}
foreach ($line in Get-Content $log -Encoding UTF8) {
    if ($line -match '^(sharp|actual)\|img=large\|(.*)$') {
        $target = if ($Matches[1] -eq 'sharp') { $sharp } else { $actual }
        foreach ($pair in $Matches[2].Split('|')) {
            $kv = $pair.Split('=', 2)
            if ($kv.Count -eq 2) { $target[$kv[0]] = $kv[1] }
        }
    }
}
$ok = $sharp.Count -gt 0 -and $sharp['zoomPx'] -eq $sharp['cardPx'] -and $sharp['fitPx'] -ne $sharp['cardPx']
$detail = if ($sharp.Count -gt 0) { "3840x2160 source pixels at fit {0}, after zooming {1} (original {2})" -f $sharp['fitPx'], $sharp['zoomPx'], $sharp['cardPx'] } else { 'no sharp line' }
Add-Check 'flow:preview-zoom-sharp' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

# User request 2026-10-28: double-click shrinks the preview to a small square, double-click again
# shows the picture one image pixel per screen pixel.
$expected = ConvertTo-InvariantDouble (Field $actual 'expected' '-1')
$scale = ConvertTo-InvariantDouble (Field $actual 'scale' '-1')
$ok = ((Field $actual 'square' '?') -eq 'True') -and ((Field $actual 'stillSquare' '?') -eq 'False') -and ($expected -gt 1.01) -and ([math]::Abs($scale - $expected) -le 0.01)
Add-Check 'flow:preview-dblclick-actual-size' $(if ($ok) { 'PASS' } else { 'FAIL' }) "double-click -> square $(Field $actual 'square' '?') ($(Field $actual 'squareWindow' '?') DIP), again -> $scale (1:1 is $expected), window $(Field $actual 'window' '?')"

# The window follows the picture: it grows while zooming in (up to the work area, where the rest
# zooms inside the box) and shrinks again when zooming out past fit.
$win = @{}
foreach ($line in Get-Content $log -Encoding UTF8) {
    if ($line -match '^window\|img=large\|(.*)$') {
        foreach ($pair in $Matches[1].Split('|')) {
            $kv = $pair.Split('=', 2)
            if ($kv.Count -eq 2) { $win[$kv[0]] = $kv[1] }
        }
    }
}
$area = { param($s) if ($s -match '^([\d.]+)x([\d.]+)$') { [double]$Matches[1] * [double]$Matches[2] } else { -1 } }
$fitArea = & $area $win['fit']
$upArea = & $area $win['up']
$downArea = & $area $win['down']
$ok = ($fitArea -gt 0) -and ($upArea -gt $fitArea) -and ($downArea -gt 0) -and ($downArea -lt $fitArea) -and ((ConvertTo-InvariantDouble (Field $win 'upOverflow' '-1')) -gt 1.0)
Add-Check 'flow:preview-window-follows-zoom' $(if ($ok) { 'PASS' } else { 'FAIL' }) "3840x2160 window at fit $($win['fit']), zoomed in $($win['up']) (box overflow $($win['upOverflow'])), zoomed out $($win['down'])"

$longest = ConvertTo-InvariantDouble (Field $zoomLarge 'longestOpMs' '-1')
$ok = ($longest -ge 0) -and ($longest -le 150)
Add-Check 'perf:preview-transform-ui-cost' $(if ($ok) { 'PASS' } else { 'FAIL' }) "zoom transform stays off the UI thread budget: longestOpMs=$longest"

return $script:Checks
