@echo off
rem Runs the cold-start measurement. Double-click this after installing OpenVSA.
rem
rem -ExecutionPolicy Bypass is scoped to this one process: a script downloaded from elsewhere is
rem blocked by default, and changing the machine's policy to run one measurement would be a
rem larger change to somebody's computer than the measurement is worth.
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Measure-ColdStart.ps1" %*
echo.
echo Press any key to close.
pause > nul
