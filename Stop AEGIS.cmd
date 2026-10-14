@echo off
setlocal
title AEGIS Stop
cd /d "%~dp0"
chcp 65001 >nul
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\stop-aegis.ps1"
pause
