#!/usr/bin/env bash
# Start yaat-server and yaat-client side by side.
# Kill both on Ctrl-C.
# Build sequentially first — both projects share Yaat.Sim.
# Usage: ./start.sh [--pull] [--docker] [--client-only] [--server-only]

set -euo pipefail

PULL=false
DOCKER=false
CLIENT_ONLY=false
SERVER_ONLY=false
for arg in "$@"; do
    case "$arg" in
        --pull) PULL=true ;;
        --docker) DOCKER=true ;;
        --client-only) CLIENT_ONLY=true ;;
        --server-only) SERVER_ONLY=true ;;
    esac
done

CLIENT_DIR="$(cd "$(dirname "$0")" && pwd)"
SERVER_DIR="$(dirname "$CLIENT_DIR")/yaat-server"

if $PULL; then
    if ! $SERVER_ONLY; then
        echo "Pulling yaat-client..."
        git -C "$CLIENT_DIR" pull --ff-only
    fi
    if ! $CLIENT_ONLY; then
        echo "Pulling yaat-server..."
        git -C "$SERVER_DIR" pull --ff-only
    fi
fi

if ! $CLIENT_ONLY; then
    if $DOCKER; then
        echo "Syncing yaat-server submodule to local yaat HEAD..."
        git -C "$SERVER_DIR/extern/yaat" fetch "$CLIENT_DIR"
        git -C "$SERVER_DIR/extern/yaat" checkout FETCH_HEAD

        echo "Building yaat-server Docker image..."
        docker build -f "$SERVER_DIR/src/Yaat.Server/Dockerfile" -t yaat-server:local "$SERVER_DIR"
    else
        echo "Building yaat-server..."
        dotnet build "$SERVER_DIR/src/Yaat.Server" -v q
    fi
fi

if ! $SERVER_ONLY; then
    echo "Building yaat-client..."
    dotnet build "$CLIENT_DIR/src/Yaat.Client" -v q
fi

PIDS=()

cleanup() {
    echo "Shutting down..."
    if ! $CLIENT_ONLY && $DOCKER; then
        docker stop yaat-server-local 2>/dev/null || true
    fi
    for pid in "${PIDS[@]}"; do
        kill "$pid" 2>/dev/null || true
        wait "$pid" 2>/dev/null || true
    done
}
trap cleanup EXIT INT TERM

if ! $CLIENT_ONLY; then
    echo "Starting yaat-server..."
    if $DOCKER; then
        docker run --rm --name yaat-server-local -p 5000:5000 yaat-server:local &
        PIDS+=($!)
    else
        dotnet run --no-build --project "$SERVER_DIR/src/Yaat.Server" &
        PIDS+=($!)
    fi
fi

if ! $SERVER_ONLY; then
    echo "Starting yaat-client..."
    if $CLIENT_ONLY; then
        dotnet run --no-build --project "$CLIENT_DIR/src/Yaat.Client" &
    else
        dotnet run --no-build --project "$CLIENT_DIR/src/Yaat.Client" -- --autoconnect http://localhost:5000 &
    fi
    PIDS+=($!)
fi

echo "PIDs: ${PIDS[*]}"
echo "Press Ctrl-C to stop."

wait "${PIDS[@]}"
