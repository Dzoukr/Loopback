#!/bin/sh
# ============================================================================
# dev-up.sh - runs Loopback locally in watch mode on macOS / Linux (dev-up.cmd on Windows):
#   1) stops the Docker stack (starter pack) if it runs (port 3000 + two servers sharing one Plaud session)
#   2) starts the host-side Claude bridge (claude-bridge.fsx) if it is not running
#   3) runs the backend:  dotnet watch    (http://localhost:5000, Swagger at /swagger)
#   4) runs the frontend: next dev        (UI: http://localhost:3000)
#
# Backend and frontend run in this terminal (output interleaved); Ctrl+C stops both, the bridge
# keeps running. dev-down.sh (from another terminal) stops it all.
# Uses data/dev/loopback.db (a copy of the Docker stack's database volume made on first run;
# delete data/dev to re-seed it). Result files
# go to LOOPBACK_OUTPUT from .env (default output/) - the backend reads it itself.
# Running Loopback from images (docker compose, up/down scripts) lives in starter-pack/,
# released as starter-pack.zip.
# Needs: .NET SDK, Node + corepack (yarn 4), claude CLI logged in.
# ============================================================================

ROOT=$(cd "$(dirname "$0")" && pwd)
# Container of the starter pack's server service (compose project "loopback")
DOCKER_SERVER=loopback-server-1
# Process groups of the backend and frontend, for dev-down.sh
PIDFILE="$ROOT/data/dev/dev.pids"
cd "$ROOT" || exit 1

if [ ! -f .env ]; then
    echo "ERROR: .env not found - copy .env.example to .env and fill in the secrets."
    exit 1
fi

git config core.hooksPath .githooks >/dev/null 2>&1

# --- The Docker stack would clash with the dev servers ---
if [ -n "$(docker ps -q --filter "label=com.docker.compose.project=loopback" 2>/dev/null)" ]; then
    echo "Stopping the Docker stack (the starter pack's loopback-up.sh starts it again) ..."
    docker compose -p loopback stop
fi

# --- Dev database: data/dev, seeded once from the Docker stack's volume (container must exist, may be stopped) ---
# Not data/loopback.db - that predates the Docker volume and is stale/corrupt.
export LOOPBACK_DATA="$ROOT/data/dev"
if [ ! -f "$LOOPBACK_DATA/loopback.db" ]; then
    echo "Seeding the dev database from the Docker stack's volume ..."
    mkdir -p "$LOOPBACK_DATA"
    docker cp "$DOCKER_SERVER:/data/." "$LOOPBACK_DATA" ||
        echo "  WARNING: could not copy it - the dev server starts with an empty database."
fi

# --- Bridge port = port of CLAUDE_BRIDGE_URL in .env (default 9100) ---
BRIDGE_PORT=$(grep -E '^[[:space:]]*CLAUDE_BRIDGE_URL[[:space:]]*=' .env | head -n 1 | tr -d '\r"' |
    sed -nE 's#^[^=]*=[[:space:]]*[A-Za-z]+://[^/:]+:([0-9]+).*#\1#p')
[ -n "$BRIDGE_PORT" ] || BRIDGE_PORT=9100

# Seconds since a process started (ps etime is [[dd-]hh:]mm:ss on both Linux and macOS)
age() {
    ps -o etime= -p "$1" | tr -d ' ' |
        awk -F'[-:]' '{ split("1 60 3600 86400", m, " "); s = 0; for (i = NF; i > 0; i--) s += $i * m[NF - i + 1]; print s }'
}

# --- A bridge started before claude-bridge.fsx last changed (an update) is stopped, so it restarts below ---
MTIME=$(stat -c %Y claude-bridge.fsx 2>/dev/null || stat -f %m claude-bridge.fsx)
NOW=$(date +%s)
RESTARTED=
for pid in $(pgrep -f 'fsi.*claude-bridge\.fsx'); do
    AGE=$(age "$pid")
    [ -n "$AGE" ] || continue
    if [ $((NOW - AGE)) -lt "$MTIME" ]; then
        kill "$pid" 2>/dev/null && RESTARTED=1
    fi
done
if [ -n "$RESTARTED" ]; then
    echo "claude-bridge.fsx was updated - restarting the Claude bridge."
    sleep 2
fi

# Job control: every background job gets its own process group, so Ctrl+C reaches only this
# script (which then stops backend + frontend) and the bridge survives it.
set -m

# --- Start the bridge unless /health already answers ---
if curl -s -m 3 -o /dev/null "http://127.0.0.1:$BRIDGE_PORT/health"; then
    echo "Claude bridge already running on :$BRIDGE_PORT."
else
    echo "Starting Claude bridge on :$BRIDGE_PORT ..."
    nohup dotnet fsi "$ROOT/claude-bridge.fsx" >/dev/null 2>&1 </dev/null &
fi

# --- Backend: outside Docker host.docker.internal is the LAN IP the bridge does not listen on ---
export CLAUDE_BRIDGE_URL="http://127.0.0.1:$BRIDGE_PORT"
export ASPNETCORE_ENVIRONMENT=Development
export ASPNETCORE_URLS=http://localhost:5000
(cd "$ROOT/src/Server" && exec dotnet watch run --non-interactive) </dev/null &
SERVER=$!

# --- Frontend: API_URL defaults to http://localhost:5000 (src/Web/src/env.ts) ---
(cd "$ROOT/src/Web" && exec yarn dev) </dev/null &
WEB=$!

echo "$SERVER $WEB" > "$PIDFILE"

stop() {
    trap - INT TERM HUP EXIT
    echo "Stopping backend and frontend ..."
    kill -s TERM -- "-$SERVER" "-$WEB" 2>/dev/null
    rm -f "$PIDFILE"
}
trap stop INT TERM HUP EXIT

echo "Loopback dev is starting: http://localhost:3000  (API http://localhost:5000/swagger) - Ctrl+C to stop"
wait
