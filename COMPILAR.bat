@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo Compilando o Lume RGB...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0source\build.ps1"
echo.
pause
