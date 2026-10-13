@echo off
chcp 65001 >nul
title AETHER relay (local)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\start-relay.ps1"
if errorlevel 1 pause
