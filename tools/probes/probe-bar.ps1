# probe-bar.ps1 - the resident narrow bar (ticket 15).
#
# Starts the probe instance with the bar window opened directly (probe
# command channel, no global hotkey), seeds synthetic history, and checks:
#   - the seeded entries are visible (footer count and realized cards)
#   - defect: double corner radius on the outer shell (review 3.1 P0)
#   - defect: the "<- ->" keycap clipped to a horizontal line (review 3.2 P0)
#   - defect: hover tray delete glyph carries no danger colour (review 3.4 P0)
#     - SKIPPED when the user is present (needs the real mouse)
#
# ASCII only (see lib.ps1 header).

param([string]$Exe = '')

. (Join-Path $PSScriptRoot 'lib.ps1')

if ($Exe -eq '') { $Exe = Get-DefaultProbeExe }
Reset-Checks

$dataDir = Join-Path $env:TEMP 'shiyu-probe-run\bar'
& (Join-Path $PSScriptRoot 'seed.ps1') -DataDir $dataDir | Out-Null

$VK_CONTROL = 0x11
$p = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd 'bar'
try {
    $hwnd = Wait-ProbeWindow $p.Id 384
    if ($hwnd -eq [IntPtr]::Zero) {
        Add-Check 'window:bar-found' 'FAIL' 'no 384-DIP window of the probe pid'
        return $script:Checks
    }
    Add-Check 'window:bar-found' 'PASS' ("hwnd={0}" -f $hwnd)

    # The bar opens beside the cursor, which can be any monitor on a
    # multi-screen machine; pin it to one known spot before measuring.
    Move-WindowToProbeSpot $hwnd 0
    Start-Sleep -Milliseconds 1500   # entrance + content settle
    $rect = Get-WindowRectInfo $hwnd 384  # position can shift during entrance

    $shot = Get-WindowShot $hwnd
    if ($shot) { [void](Save-Shot $shot 'bar-default') }

    # --- seeded entries visible -------------------------------------------
    $win = $null
    foreach ($w in (Get-UiaWindowsOfPid $p.Id)) {
        $r = $w.Current.BoundingRectangle
        if ([Math]::Abs($r.Width - 384 * $rect.Scale) -le 12) { $win = $w; break }
    }
    $tree = if ($win) { Get-UiaTree $win } else { @() }
    $cards = @($tree | Where-Object { $_.Name -eq 'Shiyu.App.BarCard' } | Sort-Object T)
    Add-Check 'data:cards-realized' `
        $(if ($cards.Count -ge 3) { 'PASS' } else { 'FAIL' }) `
        ("realized BarCard elements: {0} (seeded 17 total, list is virtualized)" -f $cards.Count)

    # The footer's "N entries" label: a short text containing the seeded
    # total as a standalone number (ordinal match - no Chinese literals).
    $footerHits = @($tree | Where-Object {
        $_.Type -eq 'Text' -and $_.Name.Length -le 24 -and $_.Name -match '(?<!\d)17(?!\d)' })
    Add-Check 'data:footer-count' `
        $(if ($footerHits.Count -ge 1) { 'PASS' } else { 'FAIL' }) `
        ("short texts carrying the seeded total 17: {0}" -f $footerHits.Count)

    # --- defect: double corner radius -------------------------------------
    if ($shot) {
        Add-CornerRadiusCheck 'bar' $shot $rect 0.0
    } else {
        Add-Check 'defect:bar-double-radius' 'FAIL' 'screenshot failed'
    }

    # --- defect: keycap "<- ->" clipped to a horizontal line ----------------
    # The reliable route, learned the hard way across three flips: click a card
    # once (the bar is WS_EX_NOACTIVATE so the CLICK alone is not enough, but a
    # real click does hand it the foreground now that cards carry no system
    # tooltip - the tooltip's popup used to take the foreground and swallow the
    # key), then a REAL keyboard Ctrl. PostMessage Ctrl works only while the
    # session's input state happens to cooperate - it has flipped twice.
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public class BarKeycapInput2 {
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
 [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint data, UIntPtr extra);
 [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
}
'@ -ErrorAction SilentlyContinue
    $clickCard = $cards | Select-Object -First 1
    if ($clickCard) {
        [void][BarKeycapInput2]::SetCursorPos(($clickCard.L + 60), ($clickCard.T + [int]($clickCard.H / 2)))
        Start-Sleep -Milliseconds 250
        [BarKeycapInput2]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
        [BarKeycapInput2]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 500
    }
    [BarKeycapInput2]::keybd_event(0x11, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 700
    $shotCtrl = Get-WindowShot $hwnd
    if ($shotCtrl) { [void](Save-Shot $shotCtrl 'bar-ctrl') }

    # The badge only lays out while Ctrl is held, so the arrow element must
    # come from a scan taken NOW - a pre-Ctrl rect is stale.
    if ($win) { $tree = Get-UiaTree $win }
    $arrow = [string][char]0x2190 + [char]0x2192
    $arrowEl = $tree | Where-Object { $_.Name -eq $arrow } | Select-Object -First 1
    if (-not $arrowEl) {
        Add-Check 'defect:bar-keycap-arrows-clipped' 'FAIL' 'arrow keycap element not found while ctrl held'
    } elseif (-not $shotCtrl) {
        Add-Check 'defect:bar-keycap-arrows-clipped' 'FAIL' 'screenshot failed'
    } else {
        # The badge is a small accent block centred on the arrow element;
        # scan a padded crop for it and measure the light "text on accent"
        # ink INSIDE it. The defect renders the arrows as a ~1px horizontal
        # line, so the ink's height is the verdict. The window's own rect is
        # re-read here: it can shift during the entrance.
        $rect = Get-WindowRectInfo $hwnd 384
        $cx = $arrowEl.L + [int]($arrowEl.W / 2) - $rect.L
        $cy = $arrowEl.T + [int]($arrowEl.H / 2) - $rect.T
        $pad = [int][Math]::Ceiling(16 * $rect.Scale)
        $crop = Get-CropPixels $shotCtrl ($cx - $pad) ($cy - $pad) ($pad * 2) ($pad * 2)
        # evidence: what the scan actually looked at
        $cropRect = New-Object System.Drawing.Rectangle(([int]($cx - $pad)), ([int]($cy - $pad)), ($pad * 2), ($pad * 2))
        $cropRect.Intersect((New-Object System.Drawing.Rectangle(0, 0, $shotCtrl.Width, $shotCtrl.Height)))
        if ($cropRect.Width -gt 0 -and $cropRect.Height -gt 0) {
            [void](Save-Shot ($shotCtrl.Clone($cropRect, $shotCtrl.PixelFormat)) 'bar-keycap-crop')
        }
        if (-not $crop) {
            Add-Check 'defect:bar-keycap-arrows-clipped' 'FAIL' 'crop failed'
        } else {
            $minX = -1; $minY = -1; $maxX = -1; $maxY = -1; $accentHits = 0
            for ($yy = 0; $yy -lt $crop.H; $yy++) {
                for ($xx = 0; $xx -lt $crop.W; $xx++) {
                    $pxl = Get-Px $crop $xx $yy
                    if (((Get-ColorDistance $pxl $AccentLight) -le 40) -or ((Get-ColorDistance $pxl $AccentDark) -le 40)) {
                        $accentHits++
                        if ($minX -lt 0 -or $xx -lt $minX) { $minX = $xx }
                        if ($xx -gt $maxX) { $maxX = $xx }
                        if ($minY -lt 0 -or $yy -lt $minY) { $minY = $yy }
                        if ($yy -gt $maxY) { $maxY = $yy }
                    }
                }
            }
            $badgeW = ($maxX - $minX + 1) / $rect.Scale
            $badgeH = ($maxY - $minY + 1) / $rect.Scale
            # The KeyCap style is width-as-content: single letters ~18 DIP,
            # "Tab" ~28, "<->" ~30 (ticket 19). Only a match wider than that,
            # or taller than any keycap, means the accent was something else.
            if ($accentHits -lt 40 -or $badgeH -lt 8 -or $badgeW -gt 38 -or $badgeH -gt 26) {
                Add-Check 'defect:bar-keycap-arrows-clipped' 'FAIL' `
                    ("accent badge block not found (hits={0}, box={1:N1}x{2:N1} DIP, center=({3},{4}), shot={5}x{6})" -f `
                        $accentHits, $badgeW, $badgeH, $cx, $cy, $shotCtrl.Width, $shotCtrl.Height)
            } else {
                # ink = light pixels well away from the accent itself
                $inkRows = @{}
                for ($yy = $minY; $yy -le $maxY; $yy++) {
                    for ($xx = $minX; $xx -le $maxX; $xx++) {
                        $pxl = Get-Px $crop $xx $yy
                        if (((Get-ColorDistance $pxl $AccentLight) -gt 80) -and ((Get-ColorDistance $pxl $AccentDark) -gt 80) `
                            -and ($pxl[0] -gt 150) -and ($pxl[1] -gt 150) -and ($pxl[2] -gt 150)) {
                            $inkRows[$yy] = $true
                        }
                    }
                }
                $inkH = [Math]::Round($inkRows.Count / $rect.Scale, 1)
                $detail = ("badge {0:N1}x{1:N1} DIP; light ink inside: {2} rows = {3:N1} DIP tall (a readable keycap needs >= 6 DIP)" -f `
                    $badgeW, $badgeH, $inkRows.Count, $inkH)
                if ($inkH -lt 6) {
                    Add-Check 'defect:bar-keycap-arrows-clipped' 'FAIL' $detail
                } else {
                    Add-Check 'defect:bar-keycap-arrows-clipped' 'PASS' $detail
                }
            }
        }
    }
    [BarKeycapInput2]::keybd_event(0x11, 0, 2, [UIntPtr]::Zero)
    [void][BarKeycapInput2]::SetCursorPos(60, 60)
    Start-Sleep -Milliseconds 400

    # --- defect: hover tray delete colour ---------------------------------
    # The only assertion allowed to use the real mouse: hover a card so the
    # action tray squeezes in, then compare the delete glyph's colour with
    # its neighbours'. Skipped entirely when the user is present.
    if (Test-UserPresent) {
        Add-Check 'defect:bar-tray-delete-danger-color' 'SKIP' `
            'user present (cursor moved between samples) - real-mouse hover skipped'
    } elseif ($cards.Count -lt 2) {
        Add-Check 'defect:bar-tray-delete-danger-color' 'SKIP' 'fewer than two cards realized'
    } else {
        # first NON-pinned card: index 0 is the pinned host's card (its tray
        # hides delete under protection), index 1 is the newest plain card.
        $card = $cards[1]
        $hoverX = $card.L + $card.W - [int][Math]::Round(10 * $rect.Scale)
        $hoverY = $card.T + [int][Math]::Round(13 * $rect.Scale)
        $restore = [Shiyu.Probe.Native]::Cursor()
        [void][Shiyu.Probe.Native]::MoveCursor($hoverX, $hoverY)
        Start-Sleep -Milliseconds 900

        $shotHover = Get-WindowShot $hwnd
        if ($shotHover) { [void](Save-Shot $shotHover 'bar-hover') }
        $tree = if ($win) { Get-UiaTree $win } else { @() }

        # tray buttons: small glyph buttons of the card's meta row, i.e.
        # buttons of tray size whose centre sits inside the card's upper band.
        $bandBottom = $card.T + [int](44 * $rect.Scale)
        $minW = [int](18 * $rect.Scale); $maxW = [int](44 * $rect.Scale)
        $minH = [int](18 * $rect.Scale); $maxH = [int](44 * $rect.Scale)
        $tray = @($tree | Where-Object {
            $_.Type -eq 'Button' -and $_.Name.Length -le 2 `
            -and $_.W -ge $minW -and $_.W -le $maxW -and $_.H -ge $minH -and $_.H -le $maxH `
            -and ($_.L + [int]($_.W / 2)) -ge ($card.L - 4) `
            -and ($_.L + [int]($_.W / 2)) -le ($card.L + $card.W + 4) `
            -and ($_.T + [int]($_.H / 2)) -ge ($card.T - 2) `
            -and ($_.T + [int]($_.H / 2)) -le $bandBottom } | Sort-Object L)

        if ($tray.Count -lt 4 -or -not $shotHover) {
            Add-Check 'defect:bar-tray-delete-danger-color' 'FAIL' `
                ("tray buttons found: {0} (hover at {1},{2})" -f $tray.Count, $hoverX, $hoverY)
        } else {
            $bodyX = $card.L - $rect.L + [int][Math]::Round(20 * $rect.Scale)
            $bodyY = $card.T - $rect.T + [int][Math]::Round(40 * $rect.Scale)
            $bg = [Shiyu.Probe.Pixels]::MedianColor($shotHover, $bodyX, $bodyY, 80,
                                                    [int][Math]::Max(4, [int][Math]::Round(8 * $rect.Scale)))
            $deleteBtn = $tray[-1]
            $coreColors = @()
            foreach ($b in $tray) {
                $coreColors += , @([Shiyu.Probe.Pixels]::InkCoreColor($shotHover,
                    $b.L - $rect.L, $b.T - $rect.T, $b.W, $b.H, $bg, 70))
            }
            $del = $coreColors[-1]
            $minDist = ($coreColors[0..($coreColors.Count - 2)] | ForEach-Object { Get-ColorDistance $_ $del } |
                Measure-Object -Minimum).Minimum
            $detail = ("{0} tray buttons; delete core rgb={1},{2},{3}; min distance to siblings={4}" -f `
                $tray.Count, $del[0], $del[1], $del[2], $minDist)
            if ($del[0] -lt 0) {
                Add-Check 'defect:bar-tray-delete-danger-color' 'FAIL' 'no ink in the delete button rect'
            } elseif ($minDist -le 50) {
                Add-Check 'defect:bar-tray-delete-danger-color' 'FAIL' `
                    ($detail + ' - delete looks exactly like its neighbours (danger colour not applied)')
            } else {
                Add-Check 'defect:bar-tray-delete-danger-color' 'PASS' $detail
            }
        }
        [void][Shiyu.Probe.Native]::MoveCursor($restore[0], $restore[1])
    }
} finally {
    Stop-ProbeApp $p
}

return $script:Checks
