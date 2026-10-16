# O-41 verification: the Release binaries must not carry a single SHIYU_ string
# literal — no literal, no variable name to ask the environment for. Scans both
# ASCII (metadata/attrs) and UTF-16LE (.NET string heap) encodings.
#
# Scans the managed DLL, not the exe: the exe is only the native apphost stub,
# the string literals live in Shiyu.App.dll (and Shiyu.Core.dll for the update
# feed override — pass -Exe to point at it).
param(
    [string]$Exe = (Join-Path $PSScriptRoot "..\..\src\Shiyu.App\bin\Release\net9.0-windows10.0.17763.0\Shiyu.App.dll")
)

$bytes = [System.IO.File]::ReadAllBytes($Exe)
$ascii = [System.Text.Encoding]::ASCII.GetBytes('SHIYU_')
$utf16 = [System.Text.Encoding]::Unicode.GetBytes('SHIYU_')

function Find-Seq([byte[]]$hay, [byte[]]$needle) {
    for ($i = 0; $i -le $hay.Length - $needle.Length; $i++) {
        $ok = $true
        for ($j = 0; $j -lt $needle.Length; $j++) {
            if ($hay[$i + $j] -ne $needle[$j]) { $ok = $false; break }
        }
        if ($ok) { return $true }
    }
    return $false
}

$hitA = Find-Seq $bytes $ascii
$hitU = Find-Seq $bytes $utf16

Write-Host ("release-override-scan: {0} ({1} bytes)" -f $Exe, $bytes.Length)
Write-Host ("  ASCII SHIYU_ present: " + $hitA)
Write-Host ("  UTF-16 SHIYU_ present: " + $hitU)

if ($hitA -or $hitU) {
    Write-Host "release-override-scan: FAILED"
    exit 1
}
Write-Host "release-override-scan: clean"
