@echo off
setlocal
cd /d "%~dp0backend\Aegis.Agent"

where dotnet >nul 2>nul
if errorlevel 1 (
  echo [AEGIS] .NET 8 SDK not found.
  echo Install it with:
  echo winget install Microsoft.DotNet.SDK.8
  pause
  exit /b 1
)

echo [AEGIS] Starting local monitoring agent on http://127.0.0.1:8765
start "" cmd /c "timeout /t 3 /nobreak >nul & start http://127.0.0.1:8765"
dotnet run
