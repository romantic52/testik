@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Launch Database Sandbox.ps1"
if errorlevel 1 pause
