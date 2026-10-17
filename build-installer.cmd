@echo off
rem Genera el instalador sin depender de la directiva de ejecucion de PowerShell.
rem Uso: doble clic, o "build-installer.cmd -Platforms x64,arm64"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-installer.ps1" %*
if errorlevel 1 (echo. & echo ERROR: no se pudo generar el instalador.)
echo.
pause
