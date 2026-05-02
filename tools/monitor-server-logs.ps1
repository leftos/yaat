# Live-tail yaat-server logs from the production droplet
# Usage: .\tools\monitor-server-logs.ps1 [-Tail 100]
# Press Ctrl+C to stop.

param(
  [int]$Tail = 100
)

$ErrorActionPreference = "Stop"

$dropletIp = "143.198.111.198"
$dropletUser = "root"
$yaatUser = "yaat"
$serverPath = "/home/yaat/yaat-server"

# Build docker compose logs command (follow + initial tail)
$logsCmd = "cd $serverPath && docker compose logs yaat-server --no-color --follow --tail $Tail"

Write-Host "Streaming logs from $dropletIp (tail=$Tail). Press Ctrl+C to stop." -ForegroundColor Cyan

# Check connectivity
$testConn = ssh -o ConnectTimeout=5 "$dropletUser@$dropletIp" "echo 'OK'" 2>&1
if ($LASTEXITCODE -ne 0) {
  Write-Host "Cannot reach $dropletIp" -ForegroundColor Red
  exit 1
}

# Stream logs (-t allocates a TTY so docker compose flushes line-by-line and
# Ctrl+C propagates cleanly to the remote process)
$sshCmd = "su - $yaatUser -c `"$logsCmd`""
ssh -t "$dropletUser@$dropletIp" $sshCmd
