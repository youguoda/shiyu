# probe-panel-speech.ps1 - reading aloud uses the WinRT (OneCore / natural) voices when the
# language has one, and the read-aloud button turns into "stop" while it speaks
# (review 2026-10-16 F-10).
#
# The DEBUG command "panel-speech" (with SHIYU_FAKE_BACKEND=1) MUTES the speech service
# first - it synthesizes and plays at volume 0, so nothing is heard on the machine - then
# opens the panel on an English sentence and clicks "read aloud". The fake backend answers
# "[EN] " + the original, which the panel takes for an echo and turns around: the text read
# aloud is English. The log names the language that was actually read.
#
# Checks:
#   - flow:speech-winrt-voice   the voice came from WinRT ("winrt:<name>"), not SAPI - when this
#                               machine has a WinRT voice for the language read. Without one,
#                               falling back to SAPI is the designed behaviour: SKIP, with the
#                               reason (2026-10-21: a machine with only Chinese OneCore voices).
#   - flow:speech-button-stop   the button shows "stop" while speaking and turns back after
#
# ASCII only (see lib.ps1 header).

param([string]$Exe = '')

. (Join-Path $PSScriptRoot 'lib.ps1')

if ($Exe -eq '') { $Exe = Get-DefaultProbeExe }
Reset-Checks

$dataDir = Join-Path $env:TEMP 'shiyu-probe-run\panel-speech'
& (Join-Path $PSScriptRoot 'seed.ps1') -DataDir $dataDir -AppBin (Split-Path -Parent $Exe) | Out-Null
$log = Join-Path $dataDir 'panel-speech.log'
Remove-Item $log -ErrorAction SilentlyContinue

$p = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd 'panel-speech' -ExtraEnv @{ SHIYU_FAKE_BACKEND = '1' }
try {
    $deadline = (Get-Date).AddSeconds(60)
    while (-not (Test-Path $log) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
} finally {
    if ($p) { Stop-ProbeApp $p }
}

if (-not (Test-Path $log)) {
    Add-Check 'flow:speech-winrt-voice' 'FAIL' 'the command wrote no log within 60s'
    return $script:Checks
}

$speech = $null
foreach ($line in Get-Content $log -Encoding UTF8) {
    if ($line -match '^speech\|(.*)$') {
        $speech = @{}
        foreach ($pair in $Matches[1].Split('|')) {
            $kv = $pair.Split('=', 2)
            if ($kv.Count -eq 2) { $speech[$kv[0]] = $kv[1] }
        }
    }
}

# Which languages this machine's WinRT synthesizer can speak, by two-letter prefix.
$prefixes = @{ 'Chinese' = 'zh'; 'English' = 'en'; 'Japanese' = 'ja'; 'Korean' = 'ko'; 'Russian' = 'ru' }
$winRtLanguages = @()
try {
    $synth = [Windows.Media.SpeechSynthesis.SpeechSynthesizer, Windows.Media.SpeechSynthesis, ContentType = WindowsRuntime]
    $winRtLanguages = @($synth::AllVoices | ForEach-Object { $_.Language.Split('-')[0].ToLowerInvariant() } | Select-Object -Unique)
} catch {
    $winRtLanguages = @()
}

if (-not $speech) {
    Add-Check 'flow:speech-winrt-voice' 'FAIL' 'no speech line'
} else {
    $target = $speech['target']
    $prefix = $prefixes[$target]
    $detail = "voice used: {0} (language read: {1}; WinRT voices here: {2})" -f $speech['voice'], $target, ($winRtLanguages -join ',')
    if ($speech['voice'] -like 'winrt:*') {
        Add-Check 'flow:speech-winrt-voice' 'PASS' $detail
    } elseif ($prefix -and $winRtLanguages -notcontains $prefix) {
        Add-Check 'flow:speech-winrt-voice' 'SKIP' ("no WinRT voice for {0} on this machine - SAPI is the designed fallback; {1}" -f $target, $detail)
    } else {
        Add-Check 'flow:speech-winrt-voice' 'FAIL' $detail
    }
}

$ok = $speech -and $speech['stopShown'] -eq 'True' -and $speech['backAfter'] -eq 'True'
$detail = if ($speech) { "button showed stop while speaking {0}, back to read-aloud after {1}" -f $speech['stopShown'], $speech['backAfter'] } else { 'no speech line' }
Add-Check 'flow:speech-button-stop' $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail

return $script:Checks
