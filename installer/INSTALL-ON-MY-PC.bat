@echo off
REM ============================================================================
REM  ScanBridge - install on a PC that has a scanner attached.
REM
REM  Double-click this file. Do NOT right-click "Run as administrator".
REM
REM  The Remote Desktop add-in is registered under HKEY_CURRENT_USER, which is
REM  where mstsc.exe looks for it. Running elevated registers it for the
REM  administrator account instead, and it would silently never load - so this
REM  script refuses to run elevated rather than appearing to succeed.
REM ============================================================================

title ScanBridge - Install on this PC
color 0B

echo.
echo   ============================================
echo     REMOTE SCANNER - INSTALL ON THIS PC
echo   ============================================
echo.

REM "net session" only succeeds when elevated - the standard admin probe.
net session >nul 2>&1
if %errorlevel% equ 0 (
    color 0E
    echo   STOP - this was started as administrator.
    echo.
    echo   Close this window, then DOUBLE-CLICK the file instead.
    echo   Do not use "Run as administrator".
    echo.
    echo   Why: your scanner and your Remote Desktop settings belong to your
    echo   own Windows account. Installing as administrator puts them in the
    echo   wrong account and nothing would work.
    echo.
    pause
    exit /b 1
)

echo   Installing for Windows user: %USERNAME%
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-Client.ps1"
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
    echo   Look for the ScanBridge icon near the clock,
    echo   at the bottom-right of your screen.
    echo.
    echo   IMPORTANT - connect to the server using "Remote Desktop
    echo   Connection" ^(mstsc^). The newer "Windows App" from the
    echo   Microsoft Store will NOT work with scanners.
)

echo.
pause
