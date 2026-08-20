# Fetch yaat-server logs from the production droplet
# Usage: .\tools\fetch-server-logs.ps1 [-Minutes 60]   # docker stdout stream (current container only)
#        .\tools\fetch-server-logs.ps1 -Files          # persisted /data/logs generation files (survive redeploys)

param(
  [int]$Minutes = 0,
  [switch]$Files
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

if ($Files) {
  # The rolled log generations live on the yaat-logs volume (/data/logs), surviving container
  # recreation — unlike `docker compose logs`, which only covers the current container. Tar them
  # up in the container, base64 over ssh (PowerShell mangles raw binary stdout), decode + extract.
  Write-Host "Fetching persisted log files from /data/logs..." -ForegroundColor Cyan
  $tarCmd = "cd $serverPath && docker compose exec -T yaat-server tar -C /data/logs -czf - . | base64 -w0"
  $sshCmd = "su - $yaatUser -c `"$tarCmd`""
  $b64 = ssh "$dropletUser@$dropletIp" $sshCmd
  if (($LASTEXITCODE -ne 0) -or (-not $b64)) {
    Write-Host "Failed to fetch log files" -ForegroundColor Red
    exit 1
  }
  $filesDir = Join-Path $outputDir "files-$timestamp"
  New-Item -ItemType Directory -Path $filesDir -Force | Out-Null
  $tarPath = Join-Path $filesDir "logs.tgz"
  [IO.File]::WriteAllBytes($tarPath, [Convert]::FromBase64String(($b64 -join '')))
  tar -xzf $tarPath -C $filesDir
  Remove-Item $tarPath
  $fetched = Get-ChildItem $filesDir | Sort-Object Name
  Write-Host "Saved $($fetched.Count) file(s) to $filesDir" -ForegroundColor Green
  $fetched | ForEach-Object { Write-Host "  $($_.Name) ($([math]::Round($_.Length / 1KB)) KB)" }
  exit 0
}

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
