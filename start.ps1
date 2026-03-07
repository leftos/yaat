# Start yaat-server and yaat-client side by side.
# Kill both on Ctrl-C.
# Build sequentially first — both projects share Yaat.Sim.
# Usage: .\start.ps1 [-Pull]

param(
    [switch]$Pull
)

$ClientDir = $PSScriptRoot
$ServerDir = Join-Path (Split-Path $ClientDir) "yaat-server"

if ($Pull) {
    Write-Host "Pulling yaat-client..."
    git -C "$ClientDir" pull --ff-only
    if ($LASTEXITCODE -ne 0) { Write-Error "Client pull failed"; exit 1 }

    Write-Host "Pulling yaat-server..."
    git -C "$ServerDir" pull --ff-only
    if ($LASTEXITCODE -ne 0) { Write-Error "Server pull failed"; exit 1 }
}

Write-Host "Building yaat-server..."
dotnet build "$ServerDir\src\Yaat.Server" -v q
if ($LASTEXITCODE -ne 0) { Write-Error "Server build failed"; exit 1 }

Write-Host "Building yaat-client..."
dotnet build "$ClientDir\src\Yaat.Client" -v q
if ($LASTEXITCODE -ne 0) { Write-Error "Client build failed"; exit 1 }

Write-Host "Starting yaat-server..."
$server = Start-Process -PassThru -NoNewWindow dotnet "run --no-build --project `"$ServerDir\src\Yaat.Server`""

Write-Host "Starting yaat-client..."
$client = Start-Process -PassThru -NoNewWindow dotnet "run --no-build --project `"$ClientDir\src\Yaat.Client`" -- --autoconnect"

Write-Host "Server PID: $($server.Id)  Client PID: $($client.Id)"
Write-Host "Press Ctrl-C to stop both."

try {
    $server.WaitForExit()
    $client.WaitForExit()
} finally {
    foreach ($proc in @($server, $client)) {
        if (!$proc.HasExited) {
            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        }
    }
    Write-Host "Shutting down..."
}
