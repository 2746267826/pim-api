param(
  [string]$Date = "",
  [switch]$PrintEnv
)
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $root) { $root = Resolve-Path (Join-Path $PSScriptRoot "../..") }
# Prefer Git Bash
$bash = @(
  "C:\Program Files\Git\bin\bash.exe",
  "bash"
) | Where-Object { $_ -eq "bash" -or (Test-Path $_) } | Select-Object -First 1
$script = Join-Path $PSScriptRoot "resolve-version.sh"
$args = @()
if ($Date) { $args += @("--date", $Date) }
if ($PrintEnv) { $args += "--print-env" }
& $bash $script @args
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
