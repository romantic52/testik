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

echo [AEGIS] Restoring dependencies...
dotnet restore "%~dp0backend\Aegis.Agent.Tests\Aegis.Agent.Tests.csproj"
if errorlevel 1 goto :fail

echo [AEGIS] Running tests...
dotnet test "%~dp0backend\Aegis.Agent.Tests\Aegis.Agent.Tests.csproj" -c Release --no-restore
if errorlevel 1 goto :fail

if exist "%~dp0dist\AEGIS" rmdir /s /q "%~dp0dist\AEGIS"

echo [AEGIS] Publishing Windows x64 self-contained bundle...
dotnet publish "%~dp0backend\Aegis.Agent\Aegis.Agent.csproj" ^
  -c Release ^
  -r win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=false ^
  -o "%~dp0dist\AEGIS"
if errorlevel 1 goto :fail

echo [AEGIS] Packaging service scripts...
if not exist "%~dp0dist\AEGIS\scripts" mkdir "%~dp0dist\AEGIS\scripts"
copy /y "%~dp0scripts\install-service.ps1" "%~dp0dist\AEGIS\scripts\install-service.ps1" >nul
copy /y "%~dp0scripts\uninstall-service.ps1" "%~dp0dist\AEGIS\scripts\uninstall-service.ps1" >nul
copy /y "%~dp0install-service.bat" "%~dp0dist\AEGIS\install-service.bat" >nul
copy /y "%~dp0uninstall-service.bat" "%~dp0dist\AEGIS\uninstall-service.bat" >nul

echo [AEGIS] Running packaged smoke test...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\smoke-agent.ps1" -Executable "%~dp0dist\AEGIS\Aegis.Agent.exe"
if errorlevel 1 goto :fail

echo.
echo [AEGIS] Release verified successfully.
echo [AEGIS] Output: dist\AEGIS\Aegis.Agent.exe
echo [AEGIS] Start it with run-agent.bat.
pause
exit /b 0

:fail
echo.
echo [AEGIS] Release verification failed. dist was not accepted as a valid build.
pause
exit /b 1
