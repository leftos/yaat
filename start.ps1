# Start yaat-server and yaat-client side by side.
# Kill both on Ctrl-C.
# Build sequentially first — both projects share Yaat.Sim.
# Usage: .\start.ps1 [-Pull] [-Docker] [-ClientOnly] [-ServerOnly]

param(
    [switch]$Pull,
    [switch]$Docker,
    [switch]$ClientOnly,
    [switch]$ServerOnly
)

$ClientDir = $PSScriptRoot
$ServerDir = Join-Path (Split-Path $ClientDir) "yaat-server"

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
        $procs += Start-Process -PassThru -NoNewWindow docker "run --rm --name yaat-server-local -p 5000:5000 yaat-server:local"
    } else {
        $procs += Start-Process -PassThru -NoNewWindow dotnet "run --no-build --project `"$ServerDir\src\Yaat.Server`""
    }
}

if (-not $ServerOnly) {
    Write-Host "Starting yaat-client..."
    if ($ClientOnly) {
        $procs += Start-Process -PassThru -NoNewWindow dotnet "run --no-build --project `"$ClientDir\src\Yaat.Client`""
    } else {
        $procs += Start-Process -PassThru -NoNewWindow dotnet "run --no-build --project `"$ClientDir\src\Yaat.Client`" -- --autoconnect http://localhost:5000"
    }
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
