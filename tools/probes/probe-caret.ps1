# probe-caret.ps1 - the text caret starts where the placeholder and the typed text
# start, and it is drawn in the accent colour (user report 2026-10-05: in an empty
# search box the caret blinked between the first two placeholder characters, and a
# 1 px dark caret over grey placeholder text was hard to see).
#
# Root cause: WPF hands a TextBox's Padding to its PART_ContentHost at runtime
# (overriding any local value in the template) and insets the text a further 2 DIP.
# Templates that ALSO laid the content host out with Padding indented the text a
# second time - 12 DIP past the placeholder, which did not get the second share.
#
# The DEBUG command "caret" opens the bar, the library, settings on the hotkeys page
# (empty boxes show a placeholder there) and a sample window (password, number and
# multi-line boxes), then logs every box into <data dir>\caret.log.
#
# Checks:
#   - data:caret-boxes             the search boxes, a placeholder box and the samples were measured
#   - flow:caret-at-placeholder    every empty box: the caret sits at the placeholder's first character
#   - flow:padding-applied-once    every box on the shared templates: text starts at border + padding + 2
#   - flow:caret-is-accent         every box draws its caret with the accent brush
#
# ASCII only (see lib.ps1 header).

param([string]$Exe = '')

. (Join-Path $PSScriptRoot 'lib.ps1')

if ($Exe -eq '') { $Exe = Get-DefaultProbeExe }
Reset-Checks

$dataDir = Join-Path $env:TEMP 'shiyu-probe-run\caret'
& (Join-Path $PSScriptRoot 'seed.ps1') -DataDir $dataDir -AppBin (Split-Path -Parent $Exe) | Out-Null
$log = Join-Path $dataDir 'caret.log'
Remove-Item $log -ErrorAction SilentlyContinue

$p = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd 'caret'
try {
    $deadline = (Get-Date).AddSeconds(40)
    while (-not (Test-Path $log) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
} finally {
    if ($p) { Stop-ProbeApp $p }
}

if (-not (Test-Path $log)) {
    Add-Check 'data:caret-boxes' 'FAIL' 'the command wrote no log within 40s'
    return $script:Checks
}

# box|<window>|<kind>|empty=<bool>|caret=<x>,<y>|placeholder=<x>,<y>|origin=<x>|expect=<x>|accent=<bool>
$boxes = @()
foreach ($line in Get-Content $log) {
    if ($line -match '^box\|([^|]+)\|([^|]+)\|empty=(\w+)\|caret=([^,]+),([^|]+)\|placeholder=([^,]+),([^|]+)\|origin=([^|]+)\|expect=([^|]+)\|accent=(\w+)$') {
        $inv = [Globalization.CultureInfo]::InvariantCulture
        $num = { param($t) if ($t -eq 'NaN') { [double]::NaN } else { [double]::Parse($t, $inv) } }
        $boxes += [pscustomobject]@{
            Window = $Matches[1]; Kind = $Matches[2]; Empty = ($Matches[3] -eq 'True')
            CaretX = & $num $Matches[4]; CaretY = & $num $Matches[5]
            HintX = & $num $Matches[6]; HintY = & $num $Matches[7]
            Origin = & $num $Matches[8]; Expect = & $num $Matches[9]
            Accent = ($Matches[10] -eq 'True') }
    }
}

$search = @($boxes | Where-Object Kind -eq 'SearchBox').Count
$emptyText = @($boxes | Where-Object { $_.Kind -eq 'TextBox' -and $_.Empty }).Count
$number = @($boxes | Where-Object Kind -eq 'NumberBox').Count
$password = @($boxes | Where-Object Kind -eq 'PasswordBox').Count
$found = "{0} boxes: search {1}, empty text {2}, number {3}, password {4}" -f $boxes.Count, $search, $emptyText, $number, $password
if ($search -ge 2 -and $emptyText -ge 1 -and $number -ge 1 -and $password -ge 1) {
    Add-Check 'data:caret-boxes' 'PASS' $found
} else {
    Add-Check 'data:caret-boxes' 'FAIL' $found
    return $script:Checks
}

$empties = @($boxes | Where-Object Empty)
$off = @($empties | Where-Object { [Math]::Abs($_.CaretX - $_.HintX) -gt 0.5 -or [Math]::Abs($_.CaretY - $_.HintY) -gt 1.0 })
$worst = ($empties | ForEach-Object { [Math]::Abs($_.CaretX - $_.HintX) } | Measure-Object -Maximum).Maximum
Add-Check 'flow:caret-at-placeholder' $(if ($off.Count -eq 0) { 'PASS' } else { 'FAIL' }) `
    ("{0} empty box(es), {1} misaligned; worst horizontal gap {2:N1} DIP" -f $empties.Count, $off.Count, $worst)

$shared = @($boxes | Where-Object Kind -ne 'SearchBox')
$double = @($shared | Where-Object { [Math]::Abs($_.Origin - $_.Expect) -gt 0.5 })
$detail = "{0} box(es) on the shared templates, {1} indented past border + padding + 2" -f $shared.Count, $double.Count
if ($double.Count -gt 0) {
    $detail += ("; e.g. {0} {1}: text at {2:N1}, expected {3:N1}" -f $double[0].Window, $double[0].Kind, $double[0].Origin, $double[0].Expect)
}
Add-Check 'flow:padding-applied-once' $(if ($double.Count -eq 0) { 'PASS' } else { 'FAIL' }) $detail

$dark = @($boxes | Where-Object { -not $_.Accent })
Add-Check 'flow:caret-is-accent' $(if ($dark.Count -eq 0) { 'PASS' } else { 'FAIL' }) `
    ("{0} of {1} box(es) draw a non-accent caret" -f $dark.Count, $boxes.Count)

return $script:Checks
