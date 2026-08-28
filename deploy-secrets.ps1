# Pushes the merged env settings for a deployment to its droplet.
#
# Usage:
#   .\deploy-secrets.ps1 [-Target yaat1] [-DryRun]
#
# Sources, lowest to highest precedence:
#   1. the env file already on the droplet (<ServerPath>/<RemoteEnvFile>) — keys that live only there
#      (YAAT_DOMAIN, ...) are kept as they are
#   2. ..\yaat-server\.env          — values shared by every deployment (SWIM subscription, admin password, ...)
#   3. ..\yaat-server\.env.<Target> — values specific to this deployment (VATSIM client, JWT key, ...)
#
# The merged file replaces the remote one (a timestamped .bak is left beside it). Only KEY names are ever
# printed; values stay inside the files and the SSH stream. Nothing is restarted: run deploy-to-droplet.ps1
# afterwards so docker compose re-creates the container with the new values.

param(
  [string]$Target = "yaat1",
  [switch]$DryRun
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "deploy-targets.ps1")
$cfg = Resolve-DeployTarget -Target $Target
$dropletIp = $cfg.DropletIp
$remotePath = "$($cfg.ServerPath)/$($cfg.RemoteEnvFile)"
$serverRepo = Join-Path (Split-Path $PSScriptRoot -Parent) "yaat-server"
$sharedFile = Join-Path $serverRepo ".env"
$targetFile = Join-Path $serverRepo ".env.$Target"

# Parses KEY=VALUE lines; comments and blanks are dropped (the remote file is regenerated from the merge).
function Read-EnvLines {
  param([string[]]$Lines)
  $map = [ordered]@{}
  foreach ($line in $Lines) {
    if ($line -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=(.*)$') {
      $map[$Matches[1]] = $Matches[2]
    }
  }
  return $map
}

function Read-LocalEnv {
  param([string]$Path, [string]$Label)
  if (-not (Test-Path $Path)) {
    Write-Host "  $Label`: $Path (absent)"
    return [ordered]@{}
  }
  $map = Read-EnvLines -Lines (Get-Content $Path)
  Write-Host "  $Label`: $Path ($($map.Count) keys)"
  return $map
}

Write-Host "deploy-secrets -> $Target (${dropletIp}:${remotePath})"
$shared = Read-LocalEnv -Path $sharedFile -Label "shared"
$perTarget = Read-LocalEnv -Path $targetFile -Label "target"
if (($shared.Count + $perTarget.Count) -eq 0) {
  throw "No local env values found for '$Target' (looked for $sharedFile and $targetFile)."
}

$remoteLines = @(ssh "root@$dropletIp" "cat '$remotePath' 2>/dev/null || true")
if ($LASTEXITCODE -ne 0) {
  throw "Could not read $remotePath on $dropletIp (ssh exit $LASTEXITCODE)."
}
$remote = Read-EnvLines -Lines $remoteLines
Write-Host "  remote: $remotePath ($($remote.Count) keys)"

$merged = [ordered]@{}
foreach ($k in $remote.Keys) { $merged[$k] = $remote[$k] }
foreach ($k in $shared.Keys) { $merged[$k] = $shared[$k] }
foreach ($k in $perTarget.Keys) { $merged[$k] = $perTarget[$k] }

$added = @($merged.Keys | Where-Object { -not $remote.Contains($_) })
$changed = @($merged.Keys | Where-Object { $remote.Contains($_) -and ($remote[$_] -ne $merged[$_]) })
$remoteOnly = @($remote.Keys | Where-Object { -not $shared.Contains($_) -and -not $perTarget.Contains($_) })
Write-Host ""
Write-Host "  added:       $(if ($added) { $added -join ', ' } else { '(none)' })"
Write-Host "  changed:     $(if ($changed) { $changed -join ', ' } else { '(none)' })"
Write-Host "  remote-only: $(if ($remoteOnly) { $remoteOnly -join ', ' } else { '(none)' })"

if (($added.Count + $changed.Count) -eq 0) {
  Write-Host "Remote file already matches. Nothing to do."
  exit 0
}
if ($DryRun) {
  Write-Host "Dry run — remote file untouched."
  exit 0
}

$stamp = (Get-Date).ToUniversalTime().ToString("yyyyMMddTHHmmssZ")
$content = (($merged.Keys | ForEach-Object { "$_=$($merged[$_])" }) -join "`n") + "`n"
# The stream goes through a temp file so a dropped connection never leaves a half-written env file;
# ownership/mode match what compose expects (yaat:docker, 660). CRs are stripped in case the shell adds them.
$remoteScript = @"
set -e
tmp="$remotePath.tmp.$stamp"
tr -d '\r' > "`$tmp"
if [ -f '$remotePath' ]; then cp -p '$remotePath' '$remotePath.bak-$stamp'; fi
chown yaat:docker "`$tmp"
chmod 660 "`$tmp"
mv "`$tmp" '$remotePath'
echo "wrote $remotePath (backup $remotePath.bak-$stamp)"
"@
$content | ssh "root@$dropletIp" $remoteScript
if ($LASTEXITCODE -ne 0) {
  throw "Writing $remotePath on $dropletIp failed (ssh exit $LASTEXITCODE)."
}
Write-Host "Done. Run .\deploy-to-droplet.ps1 -Target $Target for the container to pick up the new values."
