# probe-markdown.ps1 - LLM output shows as Markdown (user request 2026-10-10,
# ADR-0014) in the translation panel and the reverse input box.
#
# The DEBUG command "markdown" (with SHIYU_FAKE_BACKEND=1) lets the fake
# backend settle one sentence in the panel, then lays a sample with every
# block kind into that same translation box, and again into the reverse box.
# It logs what the FlowDocument holds and renders markdown-panel.png and
# markdown-reverse.png into the data dir.
#
# Checks:
#   - data:markdown-log                 the command walked every step
#   - flow:markdown-blocks:<host>       2 headings (bigger, emphasis weight), 3 lists,
#                                       1 table of 2 rows, 1 quote, 1 rule, 1 code block
#   - flow:markdown-no-markers:<host>   no raw ** ``` |:--- - [x] ~~ "## " left on screen
#   - flow:markdown-line-break:<host>   a single newline stays a line break
#   - flow:markdown-links:<host>        2 links; only the https one can open
#   - flow:markdown-caret               the streaming caret shows, and goes on settle
#   - flow:markdown-keeps-selection     the same text again keeps the user's selection
#
# ASCII only (see lib.ps1 header).

param([string]$Exe = '')

. (Join-Path $PSScriptRoot 'lib.ps1')

if ($Exe -eq '') { $Exe = Get-DefaultProbeExe }
Reset-Checks

$dataDir = Join-Path $env:TEMP 'shiyu-probe-run\markdown'
& (Join-Path $PSScriptRoot 'seed.ps1') -DataDir $dataDir | Out-Null
$log = Join-Path $dataDir 'markdown.log'
Remove-Item $log -ErrorAction SilentlyContinue

$p = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd 'markdown' -ExtraEnv @{ SHIYU_FAKE_BACKEND = '1' }
try {
    $deadline = (Get-Date).AddSeconds(45)
    while ((Get-Date) -lt $deadline) {
        if ((Test-Path $log) -and ((Get-Content $log -Raw -Encoding UTF8) -match '(?m)^done\r?$')) { break }
        Start-Sleep -Milliseconds 300
    }
} finally {
    if ($p) { Stop-ProbeApp $p }
}

$steps = @{}
if (Test-Path $log) {
    foreach ($line in Get-Content $log -Encoding UTF8) {
        $parts = $line.Split('|')
        $fields = @{}
        foreach ($pair in $parts | Select-Object -Skip 1) {
            $kv = $pair.Split('=', 2)
            if ($kv.Count -eq 2) { $fields[$kv[0]] = $kv[1] }
        }
        $steps[$parts[0]] = $fields
    }
}

$expected = @('panel', 'caret', 'selection', 'reverse', 'done')
$missing = @($expected | Where-Object { -not $steps.ContainsKey($_) -or $steps[$_].Count -eq 0 -and $_ -ne 'done' })
if ($missing.Count -gt 0) {
    Add-Check 'data:markdown-log' 'FAIL' ("log lacks: {0}" -f ($missing -join ', '))
    return $script:Checks
}
Add-Check 'data:markdown-log' 'PASS' ("{0} steps logged" -f ($expected.Count - 1))

function Verdict([bool]$ok) { if ($ok) { 'PASS' } else { 'FAIL' } }
function Num([string]$text) { [double]::Parse($text, [Globalization.CultureInfo]::InvariantCulture) }

foreach ($host_ in 'panel', 'reverse') {
    $s = $steps[$host_]
    $ok = $s['headings'] -eq '2' -and $s['lists'] -eq '3' -and $s['tables'] -eq '1' -and $s['rows'] -eq '2' `
        -and $s['quotes'] -eq '1' -and $s['rules'] -eq '1' -and $s['code'] -eq '1' `
        -and (Num $s['h1']) -gt (Num $s['body'])
    Add-Check "flow:markdown-blocks:$host_" (Verdict $ok) `
        ("headings {0} (h1 {1} over body {2}), lists {3}, tables {4} x{5} rows, quotes {6}, rules {7}, code {8}" -f `
            $s['headings'], $s['h1'], $s['body'], $s['lists'], $s['tables'], $s['rows'], $s['quotes'], $s['rules'], $s['code'])
    Add-Check "flow:markdown-no-markers:$host_" (Verdict ($s['markers'] -eq '0')) ("raw markers left: {0}" -f $s['markers'])
    Add-Check "flow:markdown-line-break:$host_" (Verdict ($s['linebreak'] -eq 'True')) ("line break kept: {0}" -f $s['linebreak'])
    Add-Check "flow:markdown-links:$host_" (Verdict ($s['links'] -eq '2' -and $s['openable'] -eq '1')) `
        ("{0} links, {1} can open" -f $s['links'], $s['openable'])
}

$c = $steps['caret']
Add-Check 'flow:markdown-caret' (Verdict ($c['streaming'] -eq '1' -and $c['settled'] -eq '0')) `
    ("carets while streaming {0}, after settling {1}" -f $c['streaming'], $c['settled'])

$sel = $steps['selection']
Add-Check 'flow:markdown-keeps-selection' (Verdict ($sel['before'].Length -gt 0 -and $sel['before'] -eq $sel['after'])) `
    ("selection before '{0}', after the same text again '{1}'" -f $sel['before'], $sel['after'])

return $script:Checks
