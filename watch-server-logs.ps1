# Watch yaat-server logs on a remote droplet
# Usage: .\watch-server-logs.ps1 [hostname]
# Example: .\watch-server-logs.ps1 yaat1.leftos.dev

param(
  [Parameter(Position = 0)]
  [string]$Server = "yaat1.leftos.dev"
)

$ErrorActionPreference = "Stop"

$dropletUser = "root"
$yaatUser = "yaat"
$serverPath = "/home/yaat/yaat-server"

Write-Host "Connecting to $Server..." -ForegroundColor Cyan
ssh "$dropletUser@$Server" "su - $yaatUser -c `"cd $serverPath && docker compose logs -f --tail 500 yaat-server`""
