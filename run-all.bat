@echo off
REM ============================================================================
REM  RateLimit - one-command demo runner.
REM
REM  Stops leftovers, builds, starts LocalDB + all four services against the
REM  LOCAL MOCK ONLY, then opens the interactive demo menu.
REM
REM    run-all.bat                 interactive menu
REM    run-all.bat 5               run demo 5 with defaults, then exit
REM    run-all.bat outbound        run demos 1-8 back to back, then exit
REM    run-all.bat inbound         run demos 9-11 back to back, then exit
REM    run-all.bat all             run everything, then exit
REM    run-all.bat attach          menu only, do not build or restart services
REM
REM  Keep this file ASCII-only: non-ASCII bytes (an em dash was enough) break cmd's
REM  line parsing and produce spurious "'M' is not recognized" errors.
REM ============================================================================
setlocal
cd /d "%~dp0"

set "SCRIPT=%~dp0scripts\Demo.ps1"

if /I "%~1"=="attach" goto :attach
if "%~1"=="" goto :menu
goto :single

:menu
powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%"
goto :end

:attach
powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" -SkipStart
goto :end

:single
powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" -Run "%~1"
goto :end

:end
endlocal
