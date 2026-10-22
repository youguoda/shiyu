# Writes latest.json: the release manifest the updater falls back to when the
# anonymous GitHub API is rate-limited (60 calls per hour per egress IP).
# Same shape as GET /repos/{owner}/{repo}/releases/latest, so the client's
# ReleaseManifest.Parse reads both. Fetched from
# github.com/{repo}/releases/latest/download/latest.json, which costs no API quota.
#
# Usage: write-latest-json.ps1 -Tag v0.18.2 -Repository youguoda/shiyu -NotesFile release-notes.md -Assets a.zip,a.zip.sig
param(
    [Parameter(Mandatory)] [string]$Tag,
    [Parameter(Mandatory)] [string]$Repository,
    [Parameter(Mandatory)] [string]$NotesFile,
    [Parameter(Mandatory)] [string[]]$Assets,
    [string]$Output = 'latest.json'
)

$ErrorActionPreference = 'Stop'

# Through -File a comma list arrives as one string.
$Assets = @($Assets | ForEach-Object { $_ -split ',' } | Where-Object { $_ })

$base = "https://github.com/$Repository/releases/download/$Tag"
$manifest = [ordered]@{
    tag_name     = $Tag
    body         = [System.IO.File]::ReadAllText((Resolve-Path $NotesFile), [System.Text.Encoding]::UTF8)
    published_at = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    assets       = @($Assets | ForEach-Object {
        [ordered]@{
            name                 = [System.IO.Path]::GetFileName($_)
            size                 = (Get-Item $_).Length
            browser_download_url = "$base/$([System.IO.Path]::GetFileName($_))"
        }
    })
}

$json = $manifest | ConvertTo-Json -Depth 4
[System.IO.File]::WriteAllText((Join-Path (Get-Location) $Output), $json, (New-Object System.Text.UTF8Encoding $false))
Write-Output "wrote $Output for $Tag with $($Assets.Count) assets"
