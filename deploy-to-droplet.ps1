# Deploy yaat-server to DigitalOcean droplet
# Usage: .\deploy-to-droplet.ps1 [-NoLogs] [-NoCache]
#
# -NoCache  Pass --no-cache to docker compose build, forcing every layer
#           to rebuild from scratch (including the wasm-tools workload
#           install in the Dockerfile, which adds ~1-3 minutes). Off by
#           default — Docker's layer cache is already correctness-
#           preserving when source files change, and the layer cache is
#           what makes incremental deploys quick.

param(
  [switch]$NoLogs,
  [switch]$NoCache
)

$ErrorActionPreference = "Stop"

# Configuration
$dropletIp = "143.198.111.198"
$dropletUser = "root"
$yaatUser = "yaat"
$serverPath = "/home/yaat/yaat-server"
$serverUrl = "https://yaat1.leftos.dev"
$logFile = "/tmp/yaat-deploy-$(Get-Date -Format 'yyyyMMdd-HHmmss').log"
$followLogs = -not $NoLogs

# Load Discord webhook URL from .env
$envFile = Join-Path $PSScriptRoot ".env"
$discordWebhook = $null
if (Test-Path $envFile) {
  foreach ($line in Get-Content $envFile) {
    if ($line -match "^DISCORD_STATUS_WEBHOOK_URL=(.+)$") {
      $discordWebhook = $Matches[1]
    }
  }
}

function Send-DiscordStatus {
  param(
    [string]$Title,
    [string]$Description,
    [int]$Color
  )
  if (-not $discordWebhook) { return }
  $timestamp = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
  $payload = @{
    embeds = @(@{
      title = $Title
      description = $Description
      color = $Color
      timestamp = $timestamp
    })
  } | ConvertTo-Json -Depth 4
  try {
    Invoke-RestMethod -Uri $discordWebhook -Method Post -ContentType "application/json" -Body $payload | Out-Null
  }
  catch {
    Write-Host "⚠ Discord notification failed: $_" -ForegroundColor Yellow
  }
}

Write-Host "YAAT Server Deployment" -ForegroundColor Cyan
Write-Host "=====================" -ForegroundColor Cyan
Write-Host "Droplet:    $dropletIp"
Write-Host "Path:       $serverPath"
Write-Host "Log file:   $logFile"
Write-Host ""

# Helper: run command on droplet as yaat user
function Invoke-OnDroplet {
  param([string]$Command)
  $sshCmd = "su - $yaatUser -c `"$Command`""
  ssh "$dropletUser@$dropletIp" $sshCmd 2>&1
}

try {
  # Pre-flight checks
  Write-Host "[1/6] Checking connectivity..." -ForegroundColor Yellow
  $testConn = ssh -o ConnectTimeout=5 "$dropletUser@$dropletIp" "echo 'OK'" 2>&1
  if ($LASTEXITCODE -ne 0) {
    Write-Host "❌ Cannot reach $dropletIp" -ForegroundColor Red
    exit 1
  }
  Write-Host "✓ Connected" -ForegroundColor Green

  Write-Host ""
  Write-Host "[2/6] Checking current status..." -ForegroundColor Yellow
  Invoke-OnDroplet "cd $serverPath && docker compose ps" | Tee-Object -Append -FilePath $logFile

  Write-Host ""
  Write-Host "[3/6] Tearing down services..." -ForegroundColor Yellow
  Send-DiscordStatus -Title "Server going down for deployment" -Description "``$serverUrl`` is being taken down for an update." -Color 16776960
  Invoke-OnDroplet "cd $serverPath && docker compose down" | Tee-Object -Append -FilePath $logFile

  Write-Host ""
  Write-Host "[4/6] Pulling latest code and submodules..." -ForegroundColor Yellow
  Invoke-OnDroplet "cd $serverPath && git pull && git submodule update --init --remote --recursive" | Tee-Object -Append -FilePath $logFile

  # Get the commit hashes after pulling
  $commitInfo = Invoke-OnDroplet "cd $serverPath && git log -1 --format='%h %s'"
  $commitHash = ($commitInfo | Select-Object -First 1).Trim()
  $submoduleInfo = Invoke-OnDroplet "cd $serverPath && git -C extern/yaat log -1 --format='%h %s'"
  $submoduleHash = ($submoduleInfo | Select-Object -First 1).Trim()

  # Full hashes for /api/version endpoint
  $serverFullHash = (Invoke-OnDroplet "cd $serverPath && git rev-parse HEAD" | Select-Object -First 1).Trim()
  $clientFullHash = (Invoke-OnDroplet "cd $serverPath && git -C extern/yaat rev-parse HEAD" | Select-Object -First 1).Trim()

  Write-Host ""
  Write-Host "[5/6] Rebuilding and starting services..." -ForegroundColor Yellow
  $buildFlags = if ($NoCache) { "--no-cache " } else { "" }
  Invoke-OnDroplet "cd $serverPath && YAAT_SERVER_COMMIT=$serverFullHash YAAT_CLIENT_COMMIT=$clientFullHash docker compose build $buildFlags&& YAAT_SERVER_COMMIT=$serverFullHash YAAT_CLIENT_COMMIT=$clientFullHash docker compose up -d" | Tee-Object -Append -FilePath $logFile

  Write-Host ""
  Write-Host "[6/6] Waiting for server to be ready..." -ForegroundColor Yellow
  $retry = 0
  $maxRetries = 30
  $ready = $false

  while ($retry -lt $maxRetries) {
    $logCheck = ssh "$dropletUser@$dropletIp" "su - $yaatUser -c `"cd $serverPath && docker compose logs yaat-server 2>/dev/null | grep -q 'Now listening on'`"" 2>&1
    if ($LASTEXITCODE -eq 0) {
      $ready = $true
      break
    }
    Write-Host "  Waiting... ($($retry+1)/$maxRetries)"
    Start-Sleep -Seconds 2
    $retry++
  }

  if ($ready) {
    Write-Host "✓ Server is ready" -ForegroundColor Green
    Send-DiscordStatus -Title "Server is back up" -Description "``$serverUrl`` is back online.`nServer: ``$commitHash```nClient (yaat): ``$submoduleHash``" -Color 5814783
  }
  else {
    Write-Host "⚠ Server startup check timed out (may still be initializing)" -ForegroundColor Yellow
    Send-DiscordStatus -Title "Server deployment: startup check timed out" -Description "``$serverUrl`` may still be initializing.`nServer: ``$commitHash```nClient (yaat): ``$submoduleHash``" -Color 16744448
  }

  Write-Host ""
  Write-Host "✓ Deployment complete!" -ForegroundColor Green
  Write-Host ""

  if ($followLogs) {
    Write-Host "Following server logs (Ctrl-C to detach)..." -ForegroundColor Cyan
    Write-Host ""
    ssh "$dropletUser@$dropletIp" "su - $yaatUser -c `"cd $serverPath && docker compose logs -f yaat-server`""
  }
}
catch {
  Send-DiscordStatus -Title "Deployment failed" -Description "``$serverUrl`` deployment failed.`nError: $_" -Color 16711680
  Write-Host "❌ Error: $_" -ForegroundColor Red
  exit 1
}
