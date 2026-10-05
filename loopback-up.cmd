@echo off
setlocal enabledelayedexpansion
REM ============================================================================
REM loopback-up.cmd - runs Loopback locally in watch mode:
REM   1) stops the LoopbackDocker stack if it runs (port 3000 + two servers sharing one Plaud session)
REM   2) starts the host-side Claude bridge (claude-bridge.fsx) if it is not running
REM   3) opens a window with the backend:  dotnet watch    (http://localhost:5000, Swagger at /swagger)
REM   4) opens a window with the frontend: next dev        (UI: http://localhost:3000)
REM
REM Close a window (or Ctrl+C in it) to stop that part; loopback-down.cmd stops it all.
REM Uses data\dev\loopback.db (a copy of the LoopbackDocker database volume made on first run;
REM delete data\dev to re-seed it). Result files
REM go to LOOPBACK_OUTPUT from .env (default output\) - the backend reads it itself.
REM Running Loopback from images (docker compose, up/down scripts) lives in the LoopbackDocker repo.
REM Needs: .NET SDK, Node + corepack (yarn 4), claude CLI logged in.
REM ============================================================================

set "ROOT=%~dp0"
set "SYS=%SystemRoot%\System32"
REM Container of LoopbackDocker's server service (compose project "loopback")
set "DOCKER_SERVER=loopback-server-1"
pushd "%ROOT%"

if not exist .env (
    echo ERROR: .env not found - copy .env.example to .env and fill in the secrets.
    popd & exit /b 1
)

git config core.hooksPath .githooks >nul 2>&1

REM --- LoopbackDocker stack would clash with the dev servers ---
for /f "delims=" %%c in ('docker ps -q --filter "label=com.docker.compose.project=loopback" 2^>nul') do set "DOCKER_UP=1"
if defined DOCKER_UP (
    echo Stopping the LoopbackDocker stack ^(its loopback-up.cmd starts it again^) ...
    docker compose -p loopback stop
)

REM --- Dev database: data\dev, seeded once from the LoopbackDocker volume (container must exist, may be stopped) ---
REM Not data\loopback.db - that predates the Docker volume and is stale/corrupt.
set "LOOPBACK_DATA=%ROOT%data\dev"
if not exist "%LOOPBACK_DATA%\loopback.db" (
    echo Seeding the dev database from the LoopbackDocker volume ...
    if not exist "%LOOPBACK_DATA%" mkdir "%LOOPBACK_DATA%"
    docker cp %DOCKER_SERVER%:/data/. "%LOOPBACK_DATA%" || echo   WARNING: could not copy it - the dev server starts with an empty database.
)

REM --- Bridge port = port of CLAUDE_BRIDGE_URL in .env (default 9100) ---
set "BRIDGE_PORT="
for /f "usebackq delims=" %%p in (`powershell -NoProfile -Command "try { $u = (Get-Content .env | Where-Object { $_ -match '^\s*CLAUDE_BRIDGE_URL\s*=' } | Select-Object -First 1) -replace '^[^=]*=\s*',''; ([uri]$u.Trim().Trim([char]34)).Port } catch { }"`) do set "BRIDGE_PORT=%%p"
if not defined BRIDGE_PORT set "BRIDGE_PORT=9100"

REM --- Start the bridge unless /health already answers ---
"%SYS%\curl.exe" -s -m 3 -o nul http://127.0.0.1:%BRIDGE_PORT%/health
if !errorlevel!==0 (
    echo Claude bridge already running on :%BRIDGE_PORT%.
) else (
    echo Starting Claude bridge on :%BRIDGE_PORT% ...
    powershell -NoProfile -Command "Start-Process conhost.exe -WorkingDirectory '%ROOT%' -ArgumentList '--headless','dotnet','fsi','\"%ROOT%claude-bridge.fsx\"'"
)

REM --- Backend: outside Docker host.docker.internal is the LAN IP the bridge does not listen on ---
set "CLAUDE_BRIDGE_URL=http://127.0.0.1:%BRIDGE_PORT%"
set "ASPNETCORE_ENVIRONMENT=Development"
set "ASPNETCORE_URLS=http://localhost:5000"
REM "title Loopback ..." marks the window's cmd command line - loopback-down.cmd finds it by that
start "Loopback Server (dotnet watch)" /d "%ROOT%src\Server" cmd /k title Loopback Server ^& dotnet watch run --non-interactive

REM --- Frontend: API_URL defaults to http://localhost:5000 (src/Web/src/env.ts) ---
start "Loopback Web (next dev)" /d "%ROOT%src\Web" cmd /k title Loopback Web ^& yarn dev

echo Loopback dev is starting: http://localhost:3000  (API http://localhost:5000/swagger)
popd
endlocal
