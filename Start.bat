@echo off
REM Starts the Looper API and the Angular dev server together.
setlocal
cd /d "%~dp0"

echo Starting Looper API...
start "Looper API" cmd /c "cd api && dotnet run --project Looper.Api"

echo Starting Angular dev server...
start "Looper Web" cmd /c "cd web && npm start"

echo.
echo Both servers are running in separate windows.
echo Press any key to stop them...
pause >nul

taskkill /FI "WINDOWTITLE eq Looper API*" /T /F >nul 2>&1
taskkill /FI "WINDOWTITLE eq Looper Web*" /T /F >nul 2>&1
echo Stopped.
