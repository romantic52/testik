@echo off
setlocal
cd /d "%~dp0"

if exist "%~dp0dist\AEGIS\Aegis.Agent.exe" (
  echo [AEGIS] Starting published agent...
  start "" cmd /c "timeout /t 2 /nobreak >nul & start http://127.0.0.1:8765"
  "%~dp0dist\AEGIS\Aegis.Agent.exe"
  exit /b %errorlevel%
)

where dotnet >nul 2>nul
if errorlevel 1 (
  echo [AEGIS] .NET 8 SDK not found and no published build exists.
  echo.
  echo Option 1: run build-release.bat on a machine with .NET 8 SDK.
  echo Option 2: install the SDK:
  echo winget install Microsoft.DotNet.SDK.8
  pause
  exit /b 1
)

echo [AEGIS] Starting source agent on http://127.0.0.1:8765
start "" cmd /c "timeout /t 3 /nobreak >nul & start http://127.0.0.1:8765"
cd /d "%~dp0backend\Aegis.Agent"
dotnet run
