# Fetch yaat-server logs from the production droplet
# Usage: .\tools\fetch-server-logs.ps1 [-Minutes 60]

param(
  [int]$Minutes = 0
)

$ErrorActionPreference = "Stop"

$dropletIp = "143.198.111.198"
$dropletUser = "root"
$yaatUser = "yaat"
$serverPath = "/home/yaat/yaat-server"
$outputDir = Join-Path $PSScriptRoot ".." ".tmp" "server-logs"
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$outputFile = Join-Path $outputDir "yaat-server-$timestamp.log"

# Ensure output directory exists
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

# Build docker compose logs command
$logsCmd = "cd $serverPath && docker compose logs yaat-server --no-color"
if ($Minutes -gt 0) {
  $logsCmd += " --since ${Minutes}m"
  Write-Host "Fetching logs from last $Minutes minutes..." -ForegroundColor Cyan
}
else {
  Write-Host "Fetching all available logs..." -ForegroundColor Cyan
}

# Check connectivity
$testConn = ssh -o ConnectTimeout=5 "$dropletUser@$dropletIp" "echo 'OK'" 2>&1
if ($LASTEXITCODE -ne 0) {
  Write-Host "Cannot reach $dropletIp" -ForegroundColor Red
  exit 1
}

# Fetch logs
$sshCmd = "su - $yaatUser -c `"$logsCmd`""
ssh "$dropletUser@$dropletIp" $sshCmd 2>&1 | Out-File -FilePath $outputFile -Encoding utf8

if ($LASTEXITCODE -ne 0) {
  Write-Host "Failed to fetch logs" -ForegroundColor Red
  exit 1
}

$lineCount = (Get-Content $outputFile).Count
Write-Host "Saved $lineCount lines to $outputFile" -ForegroundColor Green
