# probe-panel-dictionary.ps1 - English words are looked up in the offline dictionary that
# ships with Shiyu (review 2026-10-16 F-08): Chinese senses, phonetic and exam tags, at
# once and without network.
#
# The DEBUG command "panel-dictionary" (with SHIYU_FAKE_BACKEND=1 for a deterministic
# translation and SHIYU_LOCAL_DICTIONARY=1 so the panel uses the real offline dictionary,
# unknown words falling to the fake one - nothing touches the network) first times two
# raw lookups, then opens the panel on "example" and on "went" and reads the card.
#
# Checks:
#   - flow:dictionary-offline-fast  the shipped dictionary is found next to the exe and a
#                                   warm lookup takes well under 50 ms
#   - flow:dictionary-card-example  "example" shows its IPA, a Chinese sense and exam tags
#   - flow:dictionary-card-inflection  "went" shows go's card and says it is a form of go
#
# ASCII only (see lib.ps1 header).

param([string]$Exe = '')

. (Join-Path $PSScriptRoot 'lib.ps1')

if ($Exe -eq '') { $Exe = Get-DefaultProbeExe }
Reset-Checks

$dataDir = Join-Path $env:TEMP 'shiyu-probe-run\panel-dictionary'
& (Join-Path $PSScriptRoot 'seed.ps1') -DataDir $dataDir -AppBin (Split-Path -Parent $Exe) | Out-Null
$log = Join-Path $dataDir 'panel-dictionary.log'
Remove-Item $log -ErrorAction SilentlyContinue

$p = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd 'panel-dictionary' -ExtraEnv @{ SHIYU_FAKE_BACKEND = '1'; SHIYU_LOCAL_DICTIONARY = '1' }
try {
    $deadline = (Get-Date).AddSeconds(60)
    while (-not (Test-Path $log) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
} finally {
    if ($p) { Stop-ProbeApp $p }
}

if (-not (Test-Path $log)) {
    Add-Check 'flow:dictionary-offline-fast' 'FAIL' 'the command wrote no log within 60s'
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

$lookup = $lines['lookup']
$ok = $lookup -and $lookup['cold'] -eq 'True' -and $lookup['warm'] -eq 'True' -and [double]$lookup['warmMs'] -lt 50
$detail = if ($lookup) { "found {0}/{1}; first lookup {2} ms (opens the file), warm lookup {3} ms" -f $lookup['cold'], $lookup['warm'], $lookup['coldMs'], $lookup['warmMs'] } else { 'no lookup line' }
Add-Check 'flow:dictionary-offline-fast' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

foreach ($pair in @(@('example', 'flow:dictionary-card-example'), @('went', 'flow:dictionary-card-inflection'))) {
    $card = $lines[$pair[0]]
    $ok = $card -and $card['card'] -eq 'True' -and $card['phonetic'] -eq 'True' -and $card['meaning'] -eq 'True' -and $card['meta'] -eq 'True'
    $detail = if ($card) { "card shown {0}, phonetic {1}, Chinese sense {2}, tags/inflection note {3}" -f $card['card'], $card['phonetic'], $card['meaning'], $card['meta'] } else { "no line for $($pair[0])" }
    Add-Check $pair[1] $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail
}

return $script:Checks
