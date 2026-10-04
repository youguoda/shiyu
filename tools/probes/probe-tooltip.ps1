# probe-tooltip.ps1 - every tooltip wears Shiyu's look and follows the theme (user
# request 2026-10-05: small rounded corners, light/dark like the rest of the app).
#
# The DEBUG command "tooltip" opens settings, picks the element with the longest
# tooltip text, opens a real ToolTip for it (the pointer never moves), and logs the
# face of what WPF built from the implicit style; then it switches the probe's theme
# to dark and logs again. A style on StaticResource would keep its light colours
# there - only DynamicResource follows. tooltip-light.png / tooltip-dark.png land in
# the data dir for a human look.
#
# Checks:
#   - data:tooltip-opened         both phases were logged
#   - flow:tooltip-styled         small radius (Radius.Control), flyout layer, border stroke,
#                                 caption size, the square system shadow off
#   - flow:tooltip-follows-theme  dark colours are the dark palette's, not the light ones
#
# ASCII only (see lib.ps1 header).

param([string]$Exe = '')

. (Join-Path $PSScriptRoot 'lib.ps1')

if ($Exe -eq '') { $Exe = Get-DefaultProbeExe }
Reset-Checks

$dataDir = Join-Path $env:TEMP 'shiyu-probe-run\tooltip'
& (Join-Path $PSScriptRoot 'seed.ps1') -DataDir $dataDir -AppBin (Split-Path -Parent $Exe) | Out-Null
$log = Join-Path $dataDir 'tooltip.log'
Remove-Item $log -ErrorAction SilentlyContinue

$p = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd 'tooltip'
try {
    $deadline = (Get-Date).AddSeconds(40)
    while (-not (Test-Path $log) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
} finally {
    if ($p) { Stop-ProbeApp $p }
}

if (-not (Test-Path $log)) {
    Add-Check 'data:tooltip-opened' 'FAIL' 'the command wrote no log within 40s'
    return $script:Checks
}

# tip|<phase>|bg=..|bgExpect=..|stroke=..|strokeExpect=..|radius=..|radiusExpect=..|font=..|fontExpect=..|systemShadow=..|width=..
$tips = @{}
foreach ($line in Get-Content $log) {
    if ($line -match '^tip\|(\w+)\|(.*)$') {
        $fields = @{}
        foreach ($pair in $Matches[2].Split('|')) {
            $kv = $pair.Split('=', 2)
            $fields[$kv[0]] = $kv[1]
        }
        $tips[$Matches[1]] = $fields
    }
}

if (-not ($tips.ContainsKey('light') -and $tips.ContainsKey('dark'))) {
    Add-Check 'data:tooltip-opened' 'FAIL' ("phases logged: {0} (no element with a tooltip in settings?)" -f (($tips.Keys | Sort-Object) -join ', '))
    return $script:Checks
}
Add-Check 'data:tooltip-opened' 'PASS' ("light and dark; width {0} DIP" -f $tips['light']['width'])

$t = $tips['light']
$problems = @()
if ($t['radius'] -ne $t['radiusExpect']) { $problems += ("radius {0} (expected {1})" -f $t['radius'], $t['radiusExpect']) }
if ($t['bg'] -ne $t['bgExpect']) { $problems += ("background {0} (expected {1})" -f $t['bg'], $t['bgExpect']) }
if ($t['stroke'] -ne $t['strokeExpect']) { $problems += ("stroke {0} (expected {1})" -f $t['stroke'], $t['strokeExpect']) }
if ($t['font'] -ne $t['fontExpect']) { $problems += ("font {0} (expected {1})" -f $t['font'], $t['fontExpect']) }
if ($t['systemShadow'] -ne 'False') { $problems += 'system shadow still on' }
$detail = "radius {0}, background {1}, stroke {2}, font {3}" -f $t['radius'], $t['bg'], $t['stroke'], $t['font']
if ($problems.Count -eq 0) {
    Add-Check 'flow:tooltip-styled' 'PASS' $detail
} else {
    Add-Check 'flow:tooltip-styled' 'FAIL' ($problems -join '; ')
}

$d = $tips['dark']
$follows = $d['bg'] -eq $d['bgExpect'] -and $d['stroke'] -eq $d['strokeExpect'] -and $d['bg'] -ne $t['bg']
Add-Check 'flow:tooltip-follows-theme' $(if ($follows) { 'PASS' } else { 'FAIL' }) `
    ("dark background {0} (palette {1}), light was {2}" -f $d['bg'], $d['bgExpect'], $t['bg'])

return $script:Checks
