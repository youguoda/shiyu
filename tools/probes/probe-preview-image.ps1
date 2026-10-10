# probe-preview-image.ps1 - holding Space previews an image steadily and sharply
# (user report 2026-10-09: the preview image jittered and looked low-resolution).
#
# The cause was the keyboard's auto-repeat: every repeated key-down re-filled the
# panel, putting the 240-pixel thumbnail back over an original that had already
# landed and dropping the decode in flight. And the original was decoded at the
# box's DIP width - two thirds of the pixels a 150% screen shows.
#
# The DEBUG command "preview-image" opens the bar, selects the first image card,
# writes a ready marker, and watches the preview panel's picture. This script holds
# Space on the bar the real way - one WM_KEYDOWN, then the auto-repeat as a stream
# of further key-downs every 30 ms - and lets go once the command has logged.
#
# Checks:
#   - flow:preview-image-steady   the picture goes thumbnail -> original and never
#                                 falls back to the thumbnail while Space repeats
#   - flow:preview-image-sharp    the original is decoded at the screen pixels it is
#                                 shown with (display DIP x scale, capped at its own)
#   - flow:preview-image-size     it is drawn at the computed display size
#   - defect:preview-image-seam   no black row across the panel on the real screen
#                                 (its first run found one under every seeded image:
#                                 seed.ps1's PNGs failed their zlib checksum and the
#                                 decoder dropped the last scanline - fixed there)
#
# ASCII only (see lib.ps1 header).

param([string]$Exe = '')

. (Join-Path $PSScriptRoot 'lib.ps1')

# Window class names: only Shiyu's own WPF windows count (an input method parks its
# status bar in the focused process - see probe-preview.ps1).
if (-not ('Shiyu.Probe.WindowClass' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Shiyu.Probe
{
    public static class WindowClass
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr h, StringBuilder sb, int max);

        public static string Of(IntPtr h)
        {
            var sb = new StringBuilder(256);
            GetClassName(h, sb, sb.Capacity);
            return sb.ToString();
        }
    }
}
'@
}

if ($Exe -eq '') { $Exe = Get-DefaultProbeExe }
Reset-Checks

$VK_SPACE = 0x20

$dataDir = Join-Path $env:TEMP 'shiyu-probe-run\preview-image'
& (Join-Path $PSScriptRoot 'seed.ps1') -DataDir $dataDir -AppBin (Split-Path -Parent $Exe) | Out-Null
$log = Join-Path $dataDir 'preview-image.log'
$ready = Join-Path $dataDir 'preview-image.ready'
Remove-Item $log, $ready -ErrorAction SilentlyContinue

$p = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd 'preview-image'
try {
    $bar = Wait-ProbeWindow $p.Id 384
    if ($bar -eq [IntPtr]::Zero) {
        Add-Check 'flow:preview-image-steady' 'FAIL' 'no 384-DIP bar window of the probe pid'
        return $script:Checks
    }

    $deadline = (Get-Date).AddSeconds(20)
    while (-not (Test-Path $ready) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 100 }
    if (-not (Test-Path $ready)) {
        Add-Check 'flow:preview-image-steady' 'FAIL' 'the command never selected an image card'
        return $script:Checks
    }

    # Press and hold: the first key-down, a moment for the original to land, then the
    # auto-repeat - each further key-down without a key-up reads as a repeat.
    Send-ProbeKey $bar $VK_SPACE
    Start-Sleep -Milliseconds 800
    for ($i = 0; $i -lt 40; $i++) {
        Send-ProbeKey $bar $VK_SPACE
        Start-Sleep -Milliseconds 30
    }

    # The real screen, not a render: the panel as the user sees it while Space is held.
    # A near-black row across most of the panel is a seam: the seed images have no
    # black, and the panel surface is far lighter even in the dark theme.
    Start-Sleep -Milliseconds 300
    $seamRows = -1
    foreach ($h in [Shiyu.Probe.Native]::ListWindows()) {
        if ([Shiyu.Probe.Native]::PidOf($h) -ne $p.Id -or $h -eq $bar) { continue }
        if (-not [Shiyu.Probe.WindowClass]::Of($h).StartsWith('HwndWrapper[Shiyu.App')) { continue }
        if ([Shiyu.Probe.Native]::TitleLenOf($h) -eq 0) { continue }
        $shot = Get-WindowShot $h
        if ($shot) {
            $null = Save-Shot $shot 'preview-image-onscreen'
            $px = Get-Pixels $shot
            $seamRows = 0
            for ($y = 0; $y -lt $px.H; $y++) {
                $dark = 0; $n = 0
                for ($x = [int]($px.W * 0.1); $x -lt [int]($px.W * 0.9); $x += 3) {
                    $c = Get-Px $px $x $y
                    $n++
                    if ($c[0] -le 12 -and $c[1] -le 12 -and $c[2] -le 12) { $dark++ }
                }
                if ($n -gt 0 -and $dark / $n -ge 0.6) { $seamRows++ }
            }
            $shot.Dispose()
        }
        break
    }

    $deadline = (Get-Date).AddSeconds(20)
    while (-not (Test-Path $log) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
    Send-ProbeKey $bar $VK_SPACE -KeyUp
    Start-Sleep -Milliseconds 300
} finally {
    if ($p) { Stop-ProbeApp $p }
}

if (-not (Test-Path $log)) {
    Add-Check 'flow:preview-image-steady' 'FAIL' 'the command wrote no log'
    return $script:Checks
}

$lines = @{}
$sources = @()
foreach ($line in Get-Content $log -Encoding UTF8) {
    if ($line -match '^(\w+)\|(.*)$') {
        $fields = @{}
        foreach ($pair in $Matches[2].Split('|')) {
            $kv = $pair.Split('=', 2)
            if ($kv.Count -eq 2) { $fields[$kv[0]] = $kv[1] }
        }
        if ($Matches[1] -eq 'source') { $sources += , $fields } else { $lines[$Matches[1]] = $fields }
    }
}

if ($lines['image']) {
    Add-Check 'flow:preview-image-steady' 'FAIL' 'the seed has no image card'
    return $script:Checks
}

$final = $lines['final']
$selected = $lines['selected']
if (-not $final) {
    Add-Check 'flow:preview-image-steady' 'FAIL' 'no final line'
    return $script:Checks
}

$trail = ($sources | ForEach-Object { '{0}@{1}' -f $_['kind'], $_['at'] }) -join ' '
$ok = $final['kind'] -eq 'original' -and $final['backToThumbnail'] -eq '0' -and [int]$final['changes'] -le 2
Add-Check 'flow:preview-image-steady' $(if ($ok) { 'PASS' } else { 'FAIL' }) `
    ("{0} change(s), {1} fall-back(s) to the thumbnail while Space repeated; ends on {2} ({3})" -f `
        $final['changes'], $final['backToThumbnail'], $final['kind'], $trail)

$ok = $final['expected'] -ne '0x0' -and $final['px'] -eq $final['expected']
Add-Check 'flow:preview-image-sharp' $(if ($ok) { 'PASS' } else { 'FAIL' }) `
    ("decoded {0} px, the screen box is {1} px (image {2}, scale {3})" -f `
        $final['px'], $final['expected'], $selected['pixels'], $selected['scale'])

$shown = $final['shown'].Split('x') | ForEach-Object { [double]$_ }
$display = $final['display'].Split('x') | ForEach-Object { [double]$_ }
$ok = $display[0] -gt 0 -and [Math]::Abs($shown[0] - $display[0]) -le 0.5 -and [Math]::Abs($shown[1] - $display[1]) -le 0.5
Add-Check 'flow:preview-image-size' $(if ($ok) { 'PASS' } else { 'FAIL' }) `
    ("drawn {0} DIP, display box {1} DIP" -f $final['shown'], $final['display'])

if ($seamRows -lt 0) {
    Add-Check 'defect:preview-image-seam' 'SKIP' 'no on-screen preview window to capture'
} else {
    Add-Check 'defect:preview-image-seam' $(if ($seamRows -eq 0) { 'PASS' } else { 'FAIL' }) `
        ("{0} near-black row(s) across the panel on the real screen" -f $seamRows)
}

return $script:Checks
