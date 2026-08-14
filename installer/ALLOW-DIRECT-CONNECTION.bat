@echo off
REM ============================================================================
REM  Remote Scanner - allow a Remote Desktop server to reach this PC directly.
REM
REM  RUN THIS AS ADMINISTRATOR (right-click, "Run as administrator").
REM
REM  This is the one part of Remote Scanner that needs administrator rights,
REM  and it is only needed when scanning through Remote Desktop does not work
REM  on its own. Normally the scanner is carried by the Remote Desktop
REM  connection itself and no firewall rule is required at all.
REM
REM  What it allows: incoming connections on TCP port 47214, from computers on
REM  your local network only, to the Remote Scanner program only. Anything that
REM  connects must still prove it holds this PC's pairing code before a single
REM  byte of a document is sent, and everything after that is encrypted.
REM ============================================================================

setlocal
title Remote Scanner - Allow Direct Connection
color 0B

net session >nul 2>&1
if errorlevel 1 (
    echo.
    echo   ============================================
    echo     ADMINISTRATOR RIGHTS NEEDED
    echo   ============================================
    echo.
    echo   Close this window, then RIGHT-CLICK this file and choose
    echo   "Run as administrator".
    echo.
    pause
    exit /b 1
)

set "RULE=Remote Scanner (direct connection)"
set "PORT=47214"
set "TARGET=%LOCALAPPDATA%\Programs\RemoteScanner\RemoteScanner.Client.exe"

REM %LOCALAPPDATA% belongs to the administrator when elevated, not to the person who installed
REM Remote Scanner, so the per-user install path is resolved from the original profile instead.
if not exist "%TARGET%" (
    for /f "tokens=2 delims==" %%U in ('wmic computersystem get username /value 2^>nul ^| find "="') do (
        for /f "tokens=2 delims=\" %%N in ("%%U") do set "OWNER=%%N"
    )
    if defined OWNER set "TARGET=C:\Users\%OWNER%\AppData\Local\Programs\RemoteScanner\RemoteScanner.Client.exe"
)

echo.
echo   ============================================
echo     REMOTE SCANNER - ALLOW DIRECT CONNECTION
echo   ============================================
echo.

if not exist "%TARGET%" (
    echo   Could not find Remote Scanner on this PC:
    echo     %TARGET%
    echo.
    echo   Install it first by double-clicking INSTALL-ON-MY-PC.bat
    echo   ^(do not run that one as administrator^), then run this again.
    echo.
    pause
    exit /b 2
)

echo   Program : %TARGET%
echo   Port    : TCP %PORT%, local network only
echo.

netsh advfirewall firewall delete rule name="%RULE%" >nul 2>&1
netsh advfirewall firewall add rule name="%RULE%" dir=in action=allow ^
    protocol=TCP localport=%PORT% remoteip=LocalSubnet program="%TARGET%" enable=yes

if errorlevel 1 (
    echo.
    echo     FAILED - the rule was not added.
    echo.
    pause
    exit /b 3
)

echo.
echo   ============================================
echo     ALLOWED
echo   ============================================
echo.
echo   Next: in your Remote Desktop session, run PAIR-WITH-MY-PC.bat
echo   and enter the code from the Pairing Code button in Remote Scanner.
echo.
pause
exit /b 0
