@echo off
setlocal
REM ============================================================================
REM loopback-down.cmd - stops local Loopback dev (counterpart of loopback-up.cmd):
REM   1) closes the backend (dotnet watch) and frontend (next dev) windows, with their child processes
REM   2) stops the Claude bridge
REM
REM The LoopbackDocker stack loopback-up.cmd stopped is not restarted - run LoopbackDocker's
REM loopback-up.cmd for that (it starts the bridge again too).
REM The windows are found by the "title Loopback Server/Web" marker loopback-up.cmd puts in their
REM cmd command line; the bridge by its command line (dotnet ... claude-bridge.fsx), since its port
REM is held by Windows' HTTP.sys (PID 4).
REM ============================================================================

powershell -NoProfile -Command "$w = Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'cmd.exe' -and ($_.CommandLine -like '*title Loopback Server*' -or $_.CommandLine -like '*title Loopback Web*') }; if ($w) { $w | ForEach-Object { taskkill /T /F /PID $_.ProcessId *> $null; 'Closed ' + ($_.CommandLine -replace '.*title (Loopback \w+).*','$1') + ' (PID ' + $_.ProcessId + ').' } } else { 'Dev windows were not running.' }"

powershell -NoProfile -Command "$p = Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'dotnet.exe' -and $_.CommandLine -like '*claude-bridge.fsx*' }; if ($p) { $p | ForEach-Object { Stop-Process -Id $_.ProcessId -Force; 'Stopped Claude bridge (PID ' + $_.ProcessId + ').' } } else { 'Claude bridge was not running.' }"

endlocal
