#!/usr/bin/env bash
# Start yaat-server and yaat-client side by side.
# Kill both on Ctrl-C.
# Build sequentially first — both projects share Yaat.Sim.
# Usage: ./start.sh [--pull]

set -euo pipefail

PULL=false
if [[ "${1:-}" == "--pull" ]]; then
    PULL=true
fi

CLIENT_DIR="$(cd "$(dirname "$0")" && pwd)"
SERVER_DIR="$(dirname "$CLIENT_DIR")/yaat-server"

if $PULL; then
    echo "Pulling yaat-client..."
    git -C "$CLIENT_DIR" pull --ff-only

    echo "Pulling yaat-server..."
    git -C "$SERVER_DIR" pull --ff-only
fi

echo "Building yaat-server..."
dotnet build "$SERVER_DIR/src/Yaat.Server" -v q

echo "Building yaat-client..."
dotnet build "$CLIENT_DIR/src/Yaat.Client" -v q

cleanup() {
    echo "Shutting down..."
    kill "$SERVER_PID" "$CLIENT_PID" 2>/dev/null || true
    wait "$SERVER_PID" "$CLIENT_PID" 2>/dev/null || true
}
trap cleanup EXIT INT TERM

echo "Starting yaat-server..."
dotnet run --no-build --project "$SERVER_DIR/src/Yaat.Server" &
SERVER_PID=$!

echo "Starting yaat-client..."
dotnet run --no-build --project "$CLIENT_DIR/src/Yaat.Client" -- --autoconnect &
CLIENT_PID=$!

echo "Server PID: $SERVER_PID  Client PID: $CLIENT_PID"
echo "Press Ctrl-C to stop both."

wait "$SERVER_PID" "$CLIENT_PID"
