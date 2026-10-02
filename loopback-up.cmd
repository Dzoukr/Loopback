@echo off
setlocal enabledelayedexpansion
REM ============================================================================
REM loopback-up.cmd - starts Loopback:
REM   1) enables the repo's pre-commit hook (blocks committing secrets)
REM   2) starts the host-side Claude bridge (claude-bridge.fsx) if it is not running
REM   3) registers the bridge for logon autostart (HKCU Run - no admin needed)
REM   4) docker compose up -d --build  (UI: http://localhost:3000)
REM
REM A container cannot run the host's `claude`, so the bridge lives outside Compose.
REM Needs: Docker Desktop running, .NET SDK (dotnet fsi), claude CLI logged in.
REM ============================================================================

set "ROOT=%~dp0"
set "SYS=%SystemRoot%\System32"
set "RUN=HKCU\Software\Microsoft\Windows\CurrentVersion\Run"
set "RUNKEY=Loopback Claude Bridge"
set "BRIDGE=conhost.exe --headless dotnet fsi \"%ROOT%claude-bridge.fsx\""
pushd "%ROOT%"

if not exist .env (
    echo ERROR: .env not found - copy .env.example to .env and fill in the secrets.
    popd & exit /b 1
)

git config core.hooksPath .githooks >nul 2>&1

REM --- Bridge port = port of CLAUDE_BRIDGE_URL in .env (default 9100) ---
set "PORT="
for /f "usebackq delims=" %%p in (`powershell -NoProfile -Command "try { $u = (Get-Content .env | Where-Object { $_ -match '^\s*CLAUDE_BRIDGE_URL\s*=' } | Select-Object -First 1) -replace '^[^=]*=\s*',''; ([uri]$u.Trim().Trim([char]34)).Port } catch { }"`) do set "PORT=%%p"
if not defined PORT set "PORT=9100"

REM --- Start the bridge unless /health already answers ---
"%SYS%\curl.exe" -s -m 3 -o nul http://127.0.0.1:%PORT%/health
if !errorlevel!==0 (
    echo Claude bridge already running on :%PORT%.
) else (
    echo Starting Claude bridge on :%PORT% ...
    REM Start-Process launches it detached (no inherited console/pipe handles), unlike `start`.
    powershell -NoProfile -Command "Start-Process conhost.exe -WorkingDirectory '%ROOT%' -ArgumentList '--headless','dotnet','fsi','\"%ROOT%claude-bridge.fsx\"'"
    set "UP="
    for /l %%i in (1,1,30) do if not defined UP (
        "%SYS%\ping.exe" -n 3 127.0.0.1 >nul
        "%SYS%\curl.exe" -s -m 3 -o nul http://127.0.0.1:%PORT%/health && set "UP=1"
    )
    if defined UP (echo   OK - bridge is up.) else (echo   WARNING: bridge did not come up - see claude-bridge.log, or run "dotnet fsi claude-bridge.fsx" to see the error.)
)

REM --- Logon autostart (re-running is idempotent: /f overwrites) ---
reg add "%RUN%" /v "%RUNKEY%" /t REG_SZ /d "%BRIDGE%" /f >nul && echo Bridge autostart at logon enabled. || echo WARNING: could not set the autostart registry value.

REM --- Docker stack ---
docker compose up -d --build
set "RC=%errorlevel%"
if "%RC%"=="0" echo Loopback is running: http://localhost:3000
popd
endlocal & exit /b %RC%
