# probe-library.ps1 - the history management window (ticket 15).
#
# Checks:
#   - the seeded history is listed (realized rows)
#   - defect: the two button groups in the bottom bar collide at the
#     default width (review 3.8 P0)
#
# Ticket 24: the window's default width moved 1150 -> 1100 (section 6.4).
# The locator follows the window (pid + its default DIP width); what the
# check asserts is unchanged - same window of the probe pid, same seeded
# rows, same overlap-free bottom band.
#
# ASCII only (see lib.ps1 header).

param([string]$Exe = '')

. (Join-Path $PSScriptRoot 'lib.ps1')

if ($Exe -eq '') { $Exe = Get-DefaultProbeExe }
Reset-Checks

$dataDir = Join-Path $env:TEMP 'shiyu-probe-run\library'
& (Join-Path $PSScriptRoot 'seed.ps1') -DataDir $dataDir | Out-Null

$p = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd 'library'
try {
    $hwnd = Wait-ProbeWindow $p.Id 1100
    if ($hwnd -eq [IntPtr]::Zero) {
        Add-Check 'window:library-found' 'FAIL' 'no 1100-DIP window of the probe pid'
        return $script:Checks
    }
    Add-Check 'window:library-found' 'PASS' ("hwnd={0}" -f $hwnd)
    Move-WindowToProbeSpot $hwnd 3
    Start-Sleep -Milliseconds 1500

    $rect = Get-WindowRectInfo $hwnd 1100
    $shot = Get-WindowShot $hwnd
    if ($shot) { [void](Save-Shot $shot 'library') }

    $win = $null
    foreach ($w in (Get-UiaWindowsOfPid $p.Id)) {
        $r = $w.Current.BoundingRectangle
        if ([Math]::Abs($r.Width - 1100 * $rect.Scale) -le 20) { $win = $w; break }
    }
    $tree = if ($win) { Get-UiaTree $win } else { @() }

    $rows = @($tree | Where-Object { $_.Type -eq 'ListItem' })
    Add-Check 'data:library-rows' `
        $(if ($rows.Count -ge 5) { 'PASS' } else { 'FAIL' }) `
        ("list items realized: {0} (seeded 17)" -f $rows.Count)

    # Ticket 27 / U-29: the command bar's icon buttons must be named. The
    # names come from KeyMap (name single source) in the "action (key)"
    # format - and they must survive the Ctrl-held keycap swap, which is why
    # they live on AutomationProperties.Name rather than on the content.
    $wanted = @(
        (New-ZhKeyName @(0x590D,0x5236) 'C'),   # copy (C)
        (New-ZhKeyName @(0x7F6E,0x9876) 'P'),   # pin (P)
        (New-ZhKeyName @(0x6536,0x85CF) 'S'),   # favorite (S)
        (New-ZhKeyName @(0x6807,0x7B7E) 'T'),   # tag (T) - library key, not Tab
        (New-ZhKeyName @(0x5220,0x9664) 'D'),   # delete (D)
        (New-Zh @(0x7B5B,0x9009)),              # filter (icon + text composite)
        (New-Zh @(0x66F4,0x591A,0x64CD,0x4F5C)),# more (... menu)
        (New-Zh @(0x641C,0x7D22,0x5386,0x53F2)) # search box
    )
    $nameMiss = @()
    foreach ($want in $wanted) {
        if (-not ($tree | Where-Object { $_.Name -eq $want })) { $nameMiss += $want }
    }
    Add-Check 'a11y:library-command-names' `
        $(if ($nameMiss.Count -eq 0) { 'PASS' } else { 'FAIL' }) `
        ("{0} of {1} command-bar names present in the UIA tree" -f ($wanted.Count - $nameMiss.Count), $wanted.Count)

    # The bottom bar: every button-sized control in the lowest band. The
    # defect puts two groups in one grid row so their rects intersect.
    $bandTop = $rect.T + $rect.H - [int](130 * $rect.Scale)
    $controls = @($tree | Where-Object {
        ($_.Type -eq 'Button' -or $_.Type -eq 'ComboBox' -or $_.Type -eq 'Edit' -or $_.Type -eq 'Calendar') `
        -and $_.T -ge $bandTop -and $_.W -ge 20 -and $_.W -le 500 -and $_.H -ge 14 -and $_.H -le 60 })

    $clashes = @()
    for ($i = 0; $i -lt $controls.Count; $i++) {
        for ($j = $i + 1; $j -lt $controls.Count; $j++) {
            $a = $controls[$i]; $b = $controls[$j]
            # skip containment (a label inside its control)
            $inside = ($a.L -le $b.L -and $a.L + $a.W -ge $b.L + $b.W -and $a.T -le $b.T -and $a.T + $a.H -ge $b.T + $b.H) `
                   -or ($b.L -le $a.L -and $b.L + $b.W -ge $a.L + $a.W -and $b.T -le $a.T -and $b.T + $b.H -ge $a.T + $a.H)
            if ($inside) { continue }
            $ox = [Math]::Min($a.L + $a.W, $b.L + $b.W) - [Math]::Max($a.L, $b.L)
            $oy = [Math]::Min($a.T + $a.H, $b.T + $b.H) - [Math]::Max($a.T, $b.T)
            if ($ox -gt 3 -and $oy -gt 3) {
                $clashes += ("{0},{1} {2}x{3} vs {4},{5} {6}x{7} (overlap {8}x{9})" -f `
                    $a.L, $a.T, $a.W, $a.H, $b.L, $b.T, $b.W, $b.H, $ox, $oy)
            }
        }
    }

    $detail = ("{0} controls in the bottom band; {1} overlapping pair(s)" -f `
        $controls.Count, $clashes.Count)
    if ($clashes.Count -gt 0) {
        $detail += '; ' + ($clashes[0..([Math]::Min(2, $clashes.Count) - 1)] -join ' | ')
        Add-Check 'defect:library-bottom-overlap' 'FAIL' $detail
    } else {
        Add-Check 'defect:library-bottom-overlap' 'PASS' $detail
    }

    # Ticket 24 acceptance: no control overlaps anywhere across the whole
    # width range 720-1400 DIP (single column below 960, two columns above).
    # The window is resized to both extremes and the FULL control tree is
    # pairwise-checked the same way the bottom band is above.
    $widthFailures = @()
    $widthSkipped = @()
    Add-Type -AssemblyName System.Windows.Forms
    $workWidth = [System.Windows.Forms.Screen]::FromHandle($hwnd).WorkingArea.Width
    foreach ($dipWidth in @(720, 940, 1400)) {
        # A width the screen cannot hold (1400 DIP on a 1280-pixel screen) is never reached:
        # Windows clamps the window, and "window not found" would blame the layout for the monitor.
        if ([Math]::Round($dipWidth * $rect.Scale) -gt $workWidth) {
            $widthSkipped += $dipWidth
            continue
        }

        # SWP_NOZORDER(0x4) | SWP_NOACTIVATE(0x10): size only, keep position.
        [void][Shiyu.Probe.Native]::SetWindowPos($hwnd, [IntPtr]::Zero, 0, 0,
            [int][Math]::Round($dipWidth * $rect.Scale),
            [int][Math]::Round(640 * $rect.Scale), 0x14)
        Start-Sleep -Milliseconds 900

        $win2 = $null
        foreach ($w in (Get-UiaWindowsOfPid $p.Id)) {
            $r2 = $w.Current.BoundingRectangle
            if ([Math]::Abs($r2.Width - $dipWidth * $rect.Scale) -le 20) { $win2 = $w; break }
        }
        if (-not $win2) {
            $widthFailures += ("width {0}: window not found after resize" -f $dipWidth)
            continue
        }

        $tree2 = Get-UiaTree $win2
        $all = @($tree2 | Where-Object {
            ($_.Type -eq 'Button' -or $_.Type -eq 'ComboBox' -or $_.Type -eq 'Edit' `
                -or $_.Type -eq 'CheckBox' -or $_.Type -eq 'Calendar' -or $_.Type -eq 'ListItem') `
            -and $_.W -ge 20 -and $_.H -ge 14 })

        for ($i = 0; $i -lt $all.Count; $i++) {
            for ($j = $i + 1; $j -lt $all.Count; $j++) {
                $a = $all[$i]; $b = $all[$j]
                $inside = ($a.L -le $b.L -and $a.L + $a.W -ge $b.L + $b.W -and $a.T -le $b.T -and $a.T + $a.H -ge $b.T + $b.H) `
                       -or ($b.L -le $a.L -and $b.L + $b.W -ge $a.L + $a.W -and $b.T -le $a.T -and $b.T + $b.H -ge $a.T + $a.H)
                if ($inside) { continue }
                $ox = [Math]::Min($a.L + $a.W, $b.L + $b.W) - [Math]::Max($a.L, $b.L)
                $oy = [Math]::Min($a.T + $a.H, $b.T + $b.H) - [Math]::Max($a.T, $b.T)
                if ($ox -gt 3 -and $oy -gt 3) {
                    $widthFailures += ("width {0}: {1},{2} {3}x{4} vs {5},{6} {7}x{8}" -f `
                        $dipWidth, $a.L, $a.T, $a.W, $a.H, $b.L, $b.T, $b.W, $b.H)
                }
            }
        }
    }

    $sweepDetail = ("swept 720/940/1400 DIP; {0} overlapping pair(s)" -f $widthFailures.Count)
    if ($widthSkipped.Count -gt 0) {
        $sweepDetail += ("; not reachable on this {0}-pixel-wide screen: {1} DIP" -f $workWidth, ($widthSkipped -join '/'))
    }
    if ($widthFailures.Count -gt 0) {
        $sweepDetail += '; ' + ($widthFailures[0..([Math]::Min(2, $widthFailures.Count) - 1)] -join ' | ')
        Add-Check 'defect:library-width-sweep-overlap' 'FAIL' $sweepDetail
    } else {
        Add-Check 'defect:library-width-sweep-overlap' 'PASS' $sweepDetail
    }

    # Ticket 24 acceptance smoke: the keyboard model (section 5.2) wired end
    # to end - arrow selects, D deletes (undo toast with 5s window), Z
    # restores. The count label tells the story: 17 -> 16 -> 17.
    $VK_DOWN = 0x28; $VK_D = 0x44; $VK_Z = 0x5A
    $flow = @()

    # Back to the default width so the layout under test is the shipped one.
    # No SWP_NOACTIVATE here on purpose: posted keys only route through WPF
    # when the window owns the keyboard focus, and after the resize sweep the
    # probe console holds it. Sizing (without NOACTIVATE) activates the
    # window - the probe instance is ours to activate.
    [void][Shiyu.Probe.Native]::SetWindowPos($hwnd, [IntPtr]::Zero, 0, 0,
        [int][Math]::Round(1100 * $rect.Scale), [int][Math]::Round(640 * $rect.Scale), 0x4)
    Start-Sleep -Milliseconds 900

    function Get-LibraryCountText {
        $win3 = $null
        foreach ($w in (Get-UiaWindowsOfPid $p.Id)) {
            $r3 = $w.Current.BoundingRectangle
            if ([Math]::Abs($r3.Width - 1100 * $rect.Scale) -le 20) { $win3 = $w; break }
        }
        if (-not $win3) { return '' }
        $hits = @(Get-UiaTree $win3 | Where-Object { $_.Name -match '^\d+ / \d+$' })
        if ($hits.Count -eq 0) { return '' }
        return $hits[0].Name
    }

    $countBefore = Get-LibraryCountText

    Send-ProbeKey $hwnd $VK_DOWN -Extended
    Start-Sleep -Milliseconds 120
    Send-ProbeKey $hwnd $VK_DOWN -Extended -KeyUp
    Start-Sleep -Milliseconds 500
    if ((Get-LibraryCountText) -ne $countBefore) { $flow += 'arrow changed the count unexpectedly' }

    Send-ProbeKey $hwnd $VK_D
    Start-Sleep -Milliseconds 120
    Send-ProbeKey $hwnd $VK_D -KeyUp
    Start-Sleep -Milliseconds 700
    $countAfterDelete = Get-LibraryCountText
    if ($countAfterDelete -eq $countBefore) { $flow += ("D did not delete (count still {0})" -f $countAfterDelete) }

    Send-ProbeKey $hwnd $VK_Z
    Start-Sleep -Milliseconds 120
    Send-ProbeKey $hwnd $VK_Z -KeyUp
    Start-Sleep -Milliseconds 900
    $countAfterUndo = Get-LibraryCountText
    if ($countAfterUndo -ne $countBefore) { $flow += ("Z did not restore (count {0}, want {1})" -f $countAfterUndo, $countBefore) }

    $flowDetail = ("arrows/D/Z: count {0} -> {1} -> {2}" -f $countBefore, $countAfterDelete, $countAfterUndo)
    if ($flow.Count -gt 0) {
        $flowDetail += '; ' + ($flow -join ' | ')
        Add-Check 'keyboard:library-delete-undo' 'FAIL' $flowDetail
    } else {
        Add-Check 'keyboard:library-delete-undo' 'PASS' $flowDetail
    }
} finally {
    Stop-ProbeApp $p
}

return $script:Checks
