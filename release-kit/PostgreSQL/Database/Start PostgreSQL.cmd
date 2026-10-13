@echo off
chcp 65001 >nul
color 0F
title AEGIS Database Demo - PostgreSQL
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\start-database.ps1"
if errorlevel 1 pause
