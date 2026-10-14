@echo off
setlocal
title AEGIS SOC Launcher
cd /d "%~dp0"
chcp 65001 >nul
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\start-aegis.ps1"
if errorlevel 1 (
  echo.
  echo AEGIS не запустился. Причина показана выше; подробности: Diagnose AEGIS.cmd
  pause
  exit /b 1
)
exit /b 0
