@echo off
rem ============================================================================
rem  Elite HUD Studio launcher with a pass/fail check.
rem  Checks the app's files, starts it, waits until it reports READY, then makes
rem  sure it stays running for 15 seconds before calling it a PASS.
rem  Writes PASS or FAIL to Logs\launcher-log.txt AND to a backup copy in
rem  %LOCALAPPDATA%\Elite HUD Studio\Logs (antivirus folder protection can't
rem  block that one). Closes by itself once Studio is running.
rem ============================================================================
setlocal EnableDelayedExpansion
title Elite HUD Studio
cd /d "%~dp0"
set "EXE=Elite HUD Studio.exe"
set "LOGDIR=%CD%\Logs"
set "BAKDIR=%LOCALAPPDATA%\Elite HUD Studio\Logs"
if not exist "%LOGDIR%" mkdir "%LOGDIR%" 2>nul
if not exist "%BAKDIR%" mkdir "%BAKDIR%" 2>nul
set "LOG=%LOGDIR%\launcher-log.txt"
set "BAKLOG=%BAKDIR%\launcher-log.txt"
set "STATUS=%LOGDIR%\launch-status.txt"
set "BAKSTATUS=%BAKDIR%\launch-status.txt"

rem --- can we write in the app folder? (antivirus folder protection may block it)
set "FOLDER_OK=yes"
(>>"%LOG%" echo.) 2>nul || set "FOLDER_OK=no"

call :log "===== %date% %time%  launch ====="
for /f "tokens=*" %%v in ('ver') do call :log "Windows: %%v"
call :log "Folder: %CD%"
if "%FOLDER_OK%"=="no" call :log "Note: can't write in the app's Logs folder - antivirus folder protection is likely blocking it. Using the backup log only."

rem --- are all the app's files here? (antivirus sometimes removes some)
set "MISSING="
for %%f in ("%EXE%" "HUD Studio.html" "app\catalog.json" "app\defaults.json" "app\webview2\WebView2Loader.dll" "app\webview2\Microsoft.Web.WebView2.Core.dll" "app\webview2\Microsoft.Web.WebView2.WinForms.dll") do (
  if not exist %%f set "MISSING=!MISSING! %%~f;"
)
if defined MISSING if not exist "%EXE%" if not exist "app\webview2\WebView2Loader.dll" if exist "app\launcher\StudioApp.cs" (
  set "WHY=This folder is the source code download, so the app itself is not in it. Download Elite-HUD-Studio from the Releases page instead - https://github.com/rmatlock0520-cell/Elite-Hud-Studio/releases/latest"
  if exist "Start HUD Studio - Browser version.bat" set "WHY=!WHY! - or use the browser version right now by double-clicking Start HUD Studio - Browser version.bat in this folder."
  goto fail
)
if defined MISSING (
  set "WHY=Files are missing:!MISSING! Your antivirus may have moved them to quarantine - restore them and add this folder as an exception - or unzip the whole package again."
  goto fail
)
call :log "Files: all present"

rem --- start the app (no administrator permission needed)
del /q "%STATUS%" 2>nul
del /q "%BAKSTATUS%" 2>nul
echo.
echo   Starting Elite HUD Studio...
start "" "%EXE%"
if errorlevel 1 (
  set "WHY=Windows could not start %EXE% - it may be blocked by antivirus or Windows."
  goto fail
)

rem --- wait for READY / FAILED / CRASHED (up to 2 minutes), watching the app's process
set /a WAITED=0
set "SEEN=0"
set "GONE=0"
:wait
timeout /t 1 /nobreak >nul
set /a WAITED+=1
call :readstatus
call :running
if "!RUN!"=="1" (set "SEEN=1" & set "GONE=0")
if /i "!ST!"=="READY" goto ready
if /i "!ST!"=="FAILED"  ( set "WHY=Studio could not start: !DETAIL!" & goto fail )
if /i "!ST!"=="CRASHED" ( set "WHY=Studio crashed while starting: !DETAIL!" & goto fail )
if "!SEEN!"=="1" if "!RUN!"=="0" (
  set /a GONE+=1
  if !GONE! GEQ 3 ( set "WHY=Studio started, then stopped after about !WAITED! seconds before it was ready, without saying why. This is usually antivirus blocking or sandboxing it - add the Studio folder as an exception in your antivirus." & goto fail )
)
if !WAITED! GEQ 120 (
  if "!SEEN!"=="0" ( set "WHY=Studio never started. Antivirus or Windows blocked the app - add the Studio folder as an exception in your antivirus." ) else ( set "WHY=Studio did not finish starting within 2 minutes - last status: !ST! !DETAIL!" )
  goto fail
)
goto wait

rem --- READY: make sure it stays running for 15 seconds
:ready
echo !DETAIL! | find /i "already open" >nul && ( call :log "PASS: Studio was already open." & exit /b 0 )
echo   Studio is open. Checking it stays running...
set /a STABLE=0
:stable
timeout /t 1 /nobreak >nul
set /a STABLE+=1
call :readstatus
if /i "!ST!"=="CRASHED" ( set "WHY=Studio opened, then crashed: !DETAIL!" & goto fail )
call :running
if "!RUN!"=="0" (
  set "WHY=Studio opened, then was closed after about !STABLE! seconds. If you didn't close it yourself, this is usually antivirus - add the Studio folder as an exception in your antivirus."
  goto fail
)
if !STABLE! LSS 15 goto stable
call :log "PASS: Studio is running - ready after !WAITED! s and still running 15 s later. !DETAIL!"
exit /b 0

:fail
call :log "FAIL: !WHY!"
color 4F
echo.
echo   ======================================================
echo    ELITE HUD STUDIO DID NOT START PROPERLY
echo   ======================================================
echo.
echo   !WHY!
echo.
echo   This was saved in:
if "%FOLDER_OK%"=="yes" echo     %LOG%
echo     %BAKLOG%
echo   Any crash or problem reports are in the same folders.
echo   Send those Logs folders when asking for help.
echo.
if "%FOLDER_OK%"=="yes" start "" explorer "%LOGDIR%"
start "" explorer "%BAKDIR%"
pause
exit /b 1

rem --- helpers --------------------------------------------------------------
:log
if "%FOLDER_OK%"=="yes" (>>"%LOG%" echo %~1) 2>nul
(>>"%BAKLOG%" echo %~1) 2>nul
exit /b 0

:readstatus
set "ST=" & set "DETAIL="
if exist "%STATUS%" for /f "usebackq tokens=1,*" %%a in ("%STATUS%") do (set "ST=%%a" & set "DETAIL=%%b")
if not defined ST if exist "%BAKSTATUS%" for /f "usebackq tokens=1,*" %%a in ("%BAKSTATUS%") do (set "ST=%%a" & set "DETAIL=%%b")
exit /b 0

:running
set "RUN=0"
tasklist /FI "IMAGENAME eq %EXE%" /NH 2>nul | find /i "%EXE%" >nul && set "RUN=1"
exit /b 0
