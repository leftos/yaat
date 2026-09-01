# The monthly LADD routine as one command: download the FAA list from ADX, rebuild the block list,
# stage it on the droplet, wait for training rooms to clear, then recreate the container so it applies.
#
# Usage:
#   .\update-ladd.ps1 [-Target yaat1] [-Zip <path>]
#
# Steps (each reuses the existing tool for that stage):
#   1. yaat-server/tools/fetch-ladd.py fetch      - syncs the weekly lists from the ADX portal's document
#      library and zips them (ADX_USERNAME/ADX_PASSWORD from yaat-server's .env); -Zip skips this and
#      uses an already-downloaded LADD zip instead.
#   2. yaat-server/tools/refresh-ladd.py <zip> --download-aircraft-db - rebuild ladd/ladd.json.
#   3. deploy-ladd.ps1 -Target <target>           - stage the list on the droplet (no restart yet).
#   4. deploy-to-droplet.ps1 -WaitForEmptyRooms   - block until /admin/status reports zero rooms, so no
#      in-progress training session is disrupted (Ctrl-C here leaves the list staged but not applied).
#   5. deploy-ladd.ps1 -Target <target> -Restart  - recreate the container; the new list is read at startup.
#
# The FAA publishes on the first Thursday of each month; SCDS subscribers must apply the list within five
# business days, and the server refuses SWIM ingest with a list older than 45 days.

param(
  [string]$Target = "yaat1",
  [string]$Zip
)

$ErrorActionPreference = "Stop"
$serverRepo = Join-Path (Split-Path $PSScriptRoot -Parent) "yaat-server"
if (-not (Test-Path (Join-Path $serverRepo "tools/refresh-ladd.py"))) {
  throw "yaat-server repo not found at $serverRepo."
}

if ($Zip) {
  $zipPath = (Resolve-Path $Zip).Path
} else {
  Write-Host "== Downloading the LADD zip from ADX ==" -ForegroundColor Cyan
  Push-Location $serverRepo
  try {
    & python tools/fetch-ladd.py fetch 2>&1 | Tee-Object -Variable fetchOutput | Out-Host
    if ($LASTEXITCODE -ne 0) {
      throw "fetch-ladd.py failed (exit $LASTEXITCODE). For a manual fallback, download the zip from the ADX portal and rerun with -Zip <path>."
    }
    $zipLine = @($fetchOutput | Where-Object { $_ -is [string] -and $_.StartsWith("ZIP=") })[-1]
    if (-not $zipLine) {
      throw "fetch-ladd.py printed no ZIP= line; cannot locate the downloaded file."
    }
    $zipPath = (Resolve-Path $zipLine.Substring(4)).Path
  } finally {
    Pop-Location
  }
}

Write-Host "== Rebuilding ladd/ladd.json from $zipPath ==" -ForegroundColor Cyan
Push-Location $serverRepo
try {
  & python tools/refresh-ladd.py $zipPath --download-aircraft-db
  if ($LASTEXITCODE -ne 0) {
    throw "refresh-ladd.py failed (exit $LASTEXITCODE)."
  }
} finally {
  Pop-Location
}

Write-Host "== Staging the list on $Target ==" -ForegroundColor Cyan
& (Join-Path $PSScriptRoot "deploy-ladd.ps1") -Target $Target

Write-Host "== Waiting for training rooms on $Target to clear before restarting ==" -ForegroundColor Cyan
& (Join-Path $PSScriptRoot "deploy-to-droplet.ps1") -Target $Target -WaitForEmptyRooms

Write-Host "== Restarting the server so the new list applies ==" -ForegroundColor Cyan
& (Join-Path $PSScriptRoot "deploy-ladd.ps1") -Target $Target -Restart
