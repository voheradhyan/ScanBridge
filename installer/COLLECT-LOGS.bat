@echo off
REM ============================================================================
REM  Remote Scanner - collect all logs into one zip file on the Desktop.
REM
REM  Run this on whichever machine has the problem. Send the zip file it makes.
REM  Logs never contain scanned documents - only sizes, page counts and errors.
REM ============================================================================

title Remote Scanner - Collect Logs
color 0B

echo.
echo   ============================================
echo     COLLECTING LOGS
echo   ============================================
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Collect-Logs.ps1"

echo.
echo   Send that zip file from your Desktop.
echo.
pause
