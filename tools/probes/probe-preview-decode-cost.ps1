# probe-preview-decode-cost.ps1 - first-show decode cost of a 3840x2160
# screenshot, cold vs warm (WIC has no low-res PNG decode: DecodePixelWidth
# still decompresses everything, so a 4K screenshot costs a full decode the
# first time an entry is previewed; the 4-entry original cache makes the
# second look free).
#
# The DEBUG command "preview-decode-cost" appends a 4K image entry (thumbnail
# + original), shows it cold (measures until the original lands), hides, shows
# again (the KeptOriginal cache answers synchronously). Input-priority pings
# run through the cold decode: the decode belongs on the thread pool.
#
# Checks (perf:* - green expected, red = new finding):
#   perf:decode-cold-4k     cold first show lands the original within 2500 ms
#   perf:decode-warm-cache  the cached second show answers within 200 ms
#   perf:decode-off-thread  input pings stay <= 150 ms through the cold decode
#
# ASCII only (see lib.ps1 header).

param([string]$Exe = '')

. (Join-Path $PSScriptRoot 'lib.ps1')

if ($Exe -eq '') { $Exe = Get-DefaultProbeExe }
Reset-Checks

$dataDir = Join-Path $env:TEMP 'shiyu-probe-run\preview-decode-cost'
& (Join-Path $PSScriptRoot 'seed.ps1') -DataDir $dataDir -AppBin (Split-Path -Parent $Exe) | Out-Null
$log = Join-Path $dataDir 'preview-decode-cost.log'
Remove-Item $log -ErrorAction SilentlyContinue

$p = Start-ProbeApp -Exe $Exe -DataDir $dataDir -Cmd 'preview-decode-cost' -ExtraEnv @{ SHIYU_FAKE_BACKEND = '1' }
try {
    $deadline = (Get-Date).AddSeconds(90)
    while (-not (Test-Path $log) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
    while ((Get-Date) -lt $deadline -and (Test-Path $log) -and (@(Select-String -Path $log -Pattern '^done').Count -eq 0)) {
        Start-Sleep -Milliseconds 500
    }
} finally {
    if ($p) { Stop-ProbeApp $p }
}

if (-not (Test-Path $log)) {
    Add-Check 'perf:decode-cold-4k' 'FAIL' 'the command wrote no log within 90s'
    Add-Check 'perf:decode-warm-cache' 'FAIL' 'the command wrote no log within 90s'
    Add-Check 'perf:decode-off-thread' 'FAIL' 'the command wrote no log within 90s'
    return $script:Checks
}

$decode = $null
foreach ($line in Get-Content $log -Encoding UTF8) {
    if ($line -match '^decode\|(.*)$') {
        $fields = @{}
        foreach ($pair in $Matches[1].Split('|')) {
            $kv = $pair.Split('=', 2)
            if ($kv.Count -eq 2) { $fields[$kv[0]] = $kv[1] }
        }
        $decode = $fields
    }
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

if ($null -eq $decode) {
    Add-Check 'perf:decode-cold-4k' 'FAIL' 'no decode line (see preview-decode-cost.log)'
    Add-Check 'perf:decode-warm-cache' 'FAIL' 'no decode line'
    Add-Check 'perf:decode-off-thread' 'FAIL' 'no decode line'
    return $script:Checks
}

$cold = ConvertTo-InvariantDouble (Field $decode 'coldMs' '-1')
$landed = (Field $decode 'landed' '?') -eq 'True'
$ok = $landed -and ($cold -ge 0) -and ($cold -le 2500)
Add-Check 'perf:decode-cold-4k' $(if ($ok) { 'PASS' } else { 'FAIL' }) "cold 4K first show: coldMs=$cold landed=$landed px=$(Field $decode 'px' '?')"

$warm = ConvertTo-InvariantDouble (Field $decode 'warmMs' '-1')
$warmLanded = (Field $decode 'warmLanded' '?') -eq 'True'
$ok = $warmLanded -and ($warm -ge 0) -and ($warm -le 200)
Add-Check 'perf:decode-warm-cache' $(if ($ok) { 'PASS' } else { 'FAIL' }) "warm (cached) second show: warmMs=$warm warmLanded=$warmLanded"

$inputMax = ConvertTo-InvariantDouble (Field $decode 'inputMaxMs' '-1')
$ok = ($inputMax -ge 0) -and ($inputMax -le 150)
Add-Check 'perf:decode-off-thread' $(if ($ok) { 'PASS' } else { 'FAIL' }) "input pings through the cold decode: inputMaxMs=$inputMax"

return $script:Checks
