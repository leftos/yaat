#!/usr/bin/env bash
# Start yaat-server and yaat-client side by side.
# Kill all processes on Ctrl-C.
# Build sequentially first — both projects share Yaat.Sim.
# Usage: ./start.sh [--pull] [--docker] [--client-only] [--server-only] [--vstrips] [--no-vstrips-web] [--scenario <id>] [--sync <url>]
#
# --sync <url>      Sync local yaat repo to the commit pinned by a remote server,
#                   then build and run client-only. Example:
#                     ./start.sh --sync https://yaat1.leftos.dev
# --vstrips         Also launch the standalone Yaat.VStrips desktop client alongside
#                   the main client, autoconnecting to the same server. Combine with
#                   --client-only or --sync to launch vStrips against an existing
#                   server. Ignored with --server-only.
# --no-vstrips-web  Skip the Yaat.VStrips.Web (WASM) publish step. By default the
#                   web bundle is published into yaat-server/wwwroot/vstrips/ on
#                   every server-bearing run so http://<server>/vstrips/ serves
#                   the live web client. Skip when iterating server changes only
#                   and the existing bundle is fine.

set -euo pipefail

PULL=false
DOCKER=false
CLIENT_ONLY=false
SERVER_ONLY=false
VSTRIPS=false
NO_VSTRIPS_WEB=false
SCENARIO=""
SYNC=""
while [[ $# -gt 0 ]]; do
    case "$1" in
        --pull) PULL=true; shift ;;
        --docker) DOCKER=true; shift ;;
        --client-only) CLIENT_ONLY=true; shift ;;
        --server-only) SERVER_ONLY=true; shift ;;
        --vstrips) VSTRIPS=true; shift ;;
        --no-vstrips-web) NO_VSTRIPS_WEB=true; shift ;;
        --scenario) SCENARIO="$2"; shift 2 ;;
        --sync) SYNC="$2"; shift 2 ;;
        *) shift ;;
    esac
done

CLIENT_DIR="$(cd "$(dirname "$0")" && pwd)"
SERVER_DIR="$(dirname "$CLIENT_DIR")/yaat-server"

# --sync: fetch version from remote server, checkout matching commit, run client-only
if [[ -n "$SYNC" ]]; then
    # Default to https when no scheme is provided
    if [[ ! "$SYNC" =~ ^[a-zA-Z][a-zA-Z0-9+.-]*:// ]]; then
        SYNC="https://$SYNC"
    fi
    SYNC="${SYNC%/}"
    VERSION_URL="$SYNC/api/version"
    echo "Fetching version from $VERSION_URL..."
    VERSION_JSON=$(curl -sf --max-time 10 "$VERSION_URL") || {
        echo "Error: Failed to fetch version from $VERSION_URL" >&2
        exit 1
    }

    # Parse client commit from JSON (portable: no jq dependency)
    CLIENT_COMMIT=$(echo "$VERSION_JSON" | grep -o '"client":"[^"]*"' | cut -d'"' -f4)
    if [[ -z "$CLIENT_COMMIT" || "$CLIENT_COMMIT" == "dev" ]]; then
        echo "Error: Remote server did not report a client commit hash (got: '$CLIENT_COMMIT')." >&2
        echo "The server may need to be redeployed with version support." >&2
        exit 1
    fi

    echo "Remote server client commit: $CLIENT_COMMIT"
    echo "Fetching and checking out $CLIENT_COMMIT..."

    git -C "$CLIENT_DIR" fetch origin

    # Check for uncommitted changes
    if [[ -n "$(git -C "$CLIENT_DIR" status --porcelain)" ]]; then
        echo "Error: Working tree has uncommitted changes. Commit or stash them before using --sync." >&2
        exit 1
    fi

    git -C "$CLIENT_DIR" checkout "$CLIENT_COMMIT"
    echo "Checked out $CLIENT_COMMIT — building client-only against $SYNC"
    CLIENT_ONLY=true
fi

find_free_port() {
    local port=${1:-5000}
    while lsof -iTCP:"$port" -sTCP:LISTEN -t >/dev/null 2>&1; do
        port=$((port + 1))
    done
    echo "$port"
}

SERVER_PORT=$(find_free_port 5000)
if [ "$SERVER_PORT" -ne 5000 ]; then
    echo "Port 5000 in use, using port $SERVER_PORT"
fi

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

if $VSTRIPS && ! $SERVER_ONLY; then
    echo "Building yaat-vstrips..."
    dotnet build "$CLIENT_DIR/tools/Yaat.VStrips" -v q
fi

# Publish the WASM web vStrips client into yaat-server/wwwroot/vstrips/ so
# /vstrips/ serves the live bundle when the server runs. The project's
# CopyToServerWwwroot AfterTargets="Publish" target does the cross-repo copy.
# Skipped under --client-only (no server to serve it from), --no-vstrips-web
# (opt-out), or --docker (the dockerized server has its own bundle baked in
# via the image).
if ! $CLIENT_ONLY && ! $NO_VSTRIPS_WEB && ! $DOCKER; then
    echo "Publishing yaat-vstrips-web..."
    dotnet publish "$CLIENT_DIR/tools/Yaat.VStrips.Web" -c Release -v q
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
        docker run --rm --name yaat-server-local -p "$SERVER_PORT:$SERVER_PORT" -e ASPNETCORE_URLS="http://0.0.0.0:$SERVER_PORT" yaat-server:local &
        PIDS+=($!)
    else
        dotnet run --no-build --project "$SERVER_DIR/src/Yaat.Server" -- --urls "http://0.0.0.0:$SERVER_PORT" &
        PIDS+=($!)
    fi
fi

AUTOCONNECT_URL=""
if [[ -n "$SYNC" ]]; then
    AUTOCONNECT_URL="$SYNC"
elif ! $CLIENT_ONLY; then
    AUTOCONNECT_URL="http://localhost:$SERVER_PORT"
fi

if ! $SERVER_ONLY; then
    echo "Starting yaat-client..."
    CLIENT_ARGS=()
    if [[ -n "$AUTOCONNECT_URL" ]]; then
        CLIENT_ARGS+=(--autoconnect "$AUTOCONNECT_URL")
    fi
    if [[ -n "$SCENARIO" ]]; then
        CLIENT_ARGS+=(--scenario "$SCENARIO")
    fi
    if [[ ${#CLIENT_ARGS[@]} -gt 0 ]]; then
        dotnet run --no-build --project "$CLIENT_DIR/src/Yaat.Client" -- "${CLIENT_ARGS[@]}" &
    else
        dotnet run --no-build --project "$CLIENT_DIR/src/Yaat.Client" &
    fi
    PIDS+=($!)
fi

if $VSTRIPS && ! $SERVER_ONLY; then
    echo "Starting yaat-vstrips..."
    if [[ -n "$AUTOCONNECT_URL" ]]; then
        dotnet run --no-build --project "$CLIENT_DIR/tools/Yaat.VStrips" -- --autoconnect "$AUTOCONNECT_URL" &
    else
        dotnet run --no-build --project "$CLIENT_DIR/tools/Yaat.VStrips" &
    fi
    PIDS+=($!)
fi

echo "PIDs: ${PIDS[*]}"
echo "Press Ctrl-C to stop."

wait "${PIDS[@]}"
