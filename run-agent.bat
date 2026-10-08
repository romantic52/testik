@echo off
setlocal
cd /d "%~dp0"
call "%~dp0Start AEGIS.cmd"
exit /b %errorlevel%
