#!/bin/sh
# ============================================================================
# loopback-up.sh - starts (or updates) Loopback on macOS / Linux (loopback-up.cmd on Windows):
#   1) starts the host-side Claude bridge (claude-bridge.fsx) if it is not running
#   2) registers the bridge for logon autostart (macOS: LaunchAgent, Linux: systemd user unit)
#   3) pulls the latest images and starts them  (UI: http://localhost:3000)
#
# A container cannot run the host's `claude`, so the bridge lives outside Compose.
# Needs: Docker Desktop running, .NET SDK (dotnet fsi), claude CLI logged in.
# ============================================================================

ROOT=$(cd "$(dirname "$0")" && pwd)
BRIDGE="$ROOT/claude-bridge.fsx"
LABEL=local.loopback.claude-bridge
cd "$ROOT" || exit 1

if [ ! -f .env ]; then
    echo "ERROR: .env not found - copy .env.example to .env and fill in the secrets."
    exit 1
fi

# --- Bridge port = port of CLAUDE_BRIDGE_URL in .env (default 9100) ---
PORT=$(grep -E '^[[:space:]]*CLAUDE_BRIDGE_URL[[:space:]]*=' .env | head -n 1 | tr -d '\r"' |
    sed -nE 's#^[^=]*=[[:space:]]*[A-Za-z]+://[^/:]+:([0-9]+).*#\1#p')
[ -n "$PORT" ] || PORT=9100

health() { curl -s -m 3 -o /dev/null "http://127.0.0.1:$PORT/health"; }

# Seconds since a process started (ps etime is [[dd-]hh:]mm:ss on both Linux and macOS)
age() {
    ps -o etime= -p "$1" | tr -d ' ' |
        awk -F'[-:]' '{ split("1 60 3600 86400", m, " "); s = 0; for (i = NF; i > 0; i--) s += $i * m[NF - i + 1]; print s }'
}

# --- A bridge started before claude-bridge.fsx last changed (an update) is stopped, so it restarts below ---
MTIME=$(stat -c %Y "$BRIDGE" 2>/dev/null || stat -f %m "$BRIDGE")
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

# --- Start the bridge unless /health already answers ---
if health; then
    echo "Claude bridge already running on :$PORT."
else
    echo "Starting Claude bridge on :$PORT ..."
    nohup dotnet fsi "$BRIDGE" >/dev/null 2>&1 &
    UP=
    for _ in $(seq 1 30); do
        sleep 2
        if health; then UP=1; break; fi
    done
    if [ -n "$UP" ]; then
        echo "  OK - bridge is up."
    else
        echo "  WARNING: bridge did not come up - see claude-bridge.log, or run \"dotnet fsi claude-bridge.fsx\" to see the error."
    fi
fi

# --- Logon autostart (re-running is idempotent: the file is rewritten). It gets this shell's PATH,
#     since logon services do not read your shell profile and would not find dotnet / claude ---
DOTNET=$(command -v dotnet)
case "$(uname -s)" in
    Darwin)
        PLIST="$HOME/Library/LaunchAgents/$LABEL.plist"
        mkdir -p "$(dirname "$PLIST")"
        cat > "$PLIST" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key><string>$LABEL</string>
    <key>ProgramArguments</key>
    <array><string>$DOTNET</string><string>fsi</string><string>$BRIDGE</string></array>
    <key>WorkingDirectory</key><string>$ROOT</string>
    <key>EnvironmentVariables</key><dict><key>PATH</key><string>$PATH</string></dict>
    <key>RunAtLoad</key><true/>
</dict>
</plist>
EOF
        echo "Bridge autostart at logon enabled."
        ;;
    *)
        if command -v systemctl >/dev/null 2>&1; then
            UNIT="$HOME/.config/systemd/user/loopback-claude-bridge.service"
            mkdir -p "$(dirname "$UNIT")"
            cat > "$UNIT" <<EOF
[Unit]
Description=Loopback Claude Bridge

[Service]
WorkingDirectory=$ROOT
ExecStart="$DOTNET" fsi "$BRIDGE"
Environment="PATH=$PATH"

[Install]
WantedBy=default.target
EOF
            if systemctl --user daemon-reload && systemctl --user enable loopback-claude-bridge.service >/dev/null 2>&1; then
                echo "Bridge autostart at logon enabled."
            else
                echo "WARNING: could not enable the systemd user unit for autostart."
            fi
        else
            echo "WARNING: no systemd - the bridge is not started at logon; re-run loopback-up.sh after a reboot."
        fi
        ;;
esac

# --- The bridge listens on 127.0.0.1 only; containers reach it as host.docker.internal on Docker Desktop ---
case "$(docker info --format '{{.OperatingSystem}}' 2>/dev/null)" in
    *"Docker Desktop"*) ;;
    *) echo "WARNING: this does not look like Docker Desktop - the containers may not reach the Claude bridge." ;;
esac

# --- Docker stack (always pulls, so re-running updates Loopback) ---
if docker compose up -d --pull always; then
    echo "Loopback is running: http://localhost:3000"
else
    RC=$?
    echo "ERROR: docker compose failed - is Docker Desktop running?"
    exit $RC
fi
