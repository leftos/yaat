# Start yaat-server and yaat-client side by side.
# Kill both on Ctrl-C.
# Build sequentially first -- both projects share Yaat.Sim.
# Usage: .\start.ps1 [-Pull] [-Docker] [-ClientOnly] [-ServerOnly] [-Scenario <id>] [-Sync <url>]
#
# -Sync <url>  Sync local yaat repo to the commit pinned by a remote server,
#              then build and run client-only. Example:
#                .\start.ps1 -Sync https://yaat1.leftos.dev

param(
    [switch]$Pull,
    [switch]$Docker,
    [switch]$ClientOnly,
    [switch]$ServerOnly,
    [string]$Scenario,
    [string]$Sync
)

$ClientDir = $PSScriptRoot
$ServerDir = Join-Path (Split-Path $ClientDir) "yaat-server"

# --sync: fetch version from remote server, checkout matching commit, run client-only
if ($Sync) {
    $versionUrl = "$($Sync.TrimEnd('/'))/api/version"
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
        Write-Host "Building yaat-server..."
        dotnet build "$ServerDir\src\Yaat.Server" -v q
        if ($LASTEXITCODE -ne 0) { Write-Error "Server build failed"; exit 1 }
    }
}

if (-not $ServerOnly) {
    Write-Host "Building yaat-client..."
    dotnet build "$ClientDir\src\Yaat.Client" -v q
    if ($LASTEXITCODE -ne 0) { Write-Error "Client build failed"; exit 1 }
}

$procs = @()

if (-not $ClientOnly) {
    Write-Host "Starting yaat-server..."
    if ($Docker) {
        $procs += Start-Process -PassThru -NoNewWindow docker "run --rm --name yaat-server-local -p ${ServerPort}:${ServerPort} -e ASPNETCORE_URLS=http://0.0.0.0:${ServerPort} yaat-server:local"
    } else {
        $procs += Start-Process -PassThru -NoNewWindow dotnet "run --no-build --project `"$ServerDir\src\Yaat.Server`" -- --urls http://0.0.0.0:${ServerPort}"
    }
}

if (-not $ServerOnly) {
    Write-Host "Starting yaat-client..."
    $clientArgs = "run --no-build --project `"$ClientDir\src\Yaat.Client`""
    $needsDashDash = $true
    if ($Sync) {
        $clientArgs += " -- --autoconnect $Sync"
        $needsDashDash = $false
    } elseif (-not $ClientOnly) {
        $clientArgs += " -- --autoconnect http://localhost:${ServerPort}"
        $needsDashDash = $false
    }
    if ($Scenario) {
        if ($needsDashDash) { $clientArgs += " --" }
        $clientArgs += " --scenario $Scenario"
    }
    $procs += Start-Process -PassThru -NoNewWindow dotnet $clientArgs
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
        if (!$proc.HasExited) {
            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        }
    }
    Write-Host "Shutting down..."
}
