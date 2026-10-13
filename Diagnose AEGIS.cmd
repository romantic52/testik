@echo off
setlocal
title AEGIS Diagnostics
cd /d "%~dp0"
chcp 65001 >nul
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\diagnose-aegis.ps1"
pause
