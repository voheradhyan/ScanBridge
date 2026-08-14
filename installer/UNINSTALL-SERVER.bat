@echo off
REM ============================================================================
REM  ScanBridge - remove from the Windows Server.
REM
REM  Double-click this file. It asks for administrator rights itself, so there
REM  is no need to right-click it or open PowerShell.
REM
REM  Administrator IS required: it deletes the virtual scanner driver from
REM  C:\Windows\twain_32 and twain_64 and removes a Windows service.
REM
REM  This exists because the guidance used to be "right-click the .ps1 and
REM  choose Run with PowerShell, as an administrator" - which does not work.
REM  That menu item runs PowerShell WITHOUT elevation, so the script gets as far
REM  as its administrator check and refuses, with no obvious way forward.
REM ============================================================================

title ScanBridge - Remove from Server
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
echo     REMOTE SCANNER - REMOVE FROM SERVER
echo   ============================================
echo.
echo   This removes the virtual scanner driver and the Windows service
echo   from THIS server. Scanners on people's own PCs are untouched.
echo.
echo   Close any scanning programs first, or files still in use cannot
echo   be deleted and the removal finishes only after a restart.
echo.
pause

echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Uninstall-Server.ps1"
set RESULT=%errorlevel%

echo.
if %RESULT% neq 0 (
    color 0C
    echo   ============================================
    echo     REMOVAL FAILED
    echo   ============================================
    echo.
    echo   Send the messages above to your IT contact.
) else (
    color 0A
    echo   ============================================
    echo     DONE
    echo   ============================================
    echo.
    echo   Everyone must sign out of the server and back in before
    echo   scanning programs stop listing the remote scanner.
)

echo.
pause
