# Probe hooks (members named Probe*) belong to DEBUG builds only: they live in the
# *Probes.cs partials under #if DEBUG. Hooks written straight into a window's main file
# ship in Release - 2026-10-21 found 36 such names, beside a SetCursorPos P/Invoke.
#
# Scans the Release Shiyu.App.dll metadata for identifiers that start with "Probe" and
# fails on any that is not a known production name.
param(
    [string]$Dll = (Join-Path $PSScriptRoot "..\..\src\Shiyu.App\bin\Release\net9.0-windows10.0.17763.0\Shiyu.App.dll")
)

# Production names that merely start with "Probe": DebugOverrides' Release constants and
# the background file-existence check in PreviewWindow.
$allowed = @('ProbeClipboard', 'ProbeCommand', 'ProbeDirectory', 'ProbeItem', 'ProbeText', 'ProbeFileRows')

$text = [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($Dll))
$found = [regex]::Matches($text, 'Probe[A-Z][A-Za-z0-9_]*') | ForEach-Object { $_.Value } | Sort-Object -Unique
$leaked = @($found | Where-Object { $allowed -notcontains $_ })

Write-Host ("no-release-probes: {0}" -f $Dll)
if ($leaked.Count -gt 0) {
    Write-Host ("no-release-probes: FAILED - {0} probe member(s) in Release: {1}" -f $leaked.Count, ($leaked -join ', '))
    Write-Host "Move them into the window's *Probes.cs partial under #if DEBUG."
    exit 1
}

Write-Host "no-release-probes: clean"
exit 0
