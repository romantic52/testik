@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\install-service.ps1"
if errorlevel 1 pause
