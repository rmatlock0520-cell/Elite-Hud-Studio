@echo off
title Elite HUD Studio
cd /d "%~dp0"
powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File "%~dp0app\server.ps1" %*
if errorlevel 1 pause
