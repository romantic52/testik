@echo off
chcp 65001 >nul
color 0F
title AEGIS Database Demo - Stop
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\stop-database.ps1"
pause
