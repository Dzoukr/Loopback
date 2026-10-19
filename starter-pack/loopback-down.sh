#!/bin/sh
# ============================================================================
# loopback-down.sh - stops Loopback on macOS / Linux (counterpart of loopback-up.sh):
#   1) docker compose down   (the database volume and result files are kept)
#   2) removes the bridge's logon autostart
#   3) stops the Claude bridge
#
# The bridge is stopped by its command line (dotnet fsi ... claude-bridge.fsx).
# ============================================================================

LABEL=local.loopback.claude-bridge
cd "$(dirname "$0")" || exit 1

docker compose down

case "$(uname -s)" in
    Darwin)
        PLIST="$HOME/Library/LaunchAgents/$LABEL.plist"
        if [ -f "$PLIST" ]; then
            launchctl bootout "gui/$(id -u)/$LABEL" >/dev/null 2>&1
            rm -f "$PLIST" && echo "Bridge autostart at logon disabled."
        else
            echo "No bridge autostart entry was set."
        fi
        ;;
    *)
        UNIT="$HOME/.config/systemd/user/loopback-claude-bridge.service"
        if [ -f "$UNIT" ]; then
            systemctl --user disable loopback-claude-bridge.service >/dev/null 2>&1
            rm -f "$UNIT" && systemctl --user daemon-reload && echo "Bridge autostart at logon disabled."
        else
            echo "No bridge autostart entry was set."
        fi
        ;;
esac

PIDS=$(pgrep -f 'fsi.*claude-bridge\.fsx')
if [ -n "$PIDS" ]; then
    for pid in $PIDS; do
        kill "$pid" && echo "Stopped Claude bridge (PID $pid)."
    done
else
    echo "Claude bridge was not running."
fi
