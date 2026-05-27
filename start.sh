#!/usr/bin/env bash
# Start yaat-server and yaat-client side by side.
# Kill all processes on Ctrl-C.
# Build sequentially first — both projects share Yaat.Sim.
# Usage: ./start.sh [--pull] [--docker] [--client-only] [--server-only] [--vstrips-web] [--scenario <id>] [--sync <url>]
#
# --sync <url>      Sync local yaat repo to the commit pinned by a remote server,
#                   then build and run client-only. Example:
#                     ./start.sh --sync https://yaat1.leftos.dev
# --vstrips-web     Publish the Yaat.VStrips.Web (WASM) bundle into
#                   yaat-server/wwwroot/vstrips/ so http://<server>/vstrips/
#                   serves the live web client. Off by default to keep
#                   iteration fast — opt in when you've changed
#                   Yaat.VStrips.Web and need a fresh bundle. Ignored under
#                   --client-only or --docker.
# --release         Build and run every project in Release configuration. Default
#                   is Debug for faster iteration. Yaat.VStrips.Web always
#                   publishes Release regardless (its Debug bundle is huge and
#                   the timing characteristics matter for diagnosing UI issues).

set -euo pipefail

PULL=false
DOCKER=false
CLIENT_ONLY=false
SERVER_ONLY=false
VSTRIPS_WEB=false
RELEASE=false
SCENARIO=""
SYNC=""
while [[ $# -gt 0 ]]; do
    case "$1" in
        --pull) PULL=true; shift ;;
        --docker) DOCKER=true; shift ;;
        --client-only) CLIENT_ONLY=true; shift ;;
        --server-only) SERVER_ONLY=true; shift ;;
        --vstrips-web) VSTRIPS_WEB=true; shift ;;
        --release) RELEASE=true; shift ;;
        --scenario) SCENARIO="$2"; shift 2 ;;
        --sync) SYNC="$2"; shift 2 ;;
        *) shift ;;
    esac
done

if $RELEASE; then
    CONFIGURATION="Release"
else
    CONFIGURATION="Debug"
fi

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
        echo "Building yaat-server ($CONFIGURATION)..."
        dotnet build "$SERVER_DIR/src/Yaat.Server" -c "$CONFIGURATION" -v q
    fi
fi

if ! $SERVER_ONLY; then
    echo "Building yaat-client ($CONFIGURATION)..."
    dotnet build "$CLIENT_DIR/src/Yaat.Client" -c "$CONFIGURATION" -v q
fi

# Publish the WASM web vStrips client into yaat-server/wwwroot/vstrips/ so
# /vstrips/ serves the live bundle when the server runs. The project's
# CopyToServerWwwroot AfterTargets="Publish" target does the cross-repo copy.
# Opt-in via --vstrips-web. Ignored under --client-only (no server to serve
# it from) or --docker (the dockerized server has its own bundle baked in
# via the image).
if $VSTRIPS_WEB && ! $CLIENT_ONLY && ! $DOCKER; then
    # Yaat.VStrips.Web is a Microsoft.NET.Sdk.WebAssembly project with
    # WasmBuildNative=true, which needs the wasm-tools workload. There's no
    # global.json manifest pinning it, so `dotnet workload restore` is a no-op
    # — probe explicitly and bail with an actionable message rather than
    # letting publish fail with NETSDK1147.
    if ! dotnet workload list 2>/dev/null | grep -qE '^[[:space:]]*wasm-tools[[:space:]]'; then
        cat >&2 <<'EOF'
Error: missing required .NET workload: wasm-tools.

It's needed to publish the WebAssembly vStrips bundle into the server's
wwwroot (tools/Yaat.VStrips.Web -> yaat-server/src/Yaat.Server/wwwroot/vstrips/).
Install it with:

    dotnet workload install wasm-tools

(may require sudo on Linux/macOS depending on how dotnet was installed).
Then re-run start.sh.

To skip the WASM publish entirely, re-run without --vstrips-web (the default).
EOF
        exit 1
    fi

    # Clean before publish so content-hashed WASM assets don't pile up across
    # iterations (Avalonia.Base.{hash}.wasm and friends never get deleted by
    # incremental publish; the wwwroot grows from 35 MB to 150 MB+ in a few
    # rebuilds). Clean is fast on incremental and forces a fresh asset set.
    echo "Cleaning yaat-vstrips-web..."
    dotnet clean "$CLIENT_DIR/tools/Yaat.VStrips.Web" -c Release -v q
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
        dotnet run --no-build -c "$CONFIGURATION" --project "$SERVER_DIR/src/Yaat.Server" -- --urls "http://0.0.0.0:$SERVER_PORT" &
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
        dotnet run --no-build -c "$CONFIGURATION" --project "$CLIENT_DIR/src/Yaat.Client" -- "${CLIENT_ARGS[@]}" &
    else
        dotnet run --no-build -c "$CONFIGURATION" --project "$CLIENT_DIR/src/Yaat.Client" &
    fi
    PIDS+=($!)
fi

echo "PIDs: ${PIDS[*]}"
echo "Press Ctrl-C to stop."

wait "${PIDS[@]}"
