#!/bin/sh
# ============================================================================
# dev-down.sh - stops local Loopback dev on macOS / Linux (counterpart of dev-up.sh):
#   1) stops the backend (dotnet watch) and frontend (next dev), with their child processes
#   2) stops the Claude bridge
#
# The Docker stack dev-up.sh stopped is not restarted - run the starter pack's
# loopback-up.sh for that (it starts the bridge again too).
# Backend and frontend are found by their process groups, which dev-up.sh writes to
# data/dev/dev.pids; the bridge by its command line (dotnet fsi ... claude-bridge.fsx).
# ============================================================================

PIDFILE="$(cd "$(dirname "$0")" && pwd)/data/dev/dev.pids"

if [ -f "$PIDFILE" ]; then
    for pgid in $(cat "$PIDFILE"); do
        kill -s TERM -- "-$pgid" 2>/dev/null && echo "Stopped dev process group $pgid."
    done
    rm -f "$PIDFILE"
else
    echo "Dev servers were not running."
fi

PIDS=$(pgrep -f 'fsi.*claude-bridge\.fsx')
if [ -n "$PIDS" ]; then
    for pid in $PIDS; do
        kill "$pid" && echo "Stopped Claude bridge (PID $pid)."
    done
else
    echo "Claude bridge was not running."
fi
