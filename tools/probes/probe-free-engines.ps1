# probe-free-engines.ps1 - network probe for the free translation engines
# (ticket 41, ADR-0013).
#
# The free engine talks to two UNOFFICIAL web interfaces: the Bing translator
# page (+ POST /ttranslatev3) and Tencent TranSmart (POST /api/imt). Nothing
# promises they keep their shape (edge.microsoft.com/translate/auth was
# switched off in 2026-07 with no notice), so this probe is how a release finds
# out first - not a user. Release checklist: run it before tagging.
#
# What it checks, per route (direct = no proxy; system proxy = the default
# .NET handler, i.e. whatever Windows / HTTP(S)_PROXY says - the two routes
# really do give different answers on a machine with a local proxy):
#   bing:session          GET the page, parse key/token/ttl (ms), IG and the first
#                         data-iid, note the host the redirect ended on
#   bing:<from>-><to>     one translation per target language (zh->en is the
#                         "Chinese to English" check, the rest cover the code table)
#   tencent:<from>-><to>  same for TranSmart; the first one sends source "auto"
#   info:tencent-auto     does TranSmart accept source language "auto"?
#   info:tencent-empty    does an empty line in text_list keep the array length?
# Every translation is also sanity-checked by script (kana for ja, hangul for
# ko, ...), so "200 OK with the wrong language" does not pass.
#
# The code table below is a copy of FreeEngineLanguages in Shiyu.Core - keep
# the two in step (a unit test pins the C# side).
#
# Pure PowerShell: never starts Shiyu, never reads its data. Offline (no network
# interface up) => SKIP, exit 0. Any FAIL => exit 1.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\probes\probe-free-engines.ps1
#   ... -Route Direct                  # the release-checklist run
#   ... -Route SystemProxy -Quick      # session + one translation per engine (3 requests)
#   ... -SessionOnly -SavePage out.html  # one GET; keeps the page (HTML fixture source)
#
# A full run costs 17 requests per route (18 if TranSmart rejects "auto"):
# these are other people's servers, so do not loop it.
#
# ASCII ONLY (same rule as lib.ps1): PowerShell 5 reads un-BOM'd non-ASCII
# as ANSI. Chinese test text is built from code points at runtime.

param(
    [ValidateSet('Direct', 'SystemProxy', 'Both')]
    [string]$Route = 'Both',

    # Session + one translation per engine only. For the proxy route, or a quick smoke.
    [switch]$Quick,

    # Target languages to cover (names from the code table below).
    [string[]]$Languages = @('en', 'zh', 'ja', 'ko', 'ru', 'fr', 'de', 'es'),

    # Fetch and parse the Bing page only (1 request).
    [switch]$SessionOnly,

    # Write the raw Bing page here (fixture source; contains a live token -
    # never commit it as is).
    [string]$SavePage = '',

    # Test seams: point the probe at a local fake. Overriding BingBase also
    # turns off the "host must be *.bing.com" guard.
    [string]$BingBase = 'https://cn.bing.com',
    [string]$TransmartBase = 'https://transmart.qq.com'
)

$ErrorActionPreference = 'Stop'

# `powershell -File ... -Languages en,zh` hands over ONE string "en,zh"; split it.
$Languages = @($Languages | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
Add-Type -AssemblyName System.Net.Http
try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch { }

# --- fixtures -------------------------------------------------------------------
# Non-ASCII text is built from code points at runtime (this file stays ASCII).
function Chars([int[]]$codes) { -join ($codes | ForEach-Object { [string][char]$_ }) }
function Range([int]$low, [int]$high) { '[' + [char]$low + '-' + [char]$high + ']' }

# The colloquial sentence from the ticket: "wo xian che le ha, ming tian jian".
$ZhLine1 = Chars 0x6211, 0x5148, 0x64a4, 0x4e86, 0x54c8
$ZhLine2 = Chars 0x660e, 0x5929, 0x89c1
$ZhSentence = $ZhLine1 + (Chars 0xff0c) + $ZhLine2
$EnLine1 = 'I am heading out for now.'
$EnLine2 = 'See you tomorrow.'
$EnSentence = "$EnLine1 $EnLine2"

# Edge UA for the Bing leg, plain Chrome UA for TranSmart (same as the C# side).
$EdgeUa = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36 Edg/130.0.0.0'
$ChromeUa = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36'

# name -> (bing code, tencent code, script the translation must contain)
$Table = [ordered]@{
    'zh' = @{ Bing = 'zh-Hans'; Tencent = 'zh'; Script = (Range 0x4e00 0x9fff) }
    'en' = @{ Bing = 'en'; Tencent = 'en'; Script = '[A-Za-z]' }
    'ja' = @{ Bing = 'ja'; Tencent = 'ja'; Script = (Range 0x3040 0x30ff) }
    'ko' = @{ Bing = 'ko'; Tencent = 'ko'; Script = (Range 0xac00 0xd7a3) }
    'ru' = @{ Bing = 'ru'; Tencent = 'ru'; Script = (Range 0x0400 0x04ff) }
    'fr' = @{ Bing = 'fr'; Tencent = 'fr'; Script = '[A-Za-z]' }
    'de' = @{ Bing = 'de'; Tencent = 'de'; Script = '[A-Za-z]' }
    'es' = @{ Bing = 'es'; Tencent = 'es'; Script = '[A-Za-z]' }
}

# --- results ---------------------------------------------------------------------
$script:Pass = 0
$script:Fail = 0
$script:Skip = 0
$script:Requests = 0

function Emit([string]$verdict, [string]$name, [string]$detail, [long]$ms = -1) {
    $time = if ($ms -ge 0) { '{0,6} ms' -f $ms } else { '' }
    Write-Output ('{0,-5} {1,-26} {2}  {3}' -f $verdict, $name, $detail, $time)
    switch ($verdict) {
        'PASS' { $script:Pass++ }
        'FAIL' { $script:Fail++ }
        'SKIP' { $script:Skip++ }
    }
}

# --- http plumbing -----------------------------------------------------------------
function New-Client([bool]$useProxy) {
    $handler = New-Object System.Net.Http.HttpClientHandler
    $handler.UseProxy = $useProxy
    $handler.AllowAutoRedirect = $true
    $client = New-Object System.Net.Http.HttpClient($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(20)
    return $client
}

function Send-Request($Client, $Message) {
    $script:Requests++
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $response = $Client.SendAsync($Message).GetAwaiter().GetResult()
        $bytes = $response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
        return [pscustomobject]@{
            Ok = $true
            Status = [int]$response.StatusCode
            Body = [System.Text.Encoding]::UTF8.GetString($bytes)
            Bytes = $bytes.Length
            FinalUri = $response.RequestMessage.RequestUri
            Ms = $watch.ElapsedMilliseconds
            Error = $null
        }
    }
    catch {
        return [pscustomobject]@{
            Ok = $false; Status = 0; Body = ''; Bytes = 0; FinalUri = $null
            Ms = $watch.ElapsedMilliseconds; Error = $_.Exception.GetBaseException().Message
        }
    }
    finally {
        $Message.Dispose()
    }
}

# --- bing --------------------------------------------------------------------------
function Get-BingSession($Client) {
    $message = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Get, "$BingBase/translator")
    $message.Headers.TryAddWithoutValidation('User-Agent', $EdgeUa) | Out-Null
    $res = Send-Request $Client $message

    if (-not $res.Ok) { return [pscustomobject]@{ Ok = $false; Detail = "request failed: $($res.Error)"; Ms = $res.Ms } }
    if ($res.Status -ne 200) { return [pscustomobject]@{ Ok = $false; Detail = "HTTP $($res.Status)"; Ms = $res.Ms } }

    if ($SavePage) {
        [System.IO.File]::WriteAllText($SavePage, $res.Body, (New-Object System.Text.UTF8Encoding($false)))
    }

    $abuse = [regex]::Match($res.Body, 'params_AbusePreventionHelper\s*=\s*\[\s*(\d+)\s*,\s*"([^"]+)"\s*,\s*(\d+)\s*\]')
    $ig = [regex]::Match($res.Body, 'IG\s*:\s*"([A-Fa-f0-9]+)"')
    $iids = [regex]::Matches($res.Body, 'data-iid\s*=\s*"([^"]+)"')
    if (-not $abuse.Success -or -not $ig.Success -or $iids.Count -eq 0) {
        $missing = @()
        if (-not $abuse.Success) { $missing += 'params_AbusePreventionHelper' }
        if (-not $ig.Success) { $missing += 'IG' }
        if ($iids.Count -eq 0) { $missing += 'data-iid' }
        return [pscustomobject]@{ Ok = $false; Detail = "page shape changed, missing: $($missing -join ', ')"; Ms = $res.Ms }
    }

    $hostName = if ($res.FinalUri) { $res.FinalUri.Host } else { '' }
    $translateBase =
        if ($BingBase -ne 'https://cn.bing.com') { $BingBase }
        elseif ($hostName -eq 'bing.com' -or $hostName.EndsWith('.bing.com')) { "https://$hostName" }
        else { 'https://cn.bing.com' }

    return [pscustomobject]@{
        Ok = $true
        Key = $abuse.Groups[1].Value
        Token = $abuse.Groups[2].Value
        TtlMs = [long]$abuse.Groups[3].Value
        IG = $ig.Groups[1].Value
        IID = $iids[0].Groups[1].Value
        IidCount = $iids.Count
        Host = $hostName
        TranslateBase = $translateBase
        Bytes = $res.Bytes
        Ms = $res.Ms
        Detail = ''
    }
}

function Invoke-Bing($Client, $Session, [string]$Text, [string]$From, [string]$To) {
    $uri = '{0}/ttranslatev3?isVertical=1&IG={1}&IID={2}' -f $Session.TranslateBase, $Session.IG, $Session.IID
    $message = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Post, $uri)
    $message.Headers.TryAddWithoutValidation('User-Agent', $EdgeUa) | Out-Null
    $message.Headers.TryAddWithoutValidation('Referer', "$($Session.TranslateBase)/translator") | Out-Null

    $pairs = New-Object 'System.Collections.Generic.List[System.Collections.Generic.KeyValuePair[string,string]]'
    foreach ($pair in @(@('fromLang', $From), @('text', $Text), @('to', $To), @('token', $Session.Token), @('key', $Session.Key))) {
        $pairs.Add([System.Collections.Generic.KeyValuePair[string,string]]::new($pair[0], $pair[1]))
    }
    # The leading comma stops PowerShell from unrolling the list into five constructor arguments.
    $message.Content = New-Object System.Net.Http.FormUrlEncodedContent(, $pairs)

    $res = Send-Request $Client $message
    if (-not $res.Ok) { return [pscustomobject]@{ Ok = $false; Detail = "request failed: $($res.Error)"; Ms = $res.Ms } }
    if ($res.Status -ne 200) { return [pscustomobject]@{ Ok = $false; Detail = "HTTP $($res.Status)"; Ms = $res.Ms } }

    # Failures come back as HTTP 200 with an object in the body (205 = token no longer valid).
    $trimmed = $res.Body.TrimStart()
    try { $json = ConvertFrom-Json -InputObject $res.Body } catch {
        return [pscustomobject]@{ Ok = $false; Detail = 'body is not JSON'; Ms = $res.Ms }
    }
    if (-not $trimmed.StartsWith('[')) {
        $code = if ($null -ne $json.statusCode) { $json.statusCode } else { '?' }
        return [pscustomobject]@{ Ok = $false; Detail = "statusCode=$code in the body (HTTP 200)"; Ms = $res.Ms }
    }
    $first = @($json)[0]
    $text = $first.translations[0].text
    if ($null -eq $text) { return [pscustomobject]@{ Ok = $false; Detail = 'no translations[0].text'; Ms = $res.Ms } }
    $detected = if ($first.detectedLanguage) { $first.detectedLanguage.language } else { '-' }
    return [pscustomobject]@{ Ok = $true; Text = [string]$text; Detected = $detected; UsedLlm = $first.usedLLM; Ms = $res.Ms; Detail = '' }
}

# --- tencent -------------------------------------------------------------------------
$ClientKey = 'browser-chrome-130.0.0-Windows_10-probe0000-' + [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()

function Invoke-Tencent($Client, [string[]]$Lines, [string]$From, [string]$To) {
    $message = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Post, "$TransmartBase/api/imt")
    $message.Headers.TryAddWithoutValidation('User-Agent', $ChromeUa) | Out-Null
    $message.Headers.TryAddWithoutValidation('Referer', 'https://transmart.qq.com/') | Out-Null
    $body = @{
        header = @{ fn = 'auto_translation'; client_key = $ClientKey }
        type = 'plain'
        model_category = 'normal'
        source = @{ lang = $From; text_list = [string[]]$Lines }
        target = @{ lang = $To }
    } | ConvertTo-Json -Depth 6 -Compress
    $message.Content = New-Object System.Net.Http.StringContent($body, [System.Text.Encoding]::UTF8, 'application/json')

    $res = Send-Request $Client $message
    if (-not $res.Ok) { return [pscustomobject]@{ Ok = $false; Detail = "request failed: $($res.Error)"; Ms = $res.Ms } }
    if ($res.Status -ne 200) { return [pscustomobject]@{ Ok = $false; Detail = "HTTP $($res.Status)"; Ms = $res.Ms } }
    try { $json = ConvertFrom-Json -InputObject $res.Body } catch {
        return [pscustomobject]@{ Ok = $false; Detail = 'body is not JSON'; Ms = $res.Ms }
    }
    if ($null -eq $json.auto_translation) {
        $ret = if ($json.header) { $json.header.ret_code } else { '?' }
        return [pscustomobject]@{ Ok = $false; Detail = "no auto_translation (header.ret_code=$ret)"; Ms = $res.Ms }
    }
    $out = [string[]]@($json.auto_translation)
    return [pscustomobject]@{ Ok = $true; Lines = $out; Ms = $res.Ms; Detail = '' }
}

# --- one route -------------------------------------------------------------------------
function Test-Script([string]$text, [string]$pattern, [string]$source) {
    return ($text.Length -gt 0) -and [regex]::IsMatch($text, $pattern) -and ($text -ne $source)
}

function Show([string]$text) {
    $flat = ($text -replace '\s+', ' ')
    if ($flat.Length -gt 48) { $flat = $flat.Substring(0, 48) + '...' }
    # The console may not render CJK; show code points for anything non-ASCII.
    return ($flat.ToCharArray() | ForEach-Object { if ([int]$_ -lt 128) { [string]$_ } else { '\u{0:x4}' -f [int]$_ } }) -join ''
}

function Invoke-Route([string]$label, [bool]$useProxy) {
    Write-Output ''
    Write-Output ("== route: {0} ({1})" -f $label, $(if ($useProxy) { 'default handler: system proxy / HTTP(S)_PROXY' } else { 'UseProxy = false' }))

    if (-not [System.Net.NetworkInformation.NetworkInterface]::GetIsNetworkAvailable()) {
        Emit 'SKIP' "net:$label" 'offline (no network interface is up)'
        return
    }

    $client = New-Client $useProxy
    try {
        $session = Get-BingSession $client
        if (-not $session.Ok) {
            Emit 'FAIL' 'bing:session' $session.Detail $session.Ms
        }
        else {
            $detail = 'host={0} ttl={1}ms page={2}KB iids={3}' -f $session.Host, $session.TtlMs, [int]($session.Bytes / 1024), $session.IidCount
            if ($session.TtlMs -le 60000) { Emit 'FAIL' 'bing:session' "$detail (ttl too short for the 60s safety margin)" $session.Ms }
            else { Emit 'PASS' 'bing:session' $detail $session.Ms }
        }
        if ($SessionOnly) { return }

        # Fail fast: two failures from one engine mean it is broken, not that every
        # language is - stop asking instead of hammering someone else's server.
        $bingFails = 0
        $tencentFails = 0
        $wanted = if ($Quick) { @('en') } else { @($Languages) }
        foreach ($name in $wanted) {
            if (-not $Table.Contains($name)) { Emit 'SKIP' "bing:->$name" 'not in the code table'; continue }
            if (-not $session.Ok) { Emit 'SKIP' "bing:->$name" 'no session'; continue }
            if ($bingFails -ge 2) { Emit 'SKIP' "bing:->$name" 'two failures already, not asking again'; continue }
            $row = $Table[$name]
            $fromName = if ($name -eq 'zh') { 'en' } else { 'zh' }
            $source = if ($name -eq 'zh') { $EnSentence } else { $ZhSentence }
            $result = Invoke-Bing $client $session $source $Table[$fromName].Bing $row.Bing
            $title = "bing:$fromName->$name"
            if (-not $result.Ok) { Emit 'FAIL' $title $result.Detail $result.Ms; $bingFails++ }
            elseif (-not (Test-Script $result.Text $row.Script $source)) { Emit 'FAIL' $title ("wrong script / echoed: " + (Show $result.Text)) $result.Ms; $bingFails++ }
            else { Emit 'PASS' $title ('"{0}" detected={1} llm={2}' -f (Show $result.Text), $result.Detected, $result.UsedLlm) $result.Ms }
        }

        $sawAuto = $false
        foreach ($name in $wanted) {
            if (-not $Table.Contains($name)) { Emit 'SKIP' "tencent:->$name" 'not in the code table'; continue }
            $row = $Table[$name]
            $fromName = if ($name -eq 'zh') { 'en' } else { 'zh' }
            $title = "tencent:$fromName->$name"
            if ($tencentFails -ge 2) { Emit 'SKIP' $title 'two failures already, not asking again'; continue }

            if ($name -eq 'zh' -and -not $Quick) {
                # English source with a blank line in the middle: does the array survive?
                $lines = [string[]]@($EnLine1, '', $EnLine2)
                $result = Invoke-Tencent $client $lines $Table[$fromName].Tencent $row.Tencent
                if (-not $result.Ok) { Emit 'FAIL' $title $result.Detail $result.Ms; $tencentFails++; continue }
                $aligned = ($result.Lines.Count -eq 3) -and ($result.Lines[1] -eq '')
                Emit 'PASS' 'info:tencent-empty' ('text_list in=3 out={0} blank-line-kept={1}' -f $result.Lines.Count, $aligned) $result.Ms
                $joined = $result.Lines -join "`n"
                if (-not (Test-Script $joined $row.Script ($lines -join "`n"))) { Emit 'FAIL' $title ('wrong script: ' + (Show $joined)) $result.Ms; $tencentFails++ }
                else { Emit 'PASS' $title ('"{0}"' -f (Show $joined)) $result.Ms }
                continue
            }

            $source = if ($name -eq 'zh') { $EnSentence } else { $ZhSentence }
            $from = $Table[$fromName].Tencent

            if ($name -eq 'en' -and -not $Quick) {
                # The first ordinary request doubles as the "auto" check.
                $result = Invoke-Tencent $client ([string[]]@($source)) 'auto' $row.Tencent
                if ($result.Ok) {
                    $sawAuto = $true
                    Emit 'PASS' 'info:tencent-auto' 'source "auto" accepted' $result.Ms
                }
                elseif ($result.Detail -like 'request failed*') {
                    # Not the server saying no - the network did. Nothing to learn about "auto".
                    Emit 'FAIL' $title $result.Detail $result.Ms
                    $tencentFails++
                    continue
                }
                else {
                    Emit 'PASS' 'info:tencent-auto' ('source "auto" REJECTED: ' + $result.Detail + ' - retrying with an explicit source') $result.Ms
                    $result = Invoke-Tencent $client ([string[]]@($source)) $from $row.Tencent
                }
            }
            else {
                $result = Invoke-Tencent $client ([string[]]@($source)) $from $row.Tencent
            }

            $joined = if ($result.Ok) { $result.Lines -join "`n" } else { '' }
            if (-not $result.Ok) { Emit 'FAIL' $title $result.Detail $result.Ms; $tencentFails++ }
            elseif (-not (Test-Script $joined $row.Script $source)) { Emit 'FAIL' $title ('wrong script / echoed: ' + (Show $joined)) $result.Ms; $tencentFails++ }
            else { Emit 'PASS' $title ('"{0}"' -f (Show $joined)) $result.Ms }
        }
        if (-not $Quick -and -not $sawAuto -and $wanted -notcontains 'en') {
            Emit 'SKIP' 'info:tencent-auto' 'en not in -Languages, auto not tested'
        }
    }
    finally {
        $client.Dispose()
    }
}

# --- main ---------------------------------------------------------------------------------
if ($Route -in 'Direct', 'Both') { Invoke-Route 'direct' $false }
if ($Route -in 'SystemProxy', 'Both') { Invoke-Route 'system-proxy' $true }

Write-Output ''
Write-Output ("free-engine probe: {0} pass, {1} fail, {2} skip, {3} requests sent" -f $script:Pass, $script:Fail, $script:Skip, $script:Requests)
if ($script:Fail -gt 0) { exit 1 }
exit 0
