@echo off
REM ============================================================================
REM  Remote Scanner - turn on detailed logging ON THIS PC.
REM
REM  Run this on the PC that has the scanner, when scanning through Remote
REM  Desktop does not work and someone needs to see why.
REM
REM  No administrator rights needed: this writes a per-user setting, which is
REM  the point - the part that needs watching (the plugin inside Remote Desktop
REM  Connection) runs as you, not as the machine.
REM
REM  Detailed logging is slightly slower and makes bigger log files. Turn it
REM  off again with the same file once the problem is understood.
REM ============================================================================

title Remote Scanner - Detailed Logging
color 0B

echo.
echo   ============================================
echo     DETAILED LOGGING - THIS PC
echo   ============================================
echo.

reg query HKCU\SOFTWARE\RemoteScanner /v LogLevel 2>nul | find /i "Debug" >nul
if not errorlevel 1 goto :turnoff

reg add HKCU\SOFTWARE\RemoteScanner /v LogLevel /t REG_SZ /d Debug /f >nul
if errorlevel 1 (
    echo   Could not change the setting.
    echo.
    pause
    exit /b 1
)

echo   Detailed logging is now ON.
echo.
echo   Now do this, in order:
echo     1. Close Remote Desktop Connection completely.
echo     2. Right-click the Remote Scanner icon near the clock, choose Exit.
echo     3. Start Remote Scanner again from the Start Menu.
echo     4. Connect with Remote Desktop and try to scan.
echo     5. Run COLLECT-LOGS.bat and send the zip.
echo.
echo   Steps 1-3 matter: the setting is read once when each program starts.
echo.
pause
exit /b 0

:turnoff
reg add HKCU\SOFTWARE\RemoteScanner /v LogLevel /t REG_SZ /d Information /f >nul
echo   Detailed logging is now OFF (back to normal).
echo.
echo   Close and reopen Remote Desktop Connection and Remote Scanner
echo   for this to take effect.
echo.
pause
exit /b 0
