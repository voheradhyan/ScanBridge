@echo off
REM ============================================================================
REM  ScanBridge - install on the Windows Server everyone remotes into.
REM
REM  Double-click this file. It asks for administrator rights itself, so there
REM  is no need to right-click.
REM
REM  Administrator IS required here: it writes the virtual scanner driver into
REM  C:\Windows\twain_32 and twain_64, and registers a Windows service.
REM ============================================================================

title ScanBridge - Install on Server
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
echo     REMOTE SCANNER - INSTALL ON SERVER
echo   ============================================
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-Server.ps1"
set RESULT=%errorlevel%

echo.
if %RESULT% neq 0 (
    color 0C
    echo   ============================================
    echo     INSTALL FAILED
    echo   ============================================
    echo.
    echo   Send the messages above to your IT contact.
) else (
    color 0A
    echo   ============================================
    echo     DONE
    echo   ============================================
    echo.
    echo   The server is ready.
    echo.
    echo   Next: install on each PC that has a scanner,
    echo   using INSTALL-ON-MY-PC.bat
)

echo.
pause
