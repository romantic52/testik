@echo off
chcp 65001 >nul
title AEGIS PostgreSQL - Backup PostgreSQL
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\backup-database.ps1" %*
pause
