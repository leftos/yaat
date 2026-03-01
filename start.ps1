# Start yaat-server and yaat-client side by side.
# Kill both on Ctrl-C.

$ServerDir = "X:\dev\yaat-server"
$ClientDir = "X:\dev\yaat"

Write-Host "Starting yaat-server..."
$server = Start-Process -PassThru -NoNewWindow dotnet "run --project $ServerDir\src\Yaat.Server"

Write-Host "Starting yaat-client..."
$client = Start-Process -PassThru -NoNewWindow dotnet "run --project $ClientDir\src\Yaat.Client -- --autoconnect"

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
