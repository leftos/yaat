# Deploy yaat-server to a DigitalOcean droplet
# Usage: .\deploy-to-droplet.ps1 [-Target <name>] [-NoLogs] [-SkipSessionSave] [-DrainSeconds <sec>]
#        .\deploy-to-droplet.ps1 [-Target <name>] -BuildImageOnly   (build+push the ghcr image in CI, no deploy)
#        .\deploy-to-droplet.ps1 [-Target <name>] -SkipCiBuild [-NoLogs] ...   (deploy the already-built image)
#        .\deploy-to-droplet.ps1 [-Target <name>] -BuildOnDroplet [-NoCache] [-CacheReserveGb <gb>] ...
#        .\deploy-to-droplet.ps1 -Target yaat2 -RebootOnly [-NoLogs] [-SkipSessionSave] [-DrainSeconds <sec>]
#        .\deploy-to-droplet.ps1 [-Target <name>] -StatusOnly    (report active rooms only, no deploy)
#        .\deploy-to-droplet.ps1 [-Target <name>] -KillRoom <roomId>   (force-close one room, no deploy)
#
# Default deploy flow: trigger yaat-server's docker-image.yml workflow (which builds the
# image in GitHub Actions and pushes it to ghcr.io), wait for it, then have the droplet
# pull the image and recreate the container. The running server stays up through the CI
# build and the pull, so downtime is only the container recreate (~1-2 minutes). The
# droplet (4 GB) cannot absorb the WASM AOT compile, which is why the build lives in CI.
# Pulling the private ghcr image requires a one-time `docker login ghcr.io` as the yaat
# user on the droplet, with a classic PAT carrying read:packages.
#
# -BuildOnDroplet  Emergency fallback: build the image on the droplet from source (the
#                  pre-CI behavior) instead of pulling from ghcr. Use when GitHub Actions
#                  or ghcr is down. Expect ~10 minutes of downtime and heavy memory
#                  pressure on the droplet during the build. -NoCache and -CacheReserveGb
#                  only apply to this mode.
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
# -BuildImageOnly  Do NOT deploy. Dispatch yaat-server's docker-image.yml workflow, wait for
#                  it to build and push the image to ghcr.io, then exit without touching the
#                  droplet. The live server is unaffected. Used by the prepare-release flow to
#                  front-load the ~20-minute CI build so the deploy decision (and its room-
#                  occupancy check) can happen right before the actual downtime, not 20 minutes
#                  earlier. Follow up with a normal deploy passing -SkipCiBuild.
#
# -SkipCiBuild  Skip the CI image-build step and deploy whatever ghcr.io currently holds as
#               :latest. Only meaningful right after a -BuildImageOnly run (or another deploy)
#               has produced the intended image — otherwise the droplet redeploys a stale build.
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
#              active rooms (count, scenario, aircraft count) plus one line per member —
#              initials, client kind (main/vstrips/vtdls), how long they have been connected,
#              CID, and connection id — then exit. The per-member detail is what makes a
#              nonzero count actionable: a lone hours-old vstrips member is a forgotten
#              browser tab, not a session worth delaying a deploy for. Used by the
#              prepare-release flow to report room occupancy before asking whether to
#              deploy. Requires ADMIN_PASSWORD (read from .env.<target>/.env). Exit code 0
#              on a successful query (whether or not rooms are active), 2 if the query
#              failed (server unreachable or password unset). Ignores all build/deploy flags.
#
# -KillRoom <roomId>  Do NOT deploy. POST the target's /admin/rooms/<roomId>/close endpoint
#                     to force-close that room: every connected training and CRC client is
#                     evicted with a "closed by the server administrator" notice and the
#                     room's simulation state is torn down immediately, regardless of
#                     occupancy or pause state. The escape hatch for a room held open by a
#                     stuck or crashed client that abandoned-room cleanup (needs all clients
#                     gone) and paused-room retirement (needs an hour paused) never reach.
#                     Get the room id from -StatusOnly. Requires ADMIN_PASSWORD (read from
#                     .env.<target>/.env). Exit code 0 on success, 2 if the close failed
#                     (unknown room, server unreachable, or password unset). Ignores all
#                     build/deploy flags.
#
# -NoCache  (-BuildOnDroplet only) Pass --no-cache to docker compose build, forcing
#           every layer to rebuild from scratch (including the wasm-tools workload
#           install in the Dockerfile, which adds ~1-3 minutes). Off by
#           default — Docker's layer cache is already correctness-
#           preserving when source files change, and the layer cache is
#           what makes incremental deploys quick.
#
# -SkipSessionSave  Skip prepare-restart (immediate container replace; active rooms are lost).
#
# -CacheReserveGb  (-BuildOnDroplet only) After a successful build, prune the BuildKit
#                  cache down to this many GB of most-recently-used layers (default 10).
#                  Every on-droplet build rebuilds source/publish layers, so without this
#                  the cache grows unbounded toward BuildKit's auto ceiling (~57 GB) and
#                  fills the disk. The reserve keeps the current build's base/restore/wasm
#                  layers so the next incremental build stays fast.

param(
  [string]$Target = "yaat1",
  [switch]$NoLogs,
  [switch]$BuildOnDroplet,
  [switch]$NoCache,
  [switch]$SkipSessionSave,
  [switch]$RebootOnly,
  [switch]$WaitForEmptyRooms,
  [switch]$StatusOnly,
  [string]$KillRoom,
  [switch]$BuildImageOnly,
  [switch]$SkipCiBuild,
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
# The default image-pull deploy only recreates the container (the CI build and the pull happen
# while the old server is still up); an on-droplet build rebuilds everything with the server
# down (~7-10 min); a reboot-only recreates from the existing image. This is the user-facing
# outage estimate, distinct from the short -DrainSeconds connection-drain/session-save window.
$estimatedDowntime =
  if ($RebootOnly) { "a few minutes" }
  elseif ($BuildOnDroplet) { "~10 minutes" }
  else { "~2 minutes" }

# The repo whose docker-image.yml workflow builds the deployable image, and the compose
# file set the droplet uses to run from that image instead of building locally.
$serverRepo = "leftos/yaat-server"
$clientRepo = "leftos/yaat"
$composeImageFiles = "-f docker-compose.yml -f docker-compose.image.yml"

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

# How long a member has been connected, as a compact age string. A vStrips tab someone left
# open reads as hours; a controller who just joined reads as minutes.
function Format-MemberAge {
  param($JoinedAtUtc)
  if (-not $JoinedAtUtc) { return "?" }
  $age = (Get-Date).ToUniversalTime() - ([datetime]$JoinedAtUtc).ToUniversalTime()
  if ($age.TotalMinutes -lt 1) { return "{0}s" -f [int]$age.TotalSeconds }
  if ($age.TotalHours -lt 1) { return "{0}m" -f [int]$age.TotalMinutes }
  if ($age.TotalDays -lt 1) { return "{0}h{1:d2}m" -f [int]$age.TotalHours, $age.Minutes }
  return "{0}d{1:d2}h" -f [int]$age.TotalDays, $age.Hours
}

# Format the /admin/status rooms array into a one-line human summary. Shared by the
# single-shot status check and the wait-for-empty poll so both render rooms identically.
# Members carry their client kind because a room whose only member is a vstrips/vtdls
# browser tab is not an occupied training session — the bare initials can't say that.
function Format-RoomsSummary {
  param($Rooms)
  return ($Rooms | ForEach-Object {
      # A server predating per-member detail omits the field entirely. Rendering that as
      # "no members" would read as an empty room and invite a deploy over a live session,
      # so an absent field is reported as unknown rather than zero.
      $members = if ($null -eq $_.PSObject.Properties['members']) { "membership unknown — server predates per-member detail" }
      elseif ($_.members.Count -gt 0) { ($_.members | ForEach-Object { "$($_.initials)($($_.kind))" }) -join "," }
      else { "no members" }
      $scenario = if ($_.scenarioName) { $_.scenarioName } else { "(no scenario)" }
      "$($_.roomId) [$members] $scenario $($_.aircraftCount)ac"
    }) -join "; "
}

# Print one indented line per member so a nonzero count is always explainable: who, which app,
# how long they have been connected, and the connection id that distinguishes two tabs held by
# the same controller.
function Write-RoomsMemberDetail {
  param($Rooms)
  foreach ($room in $Rooms) {
    Write-Host "  $($room.roomId):" -ForegroundColor Cyan
    if ($null -eq $room.PSObject.Properties['members']) {
      Write-Host "    (membership unknown — this server predates per-member detail; deploy to see it)" -ForegroundColor Yellow
      continue
    }

    if ($room.members.Count -eq 0) {
      # Only the fact, not the cause: a room abandoned by its last client is on a short cleanup
      # timer, while one restored across a restart arms no timer and waits for the paused-retirement
      # sweep. /admin/status cannot tell those apart, so it should not claim either.
      Write-Host "    (no members — nobody is connected; the server retires the room on its own schedule)" -ForegroundColor DarkGray
      continue
    }

    foreach ($m in $room.members) {
      Write-Host ("    {0,-4} {1,-7} joined {2,-7} cid={3,-9} conn={4}" -f $m.initials, $m.kind, (Format-MemberAge $m.joinedAtUtc), $m.cid, $m.connectionId)
    }
  }
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
    Write-RoomsMemberDetail $status.rooms
  }
}

# Force-close one room via POST /admin/rooms/{roomId}/close: evicts every connected client
# with a notice and tears the room down immediately. Throws on failure (unknown room id,
# unreachable server, password unset) so the caller can exit 2.
function Invoke-KillRoom {
  param([string]$RoomId)
  if (-not $adminPassword) {
    throw "ADMIN_PASSWORD not set in $envFile — cannot call /admin/rooms/$RoomId/close. Set it (matching the droplet) and retry."
  }

  $headers = @{ "X-Yaat-Admin-Password" = $adminPassword }
  $result = Invoke-RestMethod -Uri "$serverUrl/admin/rooms/$RoomId/close" -Method Post -Headers $headers -TimeoutSec 15
  Write-Host ("✓ Force-closed room {0} (scenario '{1}'): evicted {2} member(s), removed {3} aircraft." -f
    $result.roomId, $result.scenario, $result.membersEvicted, $result.aircraftRemoved) -ForegroundColor Green
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

# Trigger yaat-server's docker-image.yml workflow and block until it succeeds. The image
# lands on ghcr.io tagged :latest (plus a <server>-<client> short-hash tag). Runs entirely
# before any downtime — the live server keeps serving while CI builds. Requires the local
# gh CLI to be authenticated with rights on $serverRepo. Throws on dispatch or build failure.
function Invoke-CiImageBuild {
  $reason = "deploy-$Target-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
  Write-Host "  Dispatching docker-image.yml (reason: $reason)..." -ForegroundColor Gray
  gh workflow run docker-image.yml --repo $serverRepo -f reason=$reason
  if ($LASTEXITCODE -ne 0) {
    throw "gh workflow run docker-image.yml failed (exit $LASTEXITCODE). Is gh authenticated with access to $serverRepo?"
  }

  # The dispatch API returns before the run exists; find ours by the reason baked into run-name.
  $runId = $null
  foreach ($attempt in 1..12) {
    Start-Sleep -Seconds 5
    $runs = gh run list --repo $serverRepo --workflow docker-image.yml --limit 10 --json databaseId,displayTitle | ConvertFrom-Json
    $match = $runs | Where-Object { $_.displayTitle -like "*$reason*" } | Select-Object -First 1
    if ($match) {
      $runId = $match.databaseId
      break
    }
  }
  if (-not $runId) {
    throw "Dispatched docker-image.yml but no matching run appeared within 60s — check https://github.com/$serverRepo/actions"
  }

  Write-Host "  Watching run $runId (the live server stays up during the build)..." -ForegroundColor Gray
  gh run watch $runId --repo $serverRepo --exit-status --interval 15
  if ($LASTEXITCODE -ne 0) {
    throw "docker-image.yml run $runId failed — inspect with: gh run view $runId --repo $serverRepo --log-failed"
  }
  Write-Host "✓ Image built and pushed to ghcr.io/$serverRepo" -ForegroundColor Green
}

# Resolve a commit to "shorthash subject" for the Discord status messages, via the GitHub
# API so it works no matter what the droplet's checkouts contain. Falls back to the bare
# short hash when the lookup fails (offline, unknown sha, non-CI "dev" image).
function Resolve-CommitLabel {
  param([string]$Repo, [string]$Sha)
  $short = $Sha.Substring(0, [Math]::Min(8, $Sha.Length))
  try {
    # Capture all output before taking the first line: piping a native command into
    # `Select-Object -First 1` stops it early and leaves $LASTEXITCODE unset.
    $lines = @(gh api "repos/$Repo/commits/$Sha" --jq '.commit.message' 2>$null)
    if (($LASTEXITCODE -eq 0) -and ($lines.Count -gt 0) -and $lines[0]) {
      return "$short $($lines[0])"
    }
  }
  catch {
    Write-Host "⚠ Could not resolve subject for $Repo@$short : $_" -ForegroundColor Yellow
  }
  return $short
}

# Draft-until-published: release.yml always creates the GitHub Release as a draft,
# so auto-update can't offer a client whose matching server isn't live yet. Once this
# deploy verifies the server is up, publish the draft — but only if the deployed
# client commit IS the tagged release commit; deploying some other main state must
# not make an unmatched release public. (Client-only releases skip the deploy; the
# /prepare-release flow publishes those directly.)
function Publish-DraftClientRelease {
  param([string]$DeployedClientSha)
  $props = Get-Content (Join-Path $PSScriptRoot "Directory.Build.props") -Raw
  if ($props -notmatch "<Version>([^<]+)</Version>") {
    Write-Host "⚠ Could not read <Version> from Directory.Build.props — skipping draft-release publish check." -ForegroundColor Yellow
    return
  }
  $tag = "v$($Matches[1])"
  $viewJson = @(gh release view $tag --repo $clientRepo --json isDraft 2>$null)
  if (($LASTEXITCODE -ne 0) -or ($viewJson.Count -eq 0)) {
    Write-Host "  No GitHub release for $tag — nothing to publish." -ForegroundColor Gray
    return
  }
  if (-not ($viewJson -join "" | ConvertFrom-Json).isDraft) {
    Write-Host "  Release $tag is already published." -ForegroundColor Gray
    return
  }
  $tagSha = (git -C $PSScriptRoot rev-list -1 $tag 2>$null | Select-Object -First 1)
  if ((-not $DeployedClientSha) -or (-not $tagSha) -or ($DeployedClientSha -ne $tagSha)) {
    Write-Host "⚠ Draft release $tag stays hidden: deployed client commit ($DeployedClientSha) is not the tagged release commit ($tagSha)." -ForegroundColor Yellow
    Write-Host "  Publish manually once the matching server is live: gh release edit $tag --repo $clientRepo --draft=false" -ForegroundColor Yellow
    return
  }
  gh release edit $tag --repo $clientRepo --draft=false
  if ($LASTEXITCODE -ne 0) {
    Write-Host "⚠ Failed to publish draft release $tag (gh exit $LASTEXITCODE). Publish manually: gh release edit $tag --repo $clientRepo --draft=false" -ForegroundColor Yellow
    return
  }
  Write-Host "✓ Published GitHub release $tag (was draft pending this deploy)" -ForegroundColor Green
  # No explicit Discord dispatch here: publishing with the operator's user token fires
  # the release:published event, which triggers discord-release.yml on its own.
  # Dispatching as well posts a duplicate announcement.
}

# Announce the imminent restart on Discord and checkpoint active sessions (unless
# -SkipSessionSave). Returns whether sessions were saved. Shared by both deploy paths;
# called as late as possible so downtime starts only when the replacement is ready.
function Invoke-DeployRestartPrep {
  if ($SkipSessionSave) {
    Send-DiscordStatus -Title "Server going down for deployment" -Description "``$serverUrl`` is being updated (sessions not preserved). Expect $estimatedDowntime of downtime." -Color 16776960
    return $false
  }

  Send-DiscordStatus -Title "Server restarting for deployment" -Description "``$serverUrl`` is saving active sessions, then updating. Expect $estimatedDowntime of downtime." -Color 16776960
  $saved = Invoke-PrepareRestartSessions -DrainSec $DrainSeconds
  if (-not $saved) {
    Send-DiscordStatus -Title "Server going down for deployment" -Description "``$serverUrl`` is being updated (session save skipped or failed). Expect $estimatedDowntime of downtime." -Color 16776960
  }
  return $saved
}

$headerTitle =
  if ($KillRoom) { "YAAT Server: force-close room $KillRoom (no deploy)" }
  elseif ($StatusOnly) { "YAAT Server: active-room status (no deploy)" }
  elseif ($WaitForEmptyRooms) { "YAAT Server: wait for empty rooms (no deploy)" }
  elseif ($BuildImageOnly) { "YAAT Server: CI image build only (no deploy)" }
  elseif ($RebootOnly) { "YAAT Server Reboot (refresh nav data, no redeploy)" }
  else { "YAAT Server Deployment" }
Write-Host $headerTitle -ForegroundColor Cyan
Write-Host "=====================" -ForegroundColor Cyan
Write-Host "Target:     $Target ($serverUrl)"
if (-not $WaitForEmptyRooms -and -not $StatusOnly -and -not $KillRoom -and -not $BuildImageOnly) {
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
  # (cache + session-checkpoints + facility-data) persist. The app's startup vNAS serial-staleness
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

# Room force-close runs outside the main try/catch so a failure never sends a false
# "Deployment failed" Discord alert. Exit 0 = room closed, 2 = close failed (unknown room,
# server unreachable, or password unset).
if ($KillRoom) {
  try {
    Invoke-KillRoom -RoomId $KillRoom
    exit 0
  }
  catch {
    Write-Host "⚠ Could not close room $KillRoom via $serverUrl ($_)" -ForegroundColor Yellow
    exit 2
  }
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

# Build-only also runs outside the main try/catch: a CI build failure leaves the live server
# untouched, so it must not send the "Deployment failed" Discord alert.
if ($BuildImageOnly) {
  try {
    Invoke-CiImageBuild
    Write-Host ""
    Write-Host "✓ Image build complete — deploy it with: .\deploy-to-droplet.ps1 -Target $Target -SkipCiBuild -NoLogs" -ForegroundColor Green
    exit 0
  }
  catch {
    Write-Host "❌ CI image build failed: $_" -ForegroundColor Red
    exit 1
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
  Write-Host "[1/9] Checking connectivity..." -ForegroundColor Yellow
  if (-not (Test-DropletReachable)) {
    Write-Host "❌ Cannot reach $dropletIp" -ForegroundColor Red
    exit 1
  }
  Write-Host "✓ Connected" -ForegroundColor Green

  Write-Host ""
  Write-Host "[2/9] Checking current status..." -ForegroundColor Yellow
  Invoke-OnDroplet "cd $serverPath && docker compose --env-file $remoteEnvFile ps" | Tee-Object -Append -FilePath $logFile

  if ($BuildOnDroplet) {
    # Emergency fallback: build from source on the droplet. Sessions are saved up front
    # because the build itself starves the running server of CPU and memory.
    Write-Host ""
    Write-Host "[3/9] Preparing for restart..." -ForegroundColor Yellow
    $sessionsSaved = Invoke-DeployRestartPrep

    Write-Host ""
    Write-Host "[4/9] Pulling latest code and submodules..." -ForegroundColor Yellow
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
    Write-Host "[5/9] Rebuilding and recreating services..." -ForegroundColor Yellow
    $buildFlags = if ($NoCache) { "--no-cache " } else { "" }
    # --force-recreate replaces the container; named volumes (cache + session-checkpoints +
    # facility-data, the last holding controller-drawn ASDE-X/SAID geometry) persist. A volume
    # newly declared in docker-compose.yml is created by `up`, so no extra step on first deploy.
    Invoke-OnDroplet "cd $serverPath && YAAT_SERVER_COMMIT=$serverFullHash YAAT_CLIENT_COMMIT=$clientFullHash docker compose --env-file $remoteEnvFile build $buildFlags&& YAAT_SERVER_COMMIT=$serverFullHash YAAT_CLIENT_COMMIT=$clientFullHash docker compose --env-file $remoteEnvFile up -d --force-recreate" | Tee-Object -Append -FilePath $logFile
  }
  else {
    # Default flow: build in CI, pull on the droplet. Everything up to the container
    # recreate happens while the old server is still serving, so downtime is minimal.
    Write-Host ""
    if ($SkipCiBuild) {
      Write-Host "[3/9] Skipping CI image build (-SkipCiBuild) — deploying the current ghcr :latest image..." -ForegroundColor Yellow
    }
    else {
      Write-Host "[3/9] Building image in CI (server stays up)..." -ForegroundColor Yellow
      Invoke-CiImageBuild
    }

    Write-Host ""
    Write-Host "[4/9] Pulling latest code on the droplet..." -ForegroundColor Yellow
    # Compose files, Caddyfile, and the deploy override come from git; the app itself
    # ships inside the image, so no submodule update is needed on this path.
    Invoke-OnDroplet "cd $serverPath && git pull" | Tee-Object -Append -FilePath $logFile
    if ($LASTEXITCODE -ne 0) {
      throw "git pull failed on the droplet (exit $LASTEXITCODE) — check 'git -C $serverPath status' for local changes blocking the pull."
    }

    Write-Host ""
    Write-Host "[5/9] Pulling image on the droplet (server stays up)..." -ForegroundColor Yellow
    Invoke-OnDroplet "cd $serverPath && docker compose $composeImageFiles --env-file $remoteEnvFile pull yaat-server" | Tee-Object -Append -FilePath $logFile
    if ($LASTEXITCODE -ne 0) {
      throw "docker compose pull failed (exit $LASTEXITCODE). If this is an auth error, run a one-time 'docker login ghcr.io' as the $yaatUser user on the droplet with a classic PAT carrying read:packages."
    }

    Write-Host ""
    Write-Host "[6/9] Preparing for restart..." -ForegroundColor Yellow
    $sessionsSaved = Invoke-DeployRestartPrep

    Write-Host ""
    Write-Host "[7/9] Recreating services from the pulled image..." -ForegroundColor Yellow
    Invoke-OnDroplet "cd $serverPath && docker compose $composeImageFiles --env-file $remoteEnvFile up -d --force-recreate --no-build" | Tee-Object -Append -FilePath $logFile
  }

  Write-Host ""
  Write-Host "[8/9] Waiting for server to be ready..." -ForegroundColor Yellow
  $ready = Wait-ServerReady

  $deployedClientSha = if ($BuildOnDroplet) { $clientFullHash } else { $null }
  if (-not $BuildOnDroplet) {
    # The image path never inspects git on the droplet — report what the new server
    # actually runs, from the commit hashes baked into the image at CI build time.
    $commitHash = "unknown"
    $submoduleHash = "unknown"
    try {
      $version = Invoke-RestMethod -Uri "$serverUrl/api/version" -Method Get -TimeoutSec 10
      $deployedClientSha = $version.client
      $commitHash = Resolve-CommitLabel -Repo $serverRepo -Sha $version.server
      $submoduleHash = Resolve-CommitLabel -Repo $clientRepo -Sha $version.client
    }
    catch {
      Write-Host "⚠ Could not query $serverUrl/api/version for deployed commit hashes: $_" -ForegroundColor Yellow
    }
  }

  if ($ready) {
    Write-Host "✓ Server is ready" -ForegroundColor Green
    $sessionNote = if ($sessionsSaved) { "`nActive sessions were checkpointed and should restore on reconnect." } else { "" }
    Send-DiscordStatus -Title "Server is back up" -Description "``$serverUrl`` is back online.`nServer: ``$commitHash```nClient (yaat): ``$submoduleHash``$sessionNote" -Color 5814783
    Publish-DraftClientRelease -DeployedClientSha $deployedClientSha
  }
  else {
    Write-Host "⚠ Server startup check timed out (may still be initializing)" -ForegroundColor Yellow
    Send-DiscordStatus -Title "Server deployment: startup check timed out" -Description "``$serverUrl`` may still be initializing.`nServer: ``$commitHash```nClient (yaat): ``$submoduleHash``" -Color 16744448
  }

  Write-Host ""
  if ($BuildOnDroplet) {
    Write-Host "[9/9] Pruning build cache (reserve ${CacheReserveGb}GB)..." -ForegroundColor Yellow
    Invoke-OnDroplet "cd $serverPath && docker builder prune -f --reserved-space ${CacheReserveGb}GB" | Tee-Object -Append -FilePath $logFile
  }
  else {
    Write-Host "[9/9] Pruning superseded images..." -ForegroundColor Yellow
    # Each pull strands the previous :latest as an untagged image (~0.5 GB); dangling-only
    # prune reclaims them without touching tagged images or the build cache.
    Invoke-OnDroplet "docker image prune -f" | Tee-Object -Append -FilePath $logFile
  }

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
