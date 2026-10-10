# Erase one VATSIM CID's stored data on a deployed yaat-server (a privacy deletion request).
# Usage: .\tools\erase-user-data.ps1 -Cid 1234567 [-Target yaat1]
#
# Calls DELETE /admin/data/{cid}, which deletes the CID's opt-in speech uploads and every room
# checkpoint naming it, and prints what was removed. ADMIN_PASSWORD is read from the repo-root
# .env.<target>, else .env, exactly as deploy-to-droplet.ps1 reads it. Log lines are not rewritten;
# they age out under the server's log retention (docs/data-handling.md).
#
# Exit codes: 0 erased (200); 1 nothing deleted because a prepare-restart or checkpoint restore is
# running (409), retry shortly; 2 incomplete (500: unreadable checkpoints or refused deletes, listed);
# 3 any other failure (bad CID, wrong password, server unreachable).

param(
  [Parameter(Mandatory)]
  [string]$Cid,
  [string]$Target = "yaat1"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path $PSScriptRoot -Parent
. (Join-Path $repoRoot "deploy-targets.ps1")
$cfg = Resolve-DeployTarget -Target $Target

$envFile = Join-Path $repoRoot ".env.$Target"
if (-not (Test-Path $envFile)) {
  $envFile = Join-Path $repoRoot ".env"
}
$adminPassword = $null
if (Test-Path $envFile) {
  foreach ($line in Get-Content $envFile) {
    if ($line -match "^ADMIN_PASSWORD=(.+)$") {
      $adminPassword = $Matches[1]
    }
  }
}
if (-not $adminPassword) {
  Write-Host "ADMIN_PASSWORD is not set in $envFile; it is the server's admin password (the same one deploy-to-droplet.ps1 uses)." -ForegroundColor Red
  exit 3
}

$uri = "$($cfg.ServerUrl.TrimEnd('/'))/admin/data/$([Uri]::EscapeDataString($Cid))"
Write-Host "Erasing data for CID $Cid on $($cfg.ServerUrl)..." -ForegroundColor Cyan

try {
  $response = Invoke-WebRequest -Uri $uri -Method Delete -Headers @{ "X-Yaat-Admin-Password" = $adminPassword } -SkipHttpErrorCheck
}
catch {
  Write-Host "Request to $uri failed: $($_.Exception.Message)" -ForegroundColor Red
  exit 3
}

$status = [int]$response.StatusCode
if (($status -ne 200) -and ($status -ne 500)) {
  $color = if ($status -eq 409) { "Yellow" } else { "Red" }
  Write-Host "HTTP ${status}: $($response.Content)" -ForegroundColor $color
  if ($status -eq 409) { exit 1 }
  exit 3
}

$report = $response.Content | ConvertFrom-Json
function Write-List([string]$Heading, $Items) {
  Write-Host "${Heading}: $(@($Items).Count)"
  foreach ($item in $Items) { Write-Host "  $item" }
}
Write-List "Speech uploads deleted" $report.speechUploadsDeleted
Write-List "Checkpoints deleted" $report.checkpointsDeleted
Write-List "Checkpoints unreadable (left in place)" $report.checkpointsUnreadable
Write-List "Deletes refused" $report.deleteFailures

if ($status -eq 500) {
  Write-Host "Erasure incomplete: retry, or remove the files listed above from the server's volumes by hand." -ForegroundColor Yellow
  exit 2
}
Write-Host "Erased." -ForegroundColor Green
exit 0
