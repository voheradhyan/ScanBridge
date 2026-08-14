@echo off
REM ============================================================================
REM  ScanBridge - check what scanning programs can actually see.
REM
REM  Run on the SERVER when "ScanBridge" does not appear in a scanning
REM  program's device list.
REM
REM  Part 2 is the one that decides it. A real TWAIN manager is loaded, pointed
REM  at a scratch folder, given our driver, and asked what it found - which is
REM  exactly the question the scanning program asks. Everything else here is
REM  supporting detail.
REM ============================================================================

setlocal enabledelayedexpansion
title ScanBridge - Check TWAIN
color 0B

set "DS32=%SystemRoot%\twain_32\ScanBridge\ScanBridge.ds"
set "DS64=%SystemRoot%\twain_64\ScanBridge\ScanBridge.ds"

echo.
echo   ============================================
echo     1. IS THE DRIVER INSTALLED?
echo   ============================================
echo.
echo   Copying a new folder onto the server does NOT update the installed
echo   driver. Only ScanBridge-Server.exe --install does that.
echo.

if exist "%DS32%" (for %%F in ("%DS32%") do echo     32-bit : %%~tF  %%~zF bytes) else echo     32-bit : MISSING
if exist "%DS64%" (for %%F in ("%DS64%") do echo     64-bit : %%~tF  %%~zF bytes) else echo     64-bit : MISSING

REM An earlier installer also put a copy one level up. A TWAIN 2.x manager reads
REM both levels, so a leftover copy shows the scanner twice in every program.
if exist "%SystemRoot%\twain_32\ScanBridge.ds" (
    echo.
    echo     ! %SystemRoot%\twain_32\ScanBridge.ds is a leftover duplicate.
    echo       Re-run ScanBridge-Server.exe --install to remove it.
)
if exist "%SystemRoot%\twain_64\ScanBridge.ds" (
    echo.
    echo     ! %SystemRoot%\twain_64\ScanBridge.ds is a leftover duplicate.
    echo       Re-run ScanBridge-Server.exe --install to remove it.
)

echo.
echo   ============================================
echo     2. WOULD A REAL TWAIN MANAGER FIND IT?
echo   ============================================
echo.
echo   This loads a genuine TWAIN manager, hands it our driver in a scratch
echo   folder, and prints what it lists. It needs no admin rights and does not
echo   touch the installed copy.
echo.

set "DSM="
if exist "%ProgramFiles%\NAPS2\lib\_win64\twaindsm.dll" set "DSM=%ProgramFiles%\NAPS2\lib\_win64\twaindsm.dll"
if not defined DSM if exist "%ProgramFiles(x86)%\NAPS2\lib\_win64\twaindsm.dll" set "DSM=%ProgramFiles(x86)%\NAPS2\lib\_win64\twaindsm.dll"
if not defined DSM if exist "%LOCALAPPDATA%\Programs\NAPS2\lib\_win64\twaindsm.dll" set "DSM=%LOCALAPPDATA%\Programs\NAPS2\lib\_win64\twaindsm.dll"

if defined DSM (
    "%~dp0x64\dsmprobe.exe" "%DSM%" "%~dp0x64\ScanBridge.ds"
) else (
    echo   No TWAIN manager found on this machine to test against.
    echo.
    echo   Install NAPS2 ^(free, https://www.naps2.com^) and run this again, or
    echo   point the tool at one you already have:
    echo     x64\dsmprobe.exe "C:\path\to\twaindsm.dll" x64\ScanBridge.ds
)

echo.
echo   ============================================
echo     3. DOES THE INSTALLED FILE LOAD?
echo   ============================================

if exist "%DS32%" (
    echo.
    echo   --- installed 32-bit driver ---
    "%~dp0x86\dstest.exe" "%DS32%"
)
if exist "%DS64%" (
    echo.
    echo   --- installed 64-bit driver ---
    "%~dp0x64\dstest.exe" "%DS64%"
)

echo.
echo   ============================================
echo     4. WHAT THE MANAGERS ON THIS MACHINE REPORT
echo   ============================================

echo.
echo   ### 32-bit programs ###
if exist "%~dp0x86\dsmenum.exe" "%~dp0x86\dsmenum.exe"

echo.
echo   ### 64-bit programs ###
if exist "%~dp0x64\dsmenum.exe" "%~dp0x64\dsmenum.exe"

echo.
echo   ============================================
echo     HOW TO READ THIS
echo   ============================================
echo.
echo   Part 2 says PASS:
echo     The driver is correct. If a program still shows nothing, either it
echo     was open before the driver was installed - restart it - or it is
echo     the wrong bitness, or it is not a TWAIN program at all.
echo.
echo   Part 2 says FAIL:
echo     The driver itself is at fault. Nothing about the installation will
echo     help. Send the output above.
echo.
echo   Part 4 shows "condition code 3" from twain_32.dll:
echo     Normal on a server, and not a fault. The twain_32.dll Windows ships
echo     is not a real TWAIN manager any more - it only re-presents WIA
echo     devices plugged into this machine, and never reads .ds files at all.
echo     Real scanning programs carry their own manager, which is the one
echo     Part 2 tests.
echo.
echo   Windows Fax and Scan will never list ScanBridge. It is WIA-only.
echo   Use a TWAIN program.
echo.
pause
