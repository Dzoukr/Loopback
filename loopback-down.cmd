@echo off
setlocal
REM ============================================================================
REM loopback-down.cmd - stops Loopback (counterpart of loopback-up.cmd):
REM   1) docker compose down   (data/ and output/ stay on disk)
REM   2) removes the bridge's logon autostart
REM   3) stops the Claude bridge
REM
REM The bridge's port is held by Windows' HTTP.sys (PID 4), so it is stopped by its
REM command line (dotnet ... claude-bridge.fsx), not by "kill whatever listens on the port".
REM ============================================================================

set "RUN=HKCU\Software\Microsoft\Windows\CurrentVersion\Run"
set "RUNKEY=Loopback Claude Bridge"
pushd "%~dp0"

docker compose down

reg delete "%RUN%" /v "%RUNKEY%" /f >nul 2>&1 && echo Bridge autostart at logon disabled. || echo No bridge autostart entry was set.

powershell -NoProfile -Command "$p = Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'dotnet.exe' -and $_.CommandLine -like '*claude-bridge.fsx*' }; if ($p) { $p | ForEach-Object { Stop-Process -Id $_.ProcessId -Force; 'Stopped Claude bridge (PID ' + $_.ProcessId + ').' } } else { 'Claude bridge was not running.' }"

popd
endlocal
