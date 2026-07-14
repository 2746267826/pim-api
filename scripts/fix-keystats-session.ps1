#Requires -Version 5.1
param(
    [Parameter(Mandatory = $true)]
    [string]$KeyStatsExe,
    [string]$LogPath = $(Join-Path $env:TEMP "pim-keystats-fix-last.log")
)

$ErrorActionPreference = "Continue"
$lines = New-Object System.Collections.Generic.List[string]
function Log([string]$m) {
    $lines.Add(("[ {0} ] {1}" -f (Get-Date -Format o), $m))
}

Log "KeyStatsExe=$KeyStatsExe"
Log "Starting forced cleanup of KeyStats.exe"

& taskkill.exe /F /IM KeyStats.exe /T 2>&1 | ForEach-Object { Log "$_" }

Start-Sleep -Milliseconds 500

$remaining = @(Get-Process -Name KeyStats -ErrorAction SilentlyContinue)
if ($remaining.Count -gt 0) {
    foreach ($p in $remaining) {
        Log ("REMAINING pid={0} session={1}" -f $p.Id, $p.SessionId)
    }
    Log "FAIL: KeyStats processes still present"
    $lines | Set-Content -Path $LogPath -Encoding UTF8
    exit 2
}

Log "OK: no KeyStats processes remain"
$lines | Set-Content -Path $LogPath -Encoding UTF8
# Intentionally do not Start-Process here; client starts KeyStats in user session.
exit 0
