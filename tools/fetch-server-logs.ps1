# Fetch yaat-server logs from the production droplet
# Usage: .\tools\fetch-server-logs.ps1 [-Lines 2000]   # the live yaat-server.log (whole file when -Lines is 0)
#        .\tools\fetch-server-logs.ps1 -Files          # every persisted /data/logs generation file
# The server's stdout carries Critical lines only, so both modes read the log files on the yaat-logs volume.

param(
  [int]$Lines = 0,
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
  # The log generations live on the yaat-logs volume (/data/logs), surviving container recreation.
  # Tar them up in the container, base64 over ssh (PowerShell mangles raw binary stdout), decode + extract.
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

if ($Lines -gt 0) {
  $logsCmd = "cd $serverPath && docker compose exec -T yaat-server tail -n $Lines /data/logs/yaat-server.log"
  Write-Host "Fetching the last $Lines lines of the live log..." -ForegroundColor Cyan
}
else {
  $logsCmd = "cd $serverPath && docker compose exec -T yaat-server cat /data/logs/yaat-server.log"
  Write-Host "Fetching the live log..." -ForegroundColor Cyan
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
