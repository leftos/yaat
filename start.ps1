# Start yaat-server and yaat-client side by side.
# Kill all processes on Ctrl-C.
# Build sequentially first -- both projects share Yaat.Sim.
# Usage: .\start.ps1 [-Pull] [-Docker] [-ClientOnly] [-ServerOnly] [-VStrips] [-VStripsWeb] [-Release] [-Scenario <id>] [-Sync <url>]
#
# -Sync <url>     Sync local yaat repo to the commit pinned by a remote server,
#                 then build and run client-only. Example:
#                   .\start.ps1 -Sync https://yaat1.leftos.dev
# -VStrips        Also launch the standalone Yaat.VStrips desktop client alongside
#                 the main client, autoconnecting to the same server. Combine with
#                 -ClientOnly or -Sync to launch vStrips against an existing
#                 server. Ignored with -ServerOnly.
# -VStripsWeb     Publish the Yaat.VStrips.Web (WASM) bundle into
#                 yaat-server\wwwroot\vstrips\ so http://<server>/vstrips/ serves
#                 the live web client. Off by default to keep iteration fast --
#                 opt in when you've changed Yaat.VStrips.Web and need a fresh
#                 bundle. Ignored under -ClientOnly or -Docker.
# -Release        Build and run every project in Release configuration. Default
#                 is Debug for faster iteration. Yaat.VStrips.Web always
#                 publishes Release regardless (its Debug bundle is huge and
#                 the timing characteristics matter for diagnosing UI issues).

param(
    [switch]$Pull,
    [switch]$Docker,
    [switch]$ClientOnly,
    [switch]$ServerOnly,
    [switch]$VStrips,
    [switch]$VStripsWeb,
    [switch]$Release,
    [string]$Scenario,
    [string]$Sync
)

$Configuration = if ($Release) { "Release" } else { "Debug" }

$ClientDir = $PSScriptRoot
$ServerDir = Join-Path (Split-Path $ClientDir) "yaat-server"

# --sync: fetch version from remote server, checkout matching commit, run client-only
if ($Sync) {
    # Default to https when no scheme is provided -- Invoke-RestMethod won't auto-follow http->https redirects.
    if ($Sync -notmatch '^[a-zA-Z][a-zA-Z0-9+.-]*://') {
        $Sync = "https://$Sync"
    }
    $Sync = $Sync.TrimEnd('/')
    $versionUrl = "$Sync/api/version"
    Write-Host "Fetching version from $versionUrl..."
    try {
        $version = Invoke-RestMethod -Uri $versionUrl -TimeoutSec 10
    } catch {
        Write-Error "Failed to fetch version from $versionUrl`: $_"
        exit 1
    }

    $clientCommit = $version.client
    if (-not $clientCommit -or $clientCommit -eq "dev") {
        Write-Error "Remote server did not report a client commit hash (got: '$clientCommit'). The server may need to be redeployed with version support."
        exit 1
    }

    Write-Host "Remote server client commit: $clientCommit"
    Write-Host "Fetching and checking out $clientCommit..."

    git -C "$ClientDir" fetch origin
    if ($LASTEXITCODE -ne 0) { Write-Error "git fetch failed"; exit 1 }

    # Check for uncommitted changes
    $status = git -C "$ClientDir" status --porcelain
    if ($status) {
        Write-Error "Working tree has uncommitted changes. Commit or stash them before using -Sync."
        exit 1
    }

    git -C "$ClientDir" checkout $clientCommit
    if ($LASTEXITCODE -ne 0) { Write-Error "git checkout $clientCommit failed"; exit 1 }

    Write-Host "Checked out $clientCommit -- building client-only against $Sync"
    # Force client-only mode: the remote server IS the server
    $ClientOnly = $true
}

function Find-FreePort {
    param([int]$Start = 5000)
    for ($port = $Start; $port -lt $Start + 100; $port++) {
        $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $port)
        try {
            $listener.Start()
            $listener.Stop()
            return $port
        } catch { }
    }
    throw "No free port found in range $Start..$($Start + 99)"
}

$ServerPort = Find-FreePort 5000
if ($ServerPort -ne 5000) { Write-Host "Port 5000 in use, using port $ServerPort" }

if ($Pull) {
    if (-not $ServerOnly) {
        Write-Host "Pulling yaat-client..."
        git -C "$ClientDir" pull --ff-only
        if ($LASTEXITCODE -ne 0) { Write-Error "Client pull failed"; exit 1 }
    }
    if (-not $ClientOnly) {
        Write-Host "Pulling yaat-server..."
        git -C "$ServerDir" pull --ff-only
        if ($LASTEXITCODE -ne 0) { Write-Error "Server pull failed"; exit 1 }
    }
}

if (-not $ClientOnly) {
    if ($Docker) {
        Write-Host "Syncing yaat-server submodule to local yaat HEAD..."
        git -C "$ServerDir\extern\yaat" fetch "$ClientDir"
        if ($LASTEXITCODE -ne 0) { Write-Error "Submodule fetch failed"; exit 1 }
        git -C "$ServerDir\extern\yaat" checkout FETCH_HEAD
        if ($LASTEXITCODE -ne 0) { Write-Error "Submodule checkout failed"; exit 1 }

        Write-Host "Building yaat-server Docker image..."
        docker build -f "$ServerDir\src\Yaat.Server\Dockerfile" -t yaat-server:local "$ServerDir"
        if ($LASTEXITCODE -ne 0) { Write-Error "Docker build failed"; exit 1 }
    } else {
        Write-Host "Building yaat-server ($Configuration)..."
        dotnet build "$ServerDir\src\Yaat.Server" -c $Configuration -v q
        if ($LASTEXITCODE -ne 0) { Write-Error "Server build failed"; exit 1 }
    }
}

if (-not $ServerOnly) {
    Write-Host "Building yaat-client ($Configuration)..."
    dotnet build "$ClientDir\src\Yaat.Client" -c $Configuration -v q
    if ($LASTEXITCODE -ne 0) { Write-Error "Client build failed"; exit 1 }
}

if ($VStrips -and -not $ServerOnly) {
    Write-Host "Building yaat-vstrips ($Configuration)..."
    dotnet build "$ClientDir\tools\Yaat.VStrips" -c $Configuration -v q
    if ($LASTEXITCODE -ne 0) { Write-Error "vStrips build failed"; exit 1 }
}

# Publish the WASM web vStrips client into yaat-server\wwwroot\vstrips\ so
# /vstrips/ serves the live bundle when the server runs. The project's
# CopyToServerWwwroot AfterTargets="Publish" target does the cross-repo copy.
# Opt-in via -VStripsWeb. Ignored under -ClientOnly (no server to serve it
# from) or -Docker (the dockerized server has its own bundle baked in via
# the image).
if ($VStripsWeb -and -not $ClientOnly -and -not $Docker) {
    # Yaat.VStrips.Web is a Microsoft.NET.Sdk.WebAssembly project with
    # WasmBuildNative=true, which needs the wasm-tools workload. There's no
    # global.json manifest pinning it, so `dotnet workload restore` is a no-op
    # -- probe explicitly and bail with an actionable message rather than
    # letting publish fail with NETSDK1147.
    $workloads = & dotnet workload list 2>&1
    if ($LASTEXITCODE -ne 0 -or -not ($workloads -match '(?m)^\s*wasm-tools\s')) {
        Write-Error @"
Missing required .NET workload: wasm-tools.

It's needed to publish the WebAssembly vStrips bundle into the server's
wwwroot (tools/Yaat.VStrips.Web -> yaat-server/src/Yaat.Server/wwwroot/vstrips/).
Install it with:

    dotnet workload install wasm-tools

On Windows this needs an elevated PowerShell. Then re-run start.ps1.

To skip the WASM publish entirely, re-run without -VStripsWeb (the default).
"@
        exit 1
    }

    # Clean before publish so content-hashed WASM assets don't pile up across
    # iterations (Avalonia.Base.{hash}.wasm and friends never get deleted by
    # incremental publish; the wwwroot grows from 35 MB to 150 MB+ in a few
    # rebuilds). Clean is fast on incremental and forces a fresh asset set.
    Write-Host "Cleaning yaat-vstrips-web..."
    dotnet clean "$ClientDir\tools\Yaat.VStrips.Web" -c Release -v q
    if ($LASTEXITCODE -ne 0) { Write-Error "Yaat.VStrips.Web clean failed"; exit 1 }
    Write-Host "Publishing yaat-vstrips-web..."
    dotnet publish "$ClientDir\tools\Yaat.VStrips.Web" -c Release -v q
    if ($LASTEXITCODE -ne 0) { Write-Error "Yaat.VStrips.Web publish failed"; exit 1 }
}

$procs = @()

if (-not $ClientOnly) {
    Write-Host "Starting yaat-server..."
    if ($Docker) {
        $procs += Start-Process -PassThru -NoNewWindow docker "run --rm --name yaat-server-local -p ${ServerPort}:${ServerPort} -e ASPNETCORE_URLS=http://0.0.0.0:${ServerPort} yaat-server:local"
    } else {
        $procs += Start-Process -PassThru -NoNewWindow dotnet "run --no-build -c $Configuration --project `"$ServerDir\src\Yaat.Server`" -- --urls http://0.0.0.0:${ServerPort}"
    }
}

$autoconnectUrl = $null
if ($Sync) {
    $autoconnectUrl = $Sync
} elseif (-not $ClientOnly) {
    $autoconnectUrl = "http://localhost:${ServerPort}"
}

if (-not $ServerOnly) {
    Write-Host "Starting yaat-client..."
    $clientArgs = "run --no-build -c $Configuration --project `"$ClientDir\src\Yaat.Client`""
    $needsDashDash = $true
    if ($autoconnectUrl) {
        $clientArgs += " -- --autoconnect $autoconnectUrl"
        $needsDashDash = $false
    }
    if ($Scenario) {
        if ($needsDashDash) { $clientArgs += " --" }
        $clientArgs += " --scenario $Scenario"
    }
    $procs += Start-Process -PassThru -NoNewWindow dotnet $clientArgs
}

if ($VStrips -and -not $ServerOnly) {
    Write-Host "Starting yaat-vstrips..."
    $vstripsArgs = "run --no-build -c $Configuration --project `"$ClientDir\tools\Yaat.VStrips`""
    if ($autoconnectUrl) {
        $vstripsArgs += " -- --autoconnect $autoconnectUrl"
    }
    $procs += Start-Process -PassThru -NoNewWindow dotnet $vstripsArgs
}

Write-Host "PIDs: $($procs.Id -join ', ')"
Write-Host "Press Ctrl-C to stop."

try {
    foreach ($proc in $procs) { $proc.WaitForExit() }
} finally {
    if (-not $ClientOnly -and $Docker) {
        docker stop yaat-server-local 2>$null
    }
    foreach ($proc in $procs) {
        if ($proc.HasExited) { continue }

        # GUI process (Yaat.Client) — ask the window to close so OnClosing fires and any
        # in-memory window geometry / pop-out state is flushed to preferences. Fall back
        # to force-kill if it doesn't exit promptly. Pure-console processes (the server)
        # have no main window — they go straight to force-kill.
        $hasWindow = $false
        try { $hasWindow = $proc.MainWindowHandle -ne [IntPtr]::Zero } catch { }

        if ($hasWindow) {
            [void]$proc.CloseMainWindow()
            if (-not $proc.WaitForExit(3000)) {
                Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
            }
        } else {
            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        }
    }
    Write-Host "Shutting down..."
}
