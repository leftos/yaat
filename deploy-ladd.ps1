# Ships the LADD block list to a deployment's droplet.
#
# Usage:
#   .\deploy-ladd.ps1 [-Target yaat1] [-Restart]
#
# Copies ..\yaat-server\ladd\ladd.json (built by yaat-server/tools/refresh-ladd.py from the FAA IndustryLADD
# list) to <ServerPath>/ladd/ladd.json, where docker-compose.yml bind-mounts it read-only. The server reads the
# list once when SWIM ingest starts, so the container must be recreated for a new list to apply: pass -Restart
# to do that here, or run deploy-to-droplet.ps1 afterwards. The list is FAA-restricted data: only counts and
# dates are printed, never identities.

param(
  [string]$Target = "yaat1",
  [switch]$Restart
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "deploy-targets.ps1")
$cfg = Resolve-DeployTarget -Target $Target
$dropletIp = $cfg.DropletIp
$serverPath = $cfg.ServerPath
$remoteEnvFile = $cfg.RemoteEnvFile
$serverRepo = Join-Path (Split-Path $PSScriptRoot -Parent) "yaat-server"
$localFile = Join-Path $serverRepo "ladd" "ladd.json"

if (-not (Test-Path $localFile)) {
  throw "No LADD list at $localFile. Build it first: python tools/refresh-ladd.py <IndustryLADD file> --published <date> (in yaat-server)."
}

$list = Get-Content $localFile -Raw | ConvertFrom-Json
if ($list.format -ne "yaat-ladd/1") {
  throw "$localFile is not a yaat-ladd/1 file (format: $($list.format))."
}
$count = @($list.entries.PSObject.Properties).Count
$ageDays = ((Get-Date).ToUniversalTime().Date - [datetime]::ParseExact($list.publishedUtc, "yyyy-MM-dd", $null)).Days
Write-Host "deploy-ladd -> $Target (${dropletIp}:$serverPath/ladd/ladd.json)"
Write-Host "  local list: $count identities, published $($list.publishedUtc) ($ageDays days ago)"
if ($ageDays -gt 45) {
  throw "The list is older than the server's 45-day limit; refresh it before deploying."
}

$stamp = (Get-Date).ToUniversalTime().ToString("yyyyMMddTHHmmssZ")
$remoteScript = @"
set -e
mkdir -p '$serverPath/ladd'
tmp="$serverPath/ladd/ladd.json.tmp.$stamp"
tr -d '\r' > "`$tmp"
chown yaat:docker "`$tmp"
chmod 640 "`$tmp"
mv "`$tmp" '$serverPath/ladd/ladd.json'
chown yaat:docker '$serverPath/ladd'
echo "wrote $serverPath/ladd/ladd.json"
"@
Get-Content $localFile -Raw | ssh "root@$dropletIp" $remoteScript
if ($LASTEXITCODE -ne 0) {
  throw "Writing the LADD list on $dropletIp failed (ssh exit $LASTEXITCODE)."
}

if ($Restart) {
  Write-Host "Recreating the container so the new list is read..."
  ssh "root@$dropletIp" "su - yaat -c `"cd $serverPath && docker compose --env-file $remoteEnvFile up -d --force-recreate --no-build yaat-server`""
  if ($LASTEXITCODE -ne 0) {
    throw "docker compose up failed (exit $LASTEXITCODE)."
  }
  Write-Host "Done. Check the log for 'LADD list in force' (or 'SWIM ingest refused')."
} else {
  Write-Host "Done. The server reads the list at startup: rerun with -Restart or run .\deploy-to-droplet.ps1 -Target $Target."
}
