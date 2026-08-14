@echo off
REM ============================================================================
REM  ScanBridge - remove from this PC.
REM
REM  Double-click. Removes the Remote Desktop add-in, the startup entry, the
REM  stored key, and the installed files.
REM ============================================================================

title ScanBridge - Uninstall
color 0E

echo.
echo   ============================================
echo     REMOTE SCANNER - UNINSTALL
echo   ============================================
echo.
echo   This removes ScanBridge from this PC.
echo   Your scanner and its own driver are NOT affected.
echo.

choice /C YN /M "   Remove ScanBridge"
if errorlevel 2 (
    echo.
    echo   Cancelled. Nothing was changed.
    echo.
    pause
    exit /b 0
)

echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Uninstall-Client.ps1"

echo.
echo   Close any open Remote Desktop windows to finish cleanup.
echo.
pause
