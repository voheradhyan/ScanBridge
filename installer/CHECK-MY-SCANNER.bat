@echo off
REM ============================================================================
REM  ScanBridge - check that this PC can see its scanner.
REM
REM  Run this FIRST whenever the scanner does not show up in the remote session.
REM  It tests only this PC and its scanner - Remote Desktop is not involved at
REM  all - so it tells you which half of the problem you have:
REM
REM      scanners listed here  -> the scanner is fine, the problem is the
REM                               Remote Desktop connection
REM      nothing listed here   -> the problem is the scanner or its driver
REM ============================================================================

title ScanBridge - Check My Scanner
color 0B

echo.
echo   ============================================
echo     CHECKING THIS PC FOR SCANNERS
echo   ============================================
echo.
echo   Please wait, this takes about 10 seconds...
echo.

"%~dp0ScanBridge.Client.exe" --enumerate-once

echo.
echo   ============================================
echo.
echo   If you see your scanner listed above, this PC is fine.
echo.
echo   If you see "0 scanner(s)" or nothing at all:
echo     1. Is the scanner switched on and plugged in?
echo     2. Does it work in its own software, or in Windows Fax and Scan?
echo     3. Try unplugging and replugging the USB cable.
echo.
echo   Detailed logs are in:
echo     %%LocalAppData%%\ScanBridge\logs
echo.
pause
