# Pulls the raw SWIM feed window covering a real-world report off a deployment and cuts it down to one facility.
#
# Usage:
#   .\swim-slice.ps1 -From 2026-08-28T17:40:00Z -To 2026-08-28T17:55:00Z [-Artcc ZOA [-Facility NCT]] [-Target yaat1] [-Out <dir>]
#   .\swim-slice.ps1 -From ... -To ... -Local <dir-with-raw-logs> [-Artcc ZOA [-Facility NCT]]
#
# The server keeps a rolling raw log of every SCDS message (Swim__RawLog__* in docker-compose.yml, ~24 h) in the
# yaat-swim-raw volume. This script lists the per-product hour files on the droplet, copies the ones that overlap the
# window (plus the hour before it, so flight plans filed before the window are there), and runs
# `yaat-server/tools/Yaat.SwimSlice cut` on them. With -Local it skips the fetch and cuts files already on disk.
#
# Output: <Out>/raw/ (the fetched hour files) and <Out>/slice/ (the cut), default Out = yaat-server/.tmp/swim-slices/<stamp>.
# Raw captures and slices are FAA data: they stay on this machine (.tmp/ is gitignored) and are never attached to issues.
# Next steps: `dotnet run --project tools/Yaat.SwimSlice -- trace --callsign X <Out>/slice` and the SwimReplayHarness
# tests (yaat-server docs/plans/live-traffic-swim/07-repro-harness.md).

param(
  [Parameter(Mandatory = $true)][string]$From,
  [Parameter(Mandatory = $true)][string]$To,
  [string]$Artcc,
  [string]$Facility,
  [string]$Target = "yaat1",
  [string]$Local,
  [string]$Out
)

$ErrorActionPreference = "Stop"
$serverRepo = Join-Path (Split-Path $PSScriptRoot -Parent) "yaat-server"
$fromUtc = [datetimeoffset]::Parse($From, [cultureinfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::AssumeUniversal).ToUniversalTime()
$toUtc = [datetimeoffset]::Parse($To, [cultureinfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::AssumeUniversal).ToUniversalTime()
if ($toUtc -le $fromUtc) {
  throw "-To must be after -From."
}
if ($Facility -and -not $Artcc) {
  throw "-Facility needs -Artcc."
}

if (-not $Out) {
  $Out = Join-Path $serverRepo ".tmp" "swim-slices" ($fromUtc.ToString("yyyyMMddTHHmmZ") + $(if ($Facility) { "-$Facility" } elseif ($Artcc) { "-$Artcc" } else { "" }))
}
$rawDir = Join-Path $Out "raw"
$sliceDir = Join-Path $Out "slice"
if (Test-Path $sliceDir) {
  throw "$sliceDir already exists; pick another -Out or remove it."
}

if ($Local) {
  if (-not (Test-Path $Local)) {
    throw "-Local '$Local' does not exist."
  }
  $rawDir = $Local
  Write-Host "swim-slice: cutting local raw logs in $rawDir"
} else {
  . (Join-Path $PSScriptRoot "deploy-targets.ps1")
  $cfg = Resolve-DeployTarget -Target $Target
  $dropletIp = $cfg.DropletIp
  Write-Host "swim-slice -> $Target ($dropletIp): window $($fromUtc.ToString('u')) .. $($toUtc.ToString('u'))"

  # The volume's mount point on the host, then the file list: "<product>-<epoch>.jsonlines.br <bytes>" per line.
  $listScript = @'
set -e
mp=$(docker volume inspect --format '{{ .Mountpoint }}' $(docker volume ls -q | grep -m1 'yaat-swim-raw$'))
cd "$mp"
for f in *.jsonlines.br; do [ -e "$f" ] && printf '%s %s\n' "$f" "$(stat -c %s "$f")"; done
'@
  $listing = ssh "root@$dropletIp" $listScript
  if ($LASTEXITCODE -ne 0) {
    throw "Listing the raw log on $dropletIp failed (ssh exit $LASTEXITCODE)."
  }

  # A file covers from its epoch until the next file of the same product; keep the ones overlapping [From - 1 h, To].
  $files = @()
  foreach ($line in $listing) {
    if ($line -match '^(?<product>[a-z]+)-(?<epoch>\d+)\.jsonlines\.br (?<bytes>\d+)$') {
      $files += [pscustomobject]@{ Name = "$($Matches.product)-$($Matches.epoch).jsonlines.br"; Product = $Matches.product; Epoch = [long]$Matches.epoch; Bytes = [long]$Matches.bytes }
    }
  }
  if ($files.Count -eq 0) {
    throw "No raw-log files on $dropletIp — is Swim__RawLog__Directory set and the feed running?"
  }
  $windowStart = $fromUtc.AddHours(-1).ToUnixTimeSeconds()
  $windowEnd = $toUtc.ToUnixTimeSeconds()
  $wanted = @()
  foreach ($group in ($files | Group-Object Product)) {
    $sorted = @($group.Group | Sort-Object Epoch)
    for ($i = 0; $i -lt $sorted.Count; $i++) {
      $start = $sorted[$i].Epoch
      $end = if ($i + 1 -lt $sorted.Count) { $sorted[$i + 1].Epoch } else { [long]::MaxValue }
      if (($start -le $windowEnd) -and ($end -ge $windowStart)) {
        $wanted += $sorted[$i]
      }
    }
  }
  if ($wanted.Count -eq 0) {
    $oldest = [datetimeoffset]::FromUnixTimeSeconds(($files | Measure-Object Epoch -Minimum).Minimum)
    throw "The raw log on $dropletIp starts at $($oldest.ToString('u')); the window is outside the retained span (SCDS keeps no history)."
  }

  New-Item -ItemType Directory -Force $rawDir | Out-Null
  $mountPoint = ssh "root@$dropletIp" "docker volume inspect --format '{{ .Mountpoint }}' `$(docker volume ls -q | grep -m1 'yaat-swim-raw$')"
  $total = ($wanted | Measure-Object Bytes -Sum).Sum
  Write-Host "  fetching $($wanted.Count) file(s), $([math]::Round($total / 1MB)) MB, into $rawDir"
  foreach ($file in $wanted) {
    $dest = Join-Path $rawDir $file.Name
    if ((Test-Path $dest) -and ((Get-Item $dest).Length -eq $file.Bytes)) {
      Write-Host "    $($file.Name) (cached)"
      continue
    }
    Write-Host "    $($file.Name) $([math]::Round($file.Bytes / 1MB)) MB"
    scp -q "root@${dropletIp}:$mountPoint/$($file.Name)" $dest
    if ($LASTEXITCODE -ne 0) {
      throw "scp of $($file.Name) failed (exit $LASTEXITCODE)."
    }
  }
}

$toolArgs = @("cut", "--from", $fromUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"), "--to", $toUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"), "--out", $sliceDir)
if ($Artcc) {
  $toolArgs += @("--artcc", $Artcc)
  if ($Facility) {
    $toolArgs += @("--facility", $Facility)
  }
}
$toolArgs += $rawDir
Write-Host "  cutting -> $sliceDir"
Push-Location $serverRepo
try {
  dotnet run --project tools/Yaat.SwimSlice -- @toolArgs
  if ($LASTEXITCODE -ne 0) {
    throw "Yaat.SwimSlice cut failed (exit $LASTEXITCODE)."
  }
} finally {
  Pop-Location
}
Write-Host "Done. Trace a callsign with: dotnet run --project tools/Yaat.SwimSlice -- trace --callsign <id> `"$sliceDir`"  (in yaat-server)"
