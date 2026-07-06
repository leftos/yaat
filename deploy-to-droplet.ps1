# Deploy yaat-server to a DigitalOcean droplet
# Usage: .\deploy-to-droplet.ps1 [-Target <name>] [-NoLogs] [-NoCache] [-SkipSessionSave] [-DrainSeconds <sec>] [-CacheReserveGb <gb>]
#        .\deploy-to-droplet.ps1 -Target yaat2 -RebootOnly [-NoLogs] [-SkipSessionSave] [-DrainSeconds <sec>]
#        .\deploy-to-droplet.ps1 [-Target <name>] -StatusOnly    (report active rooms only, no deploy)
#
# -Target  Which deployment to act on (default "yaat1"). Selects the droplet IP, server path,
#          public URL, and remote compose env file from the $targets map below. Add a new
#          entry to $targets to deploy another domain (e.g. yaat2). Each target's secrets
#          (ADMIN_PASSWORD, DISCORD_STATUS_WEBHOOK_URL) are read from a local .env.<target>
#          file if present, falling back to .env.
#
# By default, active training sessions are preserved across deploy:
#   POST /admin/prepare-restart (drain + checkpoint) -> rebuild container -> restore on boot.
#
# -RebootOnly  Restart the yaat-server container WITHOUT pulling code, rebuilding,
#              or pruning the build cache. Use it to force the server to re-run its
#              on-boot data refresh — e.g. when a new AIRAC cycle starts and you want
#              the latest nav data downloaded, cached, and loaded. The container is
#              recreated from the existing image (--force-recreate); the persistent
#              cache volume's vNAS serial-staleness check on boot pulls the new cycle
#              if the serial changed. Sessions are preserved exactly as a normal deploy
#              (honors -SkipSessionSave / -DrainSeconds). -NoCache / -CacheReserveGb are
#              ignored (nothing is built or pruned).
#
# -WaitForEmptyRooms  Do NOT deploy. Poll the target's /admin/status endpoint every
#                     -PollSeconds (default 60) and block until the server reports zero
#                     rooms in memory, printing the active rooms each check. Used by the
#                     prepare-release flow to hold a release until the server is quiet so
#                     no in-progress training session is disrupted. Requires ADMIN_PASSWORD
#                     (read from .env.<target>/.env, same as prepare-restart). Ctrl-C to
#                     stop waiting. Ignores all build/deploy flags.
#
# -StatusOnly  Do NOT deploy. Query the target's /admin/status endpoint once and print the
#              active rooms (count, members, scenario, aircraft count), then exit. Used by
#              the prepare-release flow to report room occupancy before asking whether to
#              deploy. Requires ADMIN_PASSWORD (read from .env.<target>/.env). Exit code 0
#              on a successful query (whether or not rooms are active), 2 if the query
#              failed (server unreachable or password unset). Ignores all build/deploy flags.
#
# -NoCache  Pass --no-cache to docker compose build, forcing every layer
#           to rebuild from scratch (including the wasm-tools workload
#           install in the Dockerfile, which adds ~1-3 minutes). Off by
#           default — Docker's layer cache is already correctness-
#           preserving when source files change, and the layer cache is
#           what makes incremental deploys quick.
#
# -SkipSessionSave  Skip prepare-restart (immediate container replace; active rooms are lost).
#
# -CacheReserveGb  After a successful build, prune the BuildKit cache down to this
#                  many GB of most-recently-used layers (default 10). Every deploy
#                  rebuilds source/publish layers, so without this the cache grows
#                  unbounded toward BuildKit's auto ceiling (~57 GB) and fills the
#                  disk. The reserve keeps the current build's base/restore/wasm
#                  layers so the next incremental deploy stays fast.

param(
  [string]$Target = "yaat1",
  [switch]$NoLogs,
  [switch]$NoCache,
  [switch]$SkipSessionSave,
  [switch]$RebootOnly,
  [switch]$WaitForEmptyRooms,
  [switch]$StatusOnly,
  [int]$DrainSeconds = 30,
  [int]$PollSeconds = 60,
  [int]$CacheReserveGb = 10
)

$ErrorActionPreference = "Stop"

# Per-deployment configuration. Add an entry here for each domain you deploy.
# RemoteEnvFile is the env file docker compose reads ON the droplet (--env-file); it must define
# YAAT_DOMAIN, VATSIM_CLIENT_ID, JWT_SIGNING_KEY, etc. (see yaat-server/.env.example). ".env" is the
# compose default, so a single-deployment droplet can keep using ".env".
$targets = @{
  yaat1 = @{
    DropletIp     = "143.198.111.198"
    ServerPath    = "/home/yaat/yaat-server"
    ServerUrl     = "https://yaat1.leftos.dev"
    RemoteEnvFile = ".env.yaat1"
  }
  # yaat2 = @{
  #   DropletIp     = "<droplet-ip>"
  #   ServerPath    = "/home/yaat/yaat-server"
  #   ServerUrl     = "https://yaat2.leftos.dev"
  #   RemoteEnvFile = ".env.yaat2"
  # }
}

if (-not $targets.ContainsKey($Target)) {
  throw "Unknown target '$Target'. Known targets: $($targets.Keys -join ', '). Add it to the `$targets map in deploy-to-droplet.ps1."
}
$cfg = $targets[$Target]

# Configuration
$dropletIp = $cfg.DropletIp
$dropletUser = "root"
$yaatUser = "yaat"
$serverPath = $cfg.ServerPath
$serverUrl = $cfg.ServerUrl
$remoteEnvFile = $cfg.RemoteEnvFile
$logFile = "/tmp/yaat-deploy-$Target-$(Get-Date -Format 'yyyyMMdd-HHmmss').log"
$followLogs = -not $NoLogs

# Realistic end-to-end downtime users should expect, surfaced in the Discord status messages.
# A full deploy rebuilds the container (~7-10 min); a reboot-only just recreates it from the
# existing image, so it is back much sooner. This is the user-facing outage estimate, distinct
# from the short -DrainSeconds connection-drain/session-save window.
$estimatedDowntime = if ($RebootOnly) { "a few minutes" } else { "~10 minutes" }

# Load this target's secrets from a local .env.<target> if present, else the shared .env.
$envFile = Join-Path $PSScriptRoot ".env.$Target"
if (-not (Test-Path $envFile)) {
  $envFile = Join-Path $PSScriptRoot ".env"
}
$discordWebhook = $null
$adminPassword = $null
if (Test-Path $envFile) {
  foreach ($line in Get-Content $envFile) {
    if ($line -match "^DISCORD_STATUS_WEBHOOK_URL=(.+)$") {
      $discordWebhook = $Matches[1]
    }
    if ($line -match "^ADMIN_PASSWORD=(.+)$") {
      $adminPassword = $Matches[1]
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

function Test-ServerReachable {
  try {
    $null = Invoke-WebRequest -Uri "$serverUrl/api/version" -Method Get -TimeoutSec 10 -UseBasicParsing
    return $true
  }
  catch {
    return $false
  }
}

function Invoke-PrepareRestartSessions {
  param([int]$DrainSec)

  if (-not $adminPassword) {
    Write-Host "⚠ ADMIN_PASSWORD not set in .env — skipping session save (active rooms will be lost)" -ForegroundColor Yellow
    return $false
  }

  if (-not (Test-ServerReachable)) {
    Write-Host "⚠ Server not reachable at $serverUrl — skipping session save" -ForegroundColor Yellow
    return $false
  }

  Write-Host "  Calling prepare-restart (drain ${DrainSec}s)..." -ForegroundColor Gray
  $headers = @{ "X-Yaat-Admin-Password" = $adminPassword }
  try {
    $result = Invoke-RestMethod -Uri "$serverUrl/admin/prepare-restart?drainSeconds=$DrainSec" -Method Post -Headers $headers -TimeoutSec ($DrainSec + 120)
    if ($result.success) {
      Write-Host "✓ Saved $($result.roomsSaved) room checkpoint(s)" -ForegroundColor Green
      return $true
    }

    Write-Host "⚠ prepare-restart failed: $($result.message)" -ForegroundColor Yellow
    return $false
  }
  catch {
    Write-Host "⚠ prepare-restart request failed: $_" -ForegroundColor Yellow
    return $false
  }
}

# Format the /admin/status rooms array into a one-line human summary. Shared by the
# single-shot status check and the wait-for-empty poll so both render rooms identically.
function Format-RoomsSummary {
  param($Rooms)
  return ($Rooms | ForEach-Object {
      $members = if ($_.memberInitials -and $_.memberInitials.Count -gt 0) { $_.memberInitials -join "," } else { "no members" }
      $scenario = if ($_.scenarioName) { $_.scenarioName } else { "(no scenario)" }
      "$($_.roomId) [$members] $scenario $($_.aircraftCount)ac"
    }) -join "; "
}

# Query /admin/status once and print the active rooms without blocking or deploying.
# Used by the prepare-release flow to report room occupancy before the deploy prompt.
# Throws on a query failure so the caller can distinguish "queried, empty" from "unknown".
function Invoke-StatusCheck {
  if (-not $adminPassword) {
    throw "ADMIN_PASSWORD not set in $envFile — cannot query /admin/status. Set it (matching the droplet) and retry."
  }

  $headers = @{ "X-Yaat-Admin-Password" = $adminPassword }
  $status = Invoke-RestMethod -Uri "$serverUrl/admin/status" -Method Get -Headers $headers -TimeoutSec 15
  $count = [int]$status.roomCount
  if ($count -eq 0) {
    Write-Host "✓ No active rooms on $serverUrl." -ForegroundColor Green
  }
  else {
    Write-Host ("{0} active room(s) on {1}: {2}" -f $count, $serverUrl, (Format-RoomsSummary $status.rooms)) -ForegroundColor Yellow
  }
}

# Block until the target server reports zero rooms in memory. Polls /admin/status every
# $PollSec seconds, printing the active rooms each check. Returns once the server is empty.
# A transient query failure (server briefly unreachable) is reported and retried, never
# treated as "empty" — we only stop when the server affirmatively says roomCount == 0.
function Invoke-WaitForEmptyRooms {
  param([int]$PollSec)

  if (-not $adminPassword) {
    throw "ADMIN_PASSWORD not set in $envFile — cannot query /admin/status. Set it (matching the droplet) and retry."
  }

  Write-Host "Waiting for all rooms on $serverUrl to clear (polling every ${PollSec}s, Ctrl-C to stop)..." -ForegroundColor Cyan
  $headers = @{ "X-Yaat-Admin-Password" = $adminPassword }
  while ($true) {
    try {
      $status = Invoke-RestMethod -Uri "$serverUrl/admin/status" -Method Get -Headers $headers -TimeoutSec 15
      $count = [int]$status.roomCount
      if ($count -eq 0) {
        Write-Host "✓ No active rooms — server is quiet." -ForegroundColor Green
        return
      }

      Write-Host ("  {0} room(s) active: {1}" -f $count, (Format-RoomsSummary $status.rooms)) -ForegroundColor Yellow
    }
    catch {
      Write-Host "⚠ Could not query $serverUrl/admin/status ($_) — retrying in ${PollSec}s..." -ForegroundColor Yellow
    }

    Start-Sleep -Seconds $PollSec
  }
}

$headerTitle =
  if ($StatusOnly) { "YAAT Server: active-room status (no deploy)" }
  elseif ($WaitForEmptyRooms) { "YAAT Server: wait for empty rooms (no deploy)" }
  elseif ($RebootOnly) { "YAAT Server Reboot (refresh nav data, no redeploy)" }
  else { "YAAT Server Deployment" }
Write-Host $headerTitle -ForegroundColor Cyan
Write-Host "=====================" -ForegroundColor Cyan
Write-Host "Target:     $Target ($serverUrl)"
if (-not $WaitForEmptyRooms -and -not $StatusOnly) {
  Write-Host "Droplet:    $dropletIp"
  Write-Host "Path:       $serverPath"
  Write-Host "Env file:   $remoteEnvFile (remote)"
  Write-Host "Log file:   $logFile"
  if ($SkipSessionSave) {
    Write-Host "Sessions:   NOT preserved (-SkipSessionSave)" -ForegroundColor Yellow
  }
  else {
    Write-Host "Sessions:   preserved when server is up and ADMIN_PASSWORD is set"
  }
}
Write-Host ""

# Helper: run command on droplet as yaat user
function Invoke-OnDroplet {
  param([string]$Command)
  $sshCmd = "su - $yaatUser -c `"$Command`""
  ssh "$dropletUser@$dropletIp" $sshCmd 2>&1
}

# Helper: probe SSH connectivity to the droplet. Returns $true if reachable.
function Test-DropletReachable {
  $null = ssh -o ConnectTimeout=5 "$dropletUser@$dropletIp" "echo 'OK'" 2>&1
  return ($LASTEXITCODE -eq 0)
}

# Helper: tail the live server logs until the user detaches with Ctrl-C.
function Show-ServerLogs {
  Write-Host "Following server logs (Ctrl-C to detach)..." -ForegroundColor Cyan
  Write-Host ""
  ssh "$dropletUser@$dropletIp" "su - $yaatUser -c `"cd $serverPath && docker compose --env-file $remoteEnvFile logs -f yaat-server`""
}

# Poll the container logs until the server reports it is listening. Relies on a
# freshly recreated container (deploy: up --force-recreate; reboot: same) so the
# "Now listening on" line we match is from the new instance, not a stale one.
function Wait-ServerReady {
  $retry = 0
  $maxRetries = 30
  while ($retry -lt $maxRetries) {
    $null = ssh "$dropletUser@$dropletIp" "su - $yaatUser -c `"cd $serverPath && docker compose --env-file $remoteEnvFile logs yaat-server 2>/dev/null | grep -q 'Now listening on'`"" 2>&1
    if ($LASTEXITCODE -eq 0) {
      return $true
    }
    Write-Host "  Waiting... ($($retry+1)/$maxRetries)"
    Start-Sleep -Seconds 2
    $retry++
  }
  return $false
}

# Restart the server without redeploying. Recreates the yaat-server container from
# the existing image so the app re-runs its on-boot vNAS data refresh (new AIRAC
# cycle is pulled when the serial changed). No git pull, no rebuild, no cache prune.
function Invoke-ServerReboot {
  Write-Host "[1/4] Checking connectivity..." -ForegroundColor Yellow
  if (-not (Test-DropletReachable)) {
    throw "Cannot reach $dropletIp"
  }
  Write-Host "✓ Connected" -ForegroundColor Green

  Write-Host ""
  Write-Host "[2/4] Preparing for restart..." -ForegroundColor Yellow
  $sessionsSaved = $false
  if ($SkipSessionSave) {
    Send-DiscordStatus -Title "Server rebooting" -Description "``$serverUrl`` is rebooting to refresh nav data (sessions not preserved). Expect $estimatedDowntime of downtime." -Color 16776960
  }
  else {
    Send-DiscordStatus -Title "Server rebooting" -Description "``$serverUrl`` is saving active sessions, then rebooting to refresh nav data. Expect $estimatedDowntime of downtime." -Color 16776960
    $sessionsSaved = Invoke-PrepareRestartSessions -DrainSec $DrainSeconds
    if (-not $sessionsSaved) {
      Send-DiscordStatus -Title "Server rebooting" -Description "``$serverUrl`` is rebooting to refresh nav data (session save skipped or failed). Expect $estimatedDowntime of downtime." -Color 16776960
    }
  }

  Write-Host ""
  Write-Host "[3/4] Recreating yaat-server container (re-runs on-boot nav data refresh)..." -ForegroundColor Yellow
  # --force-recreate replaces the container from the current image; named volumes
  # (cache + session-checkpoints) persist. The app's startup vNAS serial-staleness
  # check downloads a new AIRAC cycle if the published serial changed. Commit hashes
  # are baked into the image at build time, so /api/version stays correct.
  Invoke-OnDroplet "cd $serverPath && docker compose --env-file $remoteEnvFile up -d --force-recreate yaat-server" | Tee-Object -Append -FilePath $logFile

  Write-Host ""
  Write-Host "[4/4] Waiting for server to be ready..." -ForegroundColor Yellow
  $ready = Wait-ServerReady

  # No pull happened — report whatever commit is currently deployed.
  $commitHash = (Invoke-OnDroplet "cd $serverPath && git log -1 --format='%h %s'" | Select-Object -First 1).Trim()
  $submoduleHash = (Invoke-OnDroplet "cd $serverPath && git -C extern/yaat log -1 --format='%h %s'" | Select-Object -First 1).Trim()

  if ($ready) {
    Write-Host "✓ Server is ready" -ForegroundColor Green
    $sessionNote = if ($sessionsSaved) { "`nActive sessions were checkpointed and should restore on reconnect." } else { "" }
    Send-DiscordStatus -Title "Server is back up" -Description "``$serverUrl`` is back online after a nav-data refresh.`nServer: ``$commitHash```nClient (yaat): ``$submoduleHash``$sessionNote" -Color 5814783
  }
  else {
    Write-Host "⚠ Server startup check timed out (may still be initializing)" -ForegroundColor Yellow
    Send-DiscordStatus -Title "Server reboot: startup check timed out" -Description "``$serverUrl`` may still be initializing.`nServer: ``$commitHash```nClient (yaat): ``$submoduleHash``" -Color 16744448
  }

  Write-Host ""
  Write-Host "✓ Reboot complete!" -ForegroundColor Green
  Write-Host ""
}

# Read-only status check runs outside the main try/catch so a query failure never sends a
# false "Deployment failed" Discord alert. Exit 0 = queried (rooms or empty), 2 = query failed.
if ($StatusOnly) {
  try {
    Invoke-StatusCheck
    exit 0
  }
  catch {
    Write-Host "⚠ Could not query $serverUrl/admin/status ($_)" -ForegroundColor Yellow
    exit 2
  }
}

try {
  if ($WaitForEmptyRooms) {
    Invoke-WaitForEmptyRooms -PollSec $PollSeconds
    return
  }

  if ($RebootOnly) {
    Invoke-ServerReboot
    if ($followLogs) { Show-ServerLogs }
    return
  }

  # Pre-flight checks
  Write-Host "[1/8] Checking connectivity..." -ForegroundColor Yellow
  if (-not (Test-DropletReachable)) {
    Write-Host "❌ Cannot reach $dropletIp" -ForegroundColor Red
    exit 1
  }
  Write-Host "✓ Connected" -ForegroundColor Green

  Write-Host ""
  Write-Host "[2/8] Checking current status..." -ForegroundColor Yellow
  Invoke-OnDroplet "cd $serverPath && docker compose --env-file $remoteEnvFile ps" | Tee-Object -Append -FilePath $logFile

  Write-Host ""
  Write-Host "[3/8] Preparing for restart..." -ForegroundColor Yellow
  $sessionsSaved = $false
  if ($SkipSessionSave) {
    Send-DiscordStatus -Title "Server going down for deployment" -Description "``$serverUrl`` is being updated (sessions not preserved). Expect $estimatedDowntime of downtime." -Color 16776960
  }
  else {
    Send-DiscordStatus -Title "Server restarting for deployment" -Description "``$serverUrl`` is saving active sessions, then updating. Expect $estimatedDowntime of downtime." -Color 16776960
    $sessionsSaved = Invoke-PrepareRestartSessions -DrainSec $DrainSeconds
    if (-not $sessionsSaved) {
      Send-DiscordStatus -Title "Server going down for deployment" -Description "``$serverUrl`` is being updated (session save skipped or failed). Expect $estimatedDowntime of downtime." -Color 16776960
    }
  }

  Write-Host ""
  Write-Host "[4/8] Pulling latest code and submodules..." -ForegroundColor Yellow
  Invoke-OnDroplet "cd $serverPath && git pull && git submodule update --init --remote --recursive" | Tee-Object -Append -FilePath $logFile
  if ($LASTEXITCODE -ne 0) {
    throw "git pull / submodule update failed on the droplet (exit $LASTEXITCODE). Aborting so a stale build isn't shipped — check 'git -C $serverPath status' for local changes blocking the pull."
  }

  # Get the commit hashes after pulling
  $commitInfo = Invoke-OnDroplet "cd $serverPath && git log -1 --format='%h %s'"
  $commitHash = ($commitInfo | Select-Object -First 1).Trim()
  $submoduleInfo = Invoke-OnDroplet "cd $serverPath && git -C extern/yaat log -1 --format='%h %s'"
  $submoduleHash = ($submoduleInfo | Select-Object -First 1).Trim()

  # Full hashes for /api/version endpoint
  $serverFullHash = (Invoke-OnDroplet "cd $serverPath && git rev-parse HEAD" | Select-Object -First 1).Trim()
  $clientFullHash = (Invoke-OnDroplet "cd $serverPath && git -C extern/yaat rev-parse HEAD" | Select-Object -First 1).Trim()

  Write-Host ""
  Write-Host "[5/8] Rebuilding and recreating services..." -ForegroundColor Yellow
  $buildFlags = if ($NoCache) { "--no-cache " } else { "" }
  # --force-recreate replaces the container; named volumes (cache + session-checkpoints) persist.
  Invoke-OnDroplet "cd $serverPath && YAAT_SERVER_COMMIT=$serverFullHash YAAT_CLIENT_COMMIT=$clientFullHash docker compose --env-file $remoteEnvFile build $buildFlags&& YAAT_SERVER_COMMIT=$serverFullHash YAAT_CLIENT_COMMIT=$clientFullHash docker compose --env-file $remoteEnvFile up -d --force-recreate" | Tee-Object -Append -FilePath $logFile

  Write-Host ""
  Write-Host "[6/8] Waiting for server to be ready..." -ForegroundColor Yellow
  $ready = Wait-ServerReady

  if ($ready) {
    Write-Host "✓ Server is ready" -ForegroundColor Green
    $sessionNote = if ($sessionsSaved) { "`nActive sessions were checkpointed and should restore on reconnect." } else { "" }
    Send-DiscordStatus -Title "Server is back up" -Description "``$serverUrl`` is back online.`nServer: ``$commitHash```nClient (yaat): ``$submoduleHash``$sessionNote" -Color 5814783
  }
  else {
    Write-Host "⚠ Server startup check timed out (may still be initializing)" -ForegroundColor Yellow
    Send-DiscordStatus -Title "Server deployment: startup check timed out" -Description "``$serverUrl`` may still be initializing.`nServer: ``$commitHash```nClient (yaat): ``$submoduleHash``" -Color 16744448
  }

  Write-Host ""
  Write-Host "[7/8] Pruning build cache (reserve ${CacheReserveGb}GB)..." -ForegroundColor Yellow
  Invoke-OnDroplet "cd $serverPath && docker builder prune -f --reserved-space ${CacheReserveGb}GB" | Tee-Object -Append -FilePath $logFile

  Write-Host ""
  Write-Host "[8/8] Done" -ForegroundColor Green
  Write-Host ""
  Write-Host "✓ Deployment complete!" -ForegroundColor Green
  Write-Host ""

  if ($followLogs) { Show-ServerLogs }
}
catch {
  $failTitle = if ($RebootOnly) { "Reboot failed" } else { "Deployment failed" }
  $failVerb = if ($RebootOnly) { "reboot" } else { "deployment" }
  Send-DiscordStatus -Title $failTitle -Description "``$serverUrl`` $failVerb failed.`nError: $_" -Color 16711680
  Write-Host "❌ Error: $_" -ForegroundColor Red
  exit 1
}
