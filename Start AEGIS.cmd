@echo off
setlocal
title AEGIS SOC Launcher
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\start-aegis.ps1"
if errorlevel 1 (
  echo.
  echo Could not start AEGIS. Read the message above.
  pause
  exit /b 1
)
exit /b 0
