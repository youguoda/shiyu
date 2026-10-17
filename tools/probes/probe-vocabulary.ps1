# probe-vocabulary.ps1 - the vocabulary book (review 2026-10-16 F-09; acceptance delegated
# by the user on 2026-10-17): starring three words puts exactly those three into the
# vocabulary filter, the library's toggle shows three, and the exported CSV carries the
# Anki header and the three headwords.
#
# The DEBUG command "vocabulary" stars example / went / ubiquitous through the real
# offline dictionary cards, drives the library's vocabulary toggle, and writes
# vocabulary.csv next to its log. Nothing touches the clipboard.
#
# Checks:
#   - flow:vocabulary-filter      the store's vocabulary filter holds exactly the 3 words
#   - flow:vocabulary-toggle      the library's toggle lists exactly those 3
#   - flow:vocabulary-csv-export  the CSV has the Anki header and all three headwords
#
# ASCII only (see lib.ps1 header).

param([string]$Exe = '')

. (Join-Path $PSScriptRoot 'lib.ps1')

if ($Exe -eq '') { $Exe = Get-DefaultProbeExe }
Reset-Checks

$dataDir = Join-Path $env:TEMP 'shiyu-probe-run\vocabulary'
& (Join-Path $PSScriptRoot 'seed.ps1') -DataDir $dataDir -AppBin (Split-Path -Parent $Exe) | Out-Null
$log = Join-Path $dataDir 'vocabulary.log'
Remove-Item $log -ErrorAction SilentlyContinue

$p = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd 'vocabulary'
try {
    $deadline = (Get-Date).AddSeconds(60)
    while (-not (Test-Path $log) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
} finally {
    if ($p) { Stop-ProbeApp $p }
}

if (-not (Test-Path $log)) {
    Add-Check 'flow:vocabulary-filter' 'FAIL' 'the command wrote no log within 60s'
    return $script:Checks
}

$lines = @{}
foreach ($line in Get-Content $log -Encoding UTF8) {
    if ($line -match '^(\w+)\|(.*)$') {
        $fields = @{}
        foreach ($pair in $Matches[2].Split('|')) {
            $kv = $pair.Split('=', 2)
            if ($kv.Count -eq 2) { $fields[$kv[0]] = $kv[1] }
        }
        $lines[$Matches[1]] = $fields
    }
}

if ($lines['error']) {
    Add-Check 'flow:vocabulary-filter' 'FAIL' ("the command threw {0}" -f $lines['error']['type'])
    return $script:Checks
}

$filter = $lines['filter']
$ok = $filter -and $filter['count'] -eq '3' -and $filter['headwords'] -match 'example' -and $filter['headwords'] -match '\bgo\b' -and $filter['headwords'] -match 'ubiquitous'
$detail = if ($filter) { "vocabulary holds {0}: {1}" -f $filter['count'], $filter['headwords'] } else { 'no filter line' }
Add-Check 'flow:vocabulary-filter' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

$toggle = $lines['toggle']
$ok = $toggle -and $toggle['count'] -eq '3'
$detail = if ($toggle) { "the library's vocabulary toggle lists {0} item(s)" -f $toggle['count'] } else { 'no toggle line' }
Add-Check 'flow:vocabulary-toggle' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

$csv = $lines['csv']
$ok = $csv -and $csv['written'] -eq '3' -and $csv['header'] -eq 'True' -and $csv['example'] -eq 'True' -and $csv['go'] -eq 'True' -and $csv['ubiquitous'] -eq 'True'
$detail = if ($csv) { "CSV rows {0}, Anki header {1}, headwords example {2} / go {3} / ubiquitous {4}" -f $csv['written'], $csv['header'], $csv['example'], $csv['go'], $csv['ubiquitous'] } else { 'no csv line' }
Add-Check 'flow:vocabulary-csv-export' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

return $script:Checks
