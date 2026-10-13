@echo off
chcp 65001 >nul
title AEGIS PostgreSQL - Restore PostgreSQL
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\restore-database.ps1" %*
pause
