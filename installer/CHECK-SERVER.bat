@echo off
REM ============================================================================
REM  Remote Scanner - check the server is set up correctly.
REM
REM  Double-click after installing. It changes nothing - it only looks.
REM  Run it again any time the scanner stops appearing for users.
REM ============================================================================

title Remote Scanner - Check Server
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

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Check-Server.ps1"

echo.
echo   To send this to your IT contact, right-click the title bar of
echo   this window, choose Edit then Select All, press Enter to copy,
echo   and paste it into an email.
echo.
pause
