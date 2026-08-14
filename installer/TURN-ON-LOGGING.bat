@echo off
REM ============================================================================
REM  Remote Scanner - turn on detailed logging.
REM
REM  Only needed when something is not working and detailed logs are wanted.
REM  Run COLLECT-LOGS.bat afterwards to gather them up.
REM
REM  Safe to leave on, but logs grow faster. Run TURN-OFF-LOGGING.bat when done.
REM ============================================================================

title Remote Scanner - Turn On Detailed Logging
color 0B

net session >nul 2>&1
if %errorlevel% neq 0 (
    echo.
    echo   Asking for administrator rights...
    echo   Click YES on the Windows prompt.
    echo.
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

echo.
echo   ============================================
echo     TURNING ON DETAILED LOGGING
echo   ============================================
echo.

reg add HKLM\SOFTWARE\RemoteScanner /v LogLevel /t REG_SZ /d Debug /f >nul
echo   Detailed logging is now ON.
echo.

REM Both the service and the running session agents re-read the level at startup,
REM so they are restarted to pick it up.
sc query RemoteScanner >nul 2>&1
if %errorlevel% equ 0 (
    echo   Restarting the Remote Scanner service...
    net stop RemoteScanner >nul 2>&1
    net start RemoteScanner >nul 2>&1
    echo   Done.
)

echo.
echo   ============================================
echo     NOW REPRODUCE THE PROBLEM
echo   ============================================
echo.
echo   1. Close your scanning program completely.
echo   2. Open it again.
echo   3. Try to pick the scanner.
echo   4. Then run COLLECT-LOGS.bat
echo.
pause
