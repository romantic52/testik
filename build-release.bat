@echo off
setlocal
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
  echo [AEGIS] .NET 8 SDK is required to build the release.
  echo winget install Microsoft.DotNet.SDK.8
  pause
  exit /b 1
)

if exist "%~dp0dist\AEGIS" rmdir /s /q "%~dp0dist\AEGIS"

echo [AEGIS] Publishing Windows x64 bundle...
dotnet publish "%~dp0backend\Aegis.Agent\Aegis.Agent.csproj" ^
  -c Release ^
  -r win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=false ^
  -o "%~dp0dist\AEGIS"

if errorlevel 1 (
  echo [AEGIS] Build failed.
  pause
  exit /b 1
)

echo.
echo [AEGIS] Done: dist\AEGIS\Aegis.Agent.exe
echo You can now run run-agent.bat.
pause
