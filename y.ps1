#Requires -Version 7.0
<#
.SYNOPSIS
    YAAT dev superscript -- single entry point for build, launch, test, deploy,
    log tailing, server status, and the new session-persistence test loop
    (prepare-restart + checkpoint inspection).

.DESCRIPTION
    Subcommands (run `.\y.ps1 help` for the same list at runtime):

      launch          Build + run yaat-server and yaat-client side by side.
                      Forwards remaining args to start.ps1 (e.g. -Release,
                      -ClientOnly, -VStrips, -Scenario, -Sync).
      build           dotnet build both repos. -Release for Release config.
                      -Server / -Client to build just one side (default: both).
      clean           dotnet clean both repos.
      format          Run prek (the configured pre-commit chain: csharpier +
                      analyzers + warnings-as-errors build).
      test            tools/test-all.ps1 -- build + test yaat AND yaat-server.
      deploy          Forwards to deploy-to-droplet.ps1 (default: prepare
                      sessions, rebuild, deploy). Pass -SkipSessionSave,
                      -DrainSeconds, -NoCache, -NoLogs as needed.
      setup-crc       Forwards to Setup-CrcEnvironment.ps1 -- registers a
                      dev YAAT server in CRC's DevEnvironments.json.
      logs            `logs server [-Follow] [-Lines N]` or
                      `logs client [-Follow] [-Lines N]`. Tails the file
                      logs from the standard locations.
      status          GET /api/version + report checkpoint dir contents.
      prepare-restart POST /admin/prepare-restart to a running local server
                      (drain + checkpoint every active room). Reads the
                      admin password from -Password, $env:YAAT_ADMIN_PASSWORD,
                      yaat/.env, yaat-server/.env, then yaat-server's
                      appsettings.*.json files.
      restart-loop    The full session-persistence test loop: prepare-restart,
                      POST /shutdown, wait for the server to die, prompt you
                      to relaunch it (`y.ps1 launch -ServerOnly` works), then
                      wait for /api/version to come back up.
      ckpt            `ckpt ls` -- list checkpoints under the checkpoint dir.
                      `ckpt show [roomId]` -- dump manifest.json for one or
                      all checkpoints. `ckpt clean [-IncludeRestored]` -- rm
                      the checkpoint dir (and optionally the restored
                      archives under session-checkpoints-restored-*).
      help            Print this summary.

    Default (no subcommand) is `help`.

.EXAMPLE
    .\y.ps1                              # help
    .\y.ps1 launch -ServerOnly -Release
    .\y.ps1 test
    .\y.ps1 prepare-restart -Drain 5
    .\y.ps1 restart-loop -Drain 5
    .\y.ps1 status
    .\y.ps1 ckpt ls
    .\y.ps1 ckpt show
    .\y.ps1 ckpt clean -IncludeRestored
    .\y.ps1 logs server -Follow
    .\y.ps1 deploy -SkipSessionSave
#>
[CmdletBinding()]
# Write-Host is intentional: this is an interactive dev script and colored
# status output is the UX. Same pattern as start.ps1 / deploy-to-droplet.ps1.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '',
    Justification = 'Interactive dev script; colored status to console is the UX.')]
# Top-level params are forwarded into subcommand functions via -Args splatting
# or read from $script: scope; PSScriptAnalyzer can't trace those paths.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '',
    Justification = 'Top-level params consumed by sub-functions.')]
param(
    [Parameter(Position = 0)]
    [ValidateSet('', 'help', 'launch', 'build', 'clean', 'format', 'test', 'deploy',
        'setup-crc', 'logs', 'status', 'prepare-restart', 'restart-loop', 'ckpt')]
    [string]$Command = '',

    # Everything after the subcommand. Subcommands parse what they need; the
    # rest forwards to underlying tools (start.ps1, deploy-to-droplet.ps1, ...).
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Rest
)

$ErrorActionPreference = 'Stop'

$script:RepoRoot = $PSScriptRoot
$script:ServerRepo = Join-Path (Split-Path $script:RepoRoot) 'yaat-server'
$script:DefaultServerUrl = 'http://localhost:5000'

# Remote droplet defaults -- mirrors deploy-to-droplet.ps1 / watch-server-logs.ps1
# so `-Remote` is a one-flag shortcut to the production server without having to
# remember the URL / SSH user / container name.
$script:RemoteServerUrl  = 'https://yaat1.leftos.dev'
$script:RemoteSshUser    = 'root'
$script:RemoteYaatUser   = 'yaat'
$script:RemoteServerPath = '/home/yaat/yaat-server'
$script:RemoteServerHost = 'yaat1.leftos.dev'

# --- shared helpers ---

function Write-Section {
    param([string]$Title)
    Write-Host ''
    Write-Host "== $Title ==" -ForegroundColor Cyan
}

# PowerShell array splatting passes flag-like strings ("-ServerOnly") as
# positional values, not as switch arguments, when the target is a .ps1 script.
# This walks $Rest with a simple heuristic to produce both a hashtable (for
# named/switch params) and an array of positional leftovers, which CAN be
# splatted into a script and bound correctly.
function ConvertTo-SplatArg {
    param([string[]]$ArgList)
    $hash = @{}
    $positional = @()
    $i = 0
    while ($i -lt $ArgList.Count) {
        $a = $ArgList[$i]
        if ($a -match '^-([A-Za-z][A-Za-z0-9]*)$') {
            $name = $Matches[1]
            $next = if ($i + 1 -lt $ArgList.Count) { $ArgList[$i + 1] } else { $null }
            $isFlag = ($null -eq $next) -or $next.StartsWith('-')
            if ($isFlag) {
                $hash[$name] = $true
                $i += 1
            } else {
                $hash[$name] = $next
                $i += 2
            }
        } else {
            $positional += $a
            $i += 1
        }
    }
    return @{ Named = $hash; Positional = $positional }
}

function Resolve-CheckpointDir {
    if ($env:Yaat__SessionCheckpointPath) {
        return $env:Yaat__SessionCheckpointPath
    }
    if ($env:YAAT_SESSION_CHECKPOINT_PATH) {
        return $env:YAAT_SESSION_CHECKPOINT_PATH
    }
    return Join-Path $env:LOCALAPPDATA 'yaat\session-checkpoints'
}

function Get-AdminPasswordFromEnvFile {
    param([string]$Path)
    if (-not (Test-Path $Path)) { return $null }
    foreach ($line in Get-Content $Path) {
        if ($line -match '^ADMIN_PASSWORD=(.+)$') {
            return $Matches[1].Trim().Trim('"').Trim("'")
        }
    }
    return $null
}

function Get-AdminPasswordFromAppsetting {
    param([string]$Path)
    if (-not (Test-Path $Path)) { return $null }
    try {
        $json = Get-Content $Path -Raw | ConvertFrom-Json
        $pw = $json.Yaat.AdminPassword
        if ($pw) { return [string]$pw }
    } catch {
        Write-Verbose "Could not parse $Path for AdminPassword: $_"
    }
    return $null
}

function Resolve-AdminPassword {
    param([string]$Override)
    if ($Override) { return $Override }
    if ($env:YAAT_ADMIN_PASSWORD) { return $env:YAAT_ADMIN_PASSWORD }
    if ($env:Yaat__AdminPassword) { return $env:Yaat__AdminPassword }

    foreach ($envFile in @(
        (Join-Path $script:RepoRoot   '.env'),
        (Join-Path $script:ServerRepo '.env')
    )) {
        $pw = Get-AdminPasswordFromEnvFile $envFile
        if ($pw) { return $pw }
    }

    foreach ($settings in @(
        (Join-Path $script:ServerRepo 'src\Yaat.Server\appsettings.Local.json'),
        (Join-Path $script:ServerRepo 'src\Yaat.Server\appsettings.Development.json'),
        (Join-Path $script:ServerRepo 'src\Yaat.Server\appsettings.json')
    )) {
        $pw = Get-AdminPasswordFromAppsetting $settings
        if ($pw) { return $pw }
    }

    return $null
}

function Test-ServerReachable {
    param([string]$Url)
    try {
        $null = Invoke-WebRequest -Uri "$Url/api/version" -Method Get -TimeoutSec 3 -UseBasicParsing
        return $true
    } catch {
        return $false
    }
}

function Wait-For-Server {
    param(
        [string]$Url,
        [int]$TimeoutSec,
        [switch]$WantUp
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        $up = Test-ServerReachable -Url $Url
        if ($up -and $WantUp)        { return $true }
        if (-not $up -and -not $WantUp) { return $true }
        Start-Sleep -Seconds 1
    }
    return $false
}

# --- subcommand: help ---

function Invoke-Help {
    Write-Host @'
YAAT dev superscript

Usage: .\y.ps1 <subcommand> [args]

Lifecycle
  launch [start.ps1 args]   Build + run server and client. Forwards args.
  build [-Release] [-Server] [-Client]
                            dotnet build both repos (or just one side).
  clean                     dotnet clean both repos.
  format                    Run prek (csharpier + analyzers + warn-as-errors).
  test                      tools/test-all.ps1 (yaat + yaat-server).
  deploy [deploy.ps1 args]  Deploy to droplet. Forwards args.
  setup-crc                 Setup-CrcEnvironment.ps1 dispatcher.

Inspection
  logs server   [-Lines N] [-Follow] [-Remote [host]]
  logs client   [-Lines N] [-Follow]
  status        [-Url <url>] [-Remote]

Session persistence
  prepare-restart [-Drain N] [-Url <url>] [-Password <pw>]
  restart-loop    [-Drain N] [-Url <url>] [-Password <pw>] [-Timeout N]
  ckpt ls
  ckpt show [<roomId>]
  ckpt clean [-IncludeRestored]

Defaults
  URL:        http://localhost:5000 (override with -Url <url>)
  Remote:     -Remote shortcut => https://yaat1.leftos.dev for status,
              ssh root@yaat1.leftos.dev -> docker compose logs yaat-server
              for `logs server`.
  Drain:      5 seconds (clamped server-side to [5, 300])
  Password:   -Password > $env:YAAT_ADMIN_PASSWORD > yaat/.env >
              yaat-server/.env > yaat-server appsettings.{Local,Development,*}.json
  CkptDir:    $env:Yaat__SessionCheckpointPath or
              %LOCALAPPDATA%\yaat\session-checkpoints
'@
}

# --- subcommand: launch / test / deploy / setup-crc passthrough ---

function Invoke-Forward {
    param([string]$ScriptPath)
    $parsed = ConvertTo-SplatArg -ArgList $script:Rest
    # Splatting requires variables, not subexpressions -- bind first, then @ them.
    $named      = $parsed.Named
    $positional = $parsed.Positional
    & $ScriptPath @named @positional
    exit $LASTEXITCODE
}

function Invoke-Launch    { Invoke-Forward (Join-Path $script:RepoRoot 'start.ps1') }
function Invoke-Test      { Invoke-Forward (Join-Path $script:RepoRoot 'tools\test-all.ps1') }
function Invoke-Deploy    { Invoke-Forward (Join-Path $script:RepoRoot 'deploy-to-droplet.ps1') }
function Invoke-SetupCrc  { Invoke-Forward (Join-Path $script:RepoRoot 'Setup-CrcEnvironment.ps1') }

# --- subcommand: build / clean / format ---

function Invoke-Build {
    $release  = $script:Rest -contains '-Release'
    $serverOnly = $script:Rest -contains '-Server'
    $clientOnly = $script:Rest -contains '-Client'
    $config = if ($release) { 'Release' } else { 'Debug' }

    if (-not (Test-Path (Join-Path $script:RepoRoot '.tmp'))) {
        New-Item -ItemType Directory -Path (Join-Path $script:RepoRoot '.tmp') | Out-Null
    }

    $buildLog = Join-Path $script:RepoRoot '.tmp\build.log'
    if (Test-Path $buildLog) { Remove-Item $buildLog -Force }

    if (-not $clientOnly) {
        Write-Section "Build yaat-server ($config)"
        dotnet build (Join-Path $script:ServerRepo 'src\Yaat.Server') -c $config -p:TreatWarningsAsErrors=true 2>&1 |
            Tee-Object -Append -FilePath $buildLog
        if ($LASTEXITCODE -ne 0) { Write-Error 'yaat-server build failed'; exit 1 }
    }

    if (-not $serverOnly) {
        Write-Section "Build yaat ($config)"
        dotnet build (Join-Path $script:RepoRoot 'yaat.slnx') -c $config -p:TreatWarningsAsErrors=true 2>&1 |
            Tee-Object -Append -FilePath $buildLog
        if ($LASTEXITCODE -ne 0) { Write-Error 'yaat build failed'; exit 1 }
    }

    Write-Host ''
    Write-Host "Build log: $buildLog" -ForegroundColor Gray
}

function Invoke-Clean {
    Write-Section 'Clean yaat-server'
    dotnet clean (Join-Path $script:ServerRepo 'src\Yaat.Server')
    Write-Section 'Clean yaat'
    dotnet clean (Join-Path $script:RepoRoot 'yaat.slnx')
}

function Invoke-Format {
    Write-Section 'prek run (csharpier + analyzers + warn-as-errors)'
    prek run @script:Rest
    exit $LASTEXITCODE
}

# --- subcommand: logs ---

function Get-LogPath {
    param([string]$Target)
    switch ($Target) {
        'server' { return Join-Path $script:ServerRepo 'src\Yaat.Server\bin\Debug\net10.0\yaat-server.log' }
        'client' { return Join-Path $env:LOCALAPPDATA 'yaat\yaat-client.log' }
        default  { return $null }
    }
}

function Invoke-RemoteServerLog {
    param(
        [string]$DropletHost,
        [int]$Lines,
        [bool]$Follow
    )
    $followFlag = if ($Follow) { '-f' } else { '' }
    $cmd = "cd $script:RemoteServerPath && docker compose logs $followFlag --tail $Lines yaat-server"
    # Wrap in `su - <yaatuser> -c '...'` exactly like watch-server-logs.ps1 does --
    # the docker socket is accessible via the yaat user, not root directly.
    $remote = "su - $script:RemoteYaatUser -c `"$cmd`""
    Write-Host "ssh $script:RemoteSshUser@$DropletHost  ($($cmd.Trim()))" -ForegroundColor Cyan
    ssh "$script:RemoteSshUser@$DropletHost" $remote
}

function Invoke-Log {
    if ($script:Rest.Count -lt 1 -or $script:Rest[0] -notin @('server', 'client')) {
        Write-Error 'Usage: y.ps1 logs server|client [-Lines N] [-Follow] [-Remote [host]]'
        exit 2
    }
    $target = $script:Rest[0]

    $lines  = 200
    $follow = $false
    $remote = $false
    $dropletHost = $script:RemoteServerHost

    for ($i = 1; $i -lt $script:Rest.Count; $i++) {
        switch ($script:Rest[$i]) {
            '-Lines'  { $lines = [int]$script:Rest[$i + 1]; $i++ }
            '-Follow' { $follow = $true }
            '-Remote' {
                $remote = $true
                $next = $script:Rest[$i + 1]
                if ($next -and -not $next.StartsWith('-')) {
                    $dropletHost = $next; $i++
                }
            }
            default { Write-Warning "Unknown logs arg: $($script:Rest[$i])" }
        }
    }

    if ($remote) {
        if ($target -eq 'client') {
            Write-Error 'logs client -Remote is meaningless: client logs live on the operator''s machine, not the droplet.'
            exit 2
        }
        Invoke-RemoteServerLog -DropletHost $dropletHost -Lines $lines -Follow:$follow
        return
    }

    $path = Get-LogPath -Target $target
    if (-not (Test-Path $path)) {
        Write-Warning "Log not found: $path"
        Write-Host '(server logs only exist after a Debug build has run; client logs exist after the client has launched once.)' -ForegroundColor Gray
        exit 1
    }

    $followNote = if ($follow) { ', follow' } else { '' }
    Write-Host "Tailing $path (last $lines lines$followNote)" -ForegroundColor Cyan
    if ($follow) {
        Get-Content -Path $path -Tail $lines -Wait
    } else {
        Get-Content -Path $path -Tail $lines
    }
}

# --- subcommand: status ---

function Invoke-Status {
    $url = $script:DefaultServerUrl
    $remote = $false
    for ($i = 0; $i -lt $script:Rest.Count; $i++) {
        switch ($script:Rest[$i]) {
            '-Url'    { $url = $script:Rest[$i + 1]; $i++ }
            '-Remote' { $remote = $true; $url = $script:RemoteServerUrl }
        }
    }
    $url = $url.TrimEnd('/')

    Write-Section "Server: $url"
    try {
        $version = Invoke-RestMethod -Uri "$url/api/version" -Method Get -TimeoutSec 5
        Write-Host "  reachable, server=$($version.server) client=$($version.client)" -ForegroundColor Green
    } catch {
        Write-Host "  unreachable ($($_.Exception.Message))" -ForegroundColor Yellow
    }

    if ($remote) {
        Write-Section "Remote droplet ($script:RemoteServerHost)"
        Write-Host "  Checkpoint dir / restored archives live on the droplet's named volume" -ForegroundColor Gray
        Write-Host "  ($script:RemoteServerPath, yaat-session-checkpoints docker volume) -- not inspectable from here." -ForegroundColor Gray
        Write-Host "  Use `.\y.ps1 logs server -Remote -Follow` to watch the deploy/restore log live." -ForegroundColor Gray
        return
    }

    $ckptDir = Resolve-CheckpointDir
    Write-Section "Checkpoint dir: $ckptDir"
    if (Test-Path $ckptDir) {
        $zips = @(Get-ChildItem $ckptDir -Filter '*.checkpoint.zip' -ErrorAction SilentlyContinue)
        $color = $zips.Count -gt 0 ? 'Yellow' : 'Gray'
        Write-Host "  $($zips.Count) pending checkpoint(s)" -ForegroundColor $color
        foreach ($z in $zips) {
            Write-Host ("    {0,-50}  {1,10:N0} bytes  {2:s}" -f $z.Name, $z.Length, $z.LastWriteTimeUtc)
        }
    } else {
        Write-Host '  (does not exist yet)' -ForegroundColor Gray
    }

    $parent = Split-Path $ckptDir -Parent
    if (Test-Path $parent) {
        $archives = @(Get-ChildItem $parent -Filter 'session-checkpoints-restored-*' -Directory -ErrorAction SilentlyContinue |
            Sort-Object CreationTimeUtc -Descending)
        Write-Section 'Restored archives'
        if ($archives.Count -eq 0) {
            Write-Host '  (none)' -ForegroundColor Gray
        } else {
            foreach ($a in $archives) {
                $count = @(Get-ChildItem $a.FullName -Filter '*.checkpoint.zip' -ErrorAction SilentlyContinue).Count
                Write-Host ("  {0}  ({1} ckpt)" -f $a.Name, $count)
            }
        }
    }
}

# --- subcommand: prepare-restart ---

function Read-RestartArg {
    $opts = @{ Drain = 5; Url = $script:DefaultServerUrl; Password = $null; Timeout = 120 }
    for ($i = 0; $i -lt $script:Rest.Count; $i++) {
        switch ($script:Rest[$i]) {
            '-Drain'    { $opts.Drain    = [int]$script:Rest[$i + 1]; $i++ }
            '-Url'      { $opts.Url      = $script:Rest[$i + 1];     $i++ }
            '-Password' { $opts.Password = $script:Rest[$i + 1];     $i++ }
            '-Timeout'  { $opts.Timeout  = [int]$script:Rest[$i + 1]; $i++ }
            default     { Write-Warning "Ignoring arg: $($script:Rest[$i])" }
        }
    }
    $opts.Url = $opts.Url.TrimEnd('/')
    return $opts
}

function Invoke-PrepareRestart {
    $opts = Read-RestartArg

    $pw = Resolve-AdminPassword -Override $opts.Password
    if (-not $pw) {
        Write-Error @'
No admin password resolved. Tried:
  -Password, $env:YAAT_ADMIN_PASSWORD, $env:Yaat__AdminPassword,
  yaat/.env (ADMIN_PASSWORD=), yaat-server/.env (ADMIN_PASSWORD=),
  yaat-server appsettings.{Local,Development,*}.json (Yaat.AdminPassword).
'@
        exit 1
    }

    if (-not (Test-ServerReachable -Url $opts.Url)) {
        Write-Error "Server not reachable at $($opts.Url)"
        exit 1
    }

    Write-Section "POST $($opts.Url)/admin/prepare-restart?drainSeconds=$($opts.Drain)"
    Write-Host '  (this blocks until checkpoints are written; expect ~drain seconds)' -ForegroundColor Gray

    $headers = @{ 'X-Yaat-Admin-Password' = $pw }
    $timeoutSec = $opts.Drain + 120

    try {
        $result = Invoke-RestMethod `
            -Uri "$($opts.Url)/admin/prepare-restart?drainSeconds=$($opts.Drain)" `
            -Method Post -Headers $headers -TimeoutSec $timeoutSec
    } catch {
        Write-Error "prepare-restart request failed: $_"
        exit 1
    }

    if ($result.success) {
        Write-Host "OK: saved $($result.roomsSaved) checkpoint(s)" -ForegroundColor Green
        $ckptDir = Resolve-CheckpointDir
        if (Test-Path $ckptDir) {
            $zips = @(Get-ChildItem $ckptDir -Filter '*.checkpoint.zip' -ErrorAction SilentlyContinue)
            Write-Host ("  on disk: {0} file(s) in {1}" -f $zips.Count, $ckptDir) -ForegroundColor Gray
        }
        return $opts
    }

    Write-Error "prepare-restart returned failure: $($result.message)"
    exit 1
}

# --- subcommand: restart-loop ---

function Invoke-RestartLoop {
    $opts = Invoke-PrepareRestart  # exits non-zero on its own failure paths

    Write-Section "POST $($opts.Url)/shutdown"
    try {
        Invoke-RestMethod -Uri "$($opts.Url)/shutdown" -Method Post -TimeoutSec 10 | Out-Null
        Write-Host '  shutdown requested' -ForegroundColor Green
    } catch {
        Write-Warning "shutdown POST raised: $_  (server may have died before responding -- ok)"
    }

    Write-Section "Waiting for $($opts.Url) to stop responding (up to 10s)"
    if (Wait-For-Server -Url $opts.Url -TimeoutSec 10) {
        Write-Host '  server is down' -ForegroundColor Green
    } else {
        Write-Warning '  server still responding after 10s -- continuing anyway'
    }

    Write-Host ''
    Write-Host 'Now relaunch the server in another terminal (e.g. `.\y.ps1 launch -ServerOnly`),' -ForegroundColor Cyan
    Write-Host "or use your existing `dotnet run` session." -ForegroundColor Cyan
    Write-Host ''

    Write-Section "Waiting up to $($opts.Timeout)s for server to come back"
    if (Wait-For-Server -Url $opts.Url -TimeoutSec $opts.Timeout -WantUp) {
        try {
            $v = Invoke-RestMethod -Uri "$($opts.Url)/api/version" -TimeoutSec 5
            Write-Host "  back up (server=$($v.server) client=$($v.client))" -ForegroundColor Green
        } catch {
            Write-Host '  back up' -ForegroundColor Green
        }
        Write-Host 'Restored room status will be in the server log and on connected clients.' -ForegroundColor Gray
    } else {
        Write-Error "Server did not come back within $($opts.Timeout)s"
        exit 1
    }
}

# --- subcommand: ckpt ---

function Show-CheckpointManifest {
    param([string]$ZipPath)
    Add-Type -AssemblyName 'System.IO.Compression.FileSystem'
    $zip = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        $entry = $zip.GetEntry('manifest.json')
        if (-not $entry) {
            Write-Warning "No manifest.json inside $ZipPath"
            return
        }
        $stream = $entry.Open()
        try {
            $reader = New-Object System.IO.StreamReader($stream)
            try {
                $json = $reader.ReadToEnd()
            } finally { $reader.Dispose() }
        } finally { $stream.Dispose() }

        Write-Host "[$([System.IO.Path]::GetFileName($ZipPath))]" -ForegroundColor Cyan
        ($json | ConvertFrom-Json) | ConvertTo-Json -Depth 10
    } finally {
        $zip.Dispose()
    }
}

function Invoke-Ckpt {
    if ($script:Rest.Count -lt 1) {
        Write-Error 'Usage: y.ps1 ckpt ls | show [roomId] | clean [-IncludeRestored]'
        exit 2
    }
    $op = $script:Rest[0]
    $ckptDir = Resolve-CheckpointDir

    switch ($op) {
        'ls' {
            Write-Section "Checkpoints in $ckptDir"
            if (-not (Test-Path $ckptDir)) {
                Write-Host '  (directory does not exist)' -ForegroundColor Gray
                return
            }
            $zips = @(Get-ChildItem $ckptDir -Filter '*.checkpoint.zip' -ErrorAction SilentlyContinue)
            if ($zips.Count -eq 0) {
                Write-Host '  (empty)' -ForegroundColor Gray
                return
            }
            $zips | Format-Table Name, Length, LastWriteTimeUtc -AutoSize
        }
        'show' {
            if (-not (Test-Path $ckptDir)) {
                Write-Warning "Checkpoint dir does not exist: $ckptDir"; exit 1
            }
            $roomId = if ($script:Rest.Count -ge 2) { $script:Rest[1] } else { $null }
            $zips = if ($roomId) {
                @(Get-ChildItem $ckptDir -Filter "$roomId.checkpoint.zip" -ErrorAction SilentlyContinue)
            } else {
                @(Get-ChildItem $ckptDir -Filter '*.checkpoint.zip' -ErrorAction SilentlyContinue)
            }
            if ($zips.Count -eq 0) {
                Write-Warning 'No matching checkpoints'; exit 1
            }
            foreach ($z in $zips) { Show-CheckpointManifest -ZipPath $z.FullName }
        }
        'clean' {
            $includeRestored = $script:Rest -contains '-IncludeRestored'
            if (Test-Path $ckptDir) {
                Write-Host "Removing $ckptDir" -ForegroundColor Yellow
                Remove-Item -Path $ckptDir -Recurse -Force
            } else {
                Write-Host "Nothing to remove at $ckptDir" -ForegroundColor Gray
            }
            if ($includeRestored) {
                $parent = Split-Path $ckptDir -Parent
                if (Test-Path $parent) {
                    $archives = @(Get-ChildItem $parent -Filter 'session-checkpoints-restored-*' -Directory -ErrorAction SilentlyContinue)
                    foreach ($a in $archives) {
                        Write-Host "Removing $($a.FullName)" -ForegroundColor Yellow
                        Remove-Item -Path $a.FullName -Recurse -Force
                    }
                }
            }
        }
        default {
            Write-Error "Unknown ckpt op: $op (use ls | show | clean)"
            exit 2
        }
    }
}

# --- dispatch ---

switch ($Command) {
    ''                { Invoke-Help }
    'help'            { Invoke-Help }
    'launch'          { Invoke-Launch }
    'build'           { Invoke-Build }
    'clean'           { Invoke-Clean }
    'format'          { Invoke-Format }
    'test'            { Invoke-Test }
    'deploy'          { Invoke-Deploy }
    'setup-crc'       { Invoke-SetupCrc }
    'logs'            { Invoke-Log }
    'status'          { Invoke-Status }
    'prepare-restart' { Invoke-PrepareRestart | Out-Null }
    'restart-loop'    { Invoke-RestartLoop }
    'ckpt'            { Invoke-Ckpt }
}
