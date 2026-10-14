@echo off
chcp 65001 >nul
set "AEGIS_SRC=%LOCALAPPDATA%\AEGIS"
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process powershell -Verb RunAs -Wait -ArgumentList '-NoProfile -ExecutionPolicy Bypass -NoExit -File \"%~dp0scripts\migrate-service-data.ps1\" -SourceData \"%AEGIS_SRC%\"'"
