@echo off
chcp 65001 >nul
title AEGIS PostgreSQL - Repair PostgreSQL
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\repair-database.ps1" %*
pause
