@echo off
setlocal enabledelayedexpansion
REM ============================================================================
REM  ScanBridge - full self-test, WITHOUT a server and WITHOUT RDP.
REM
REM  RUN THIS ON THE PC THAT HAS THE SCANNER PLUGGED INTO IT.
REM  (Copy this whole folder over to that PC first.)
REM
REM  It runs every part of ScanBridge except the RDP hop:
REM
REM      scanning program -> ScanBridge.ds -> session agent
REM                       -> ScanBridge tray app -> scanner
REM
REM  and finishes by scanning one page for real and saving it as a picture.
REM
REM  Why this is worth running: when scanning through RDP does not work, this
REM  says which half is broken. If this test passes, scanning itself is fine and
REM  the problem is the RDP connection. If it fails, ignore RDP entirely - the
REM  problem is here, and the logs will say where.
REM ============================================================================

title ScanBridge - Self Test
color 0B

set "HERE=%~dp0"
set "OUTPUT=%TEMP%\ScanBridge-selftest.bmp"

echo.
echo   ============================================
echo     REMOTE SCANNER - SELF TEST
echo   ============================================
echo.
echo   This scans one page from the scanner attached to THIS PC,
echo   through the ScanBridge driver, with no server involved.
echo.
echo   Put a page in the scanner if you want to see something on it.
echo   An empty scanner is fine too - a blank page still proves it works.
echo.
pause

REM -------------------------------------------------- 1. transport check
echo.
echo   [1/5] Checking the connection plumbing...

REM Runs before anything else because it needs no scanner, no tray app and no TWAIN manager,
REM and because the fault it looks for used to be invisible: two threads sharing one pipe
REM handle, where a message could be accepted and then never delivered while both ends
REM reported themselves healthy. Two seconds here versus a whole afternoon of logs.
"%HERE%x64\pipetest.exe"
if errorlevel 1 (
    echo.
    echo   The connection plumbing on this PC is faulty - see the lines above.
    echo.
    echo   Nothing else in this test can be trusted until that passes, so it
    echo   stops here. Send this screen to whoever gave you this build.
    echo.
    pause
    exit /b 1
)

REM -------------------------------------------------- 2. is the tray app running
echo.
echo   [2/5] Checking the ScanBridge tray app...

REM /fo csv is deliberate. The default table output truncates the Image Name column at 25
REM characters, so a name longer than that never matches and the check reports "not running"
REM about a process that is running perfectly well. CSV prints the whole name, quoted.
tasklist /fi "imagename eq ScanBridge.Client.exe" /fo csv /nh 2>nul | find /i "ScanBridge.Client" >nul
if errorlevel 1 (
    echo.
    echo   The ScanBridge tray app is NOT running on this PC.
    echo.
    echo   It is the part that owns the scanner, so nothing can work without it.
    echo   Start it from the Start Menu ^(search for "ScanBridge"^), wait for
    echo   the tray icon to appear, then run this test again.
    echo.
    pause
    exit /b 1
)
echo         running.

REM -------------------------------------------------- 3. find a TWAIN manager
echo.
echo   [3/5] Looking for a TWAIN manager to test with...

set "DSM="
if not "%~1"=="" set "DSM=%~1"

if not defined DSM if exist "%ProgramFiles%\NAPS2\lib\_win64\twaindsm.dll" set "DSM=%ProgramFiles%\NAPS2\lib\_win64\twaindsm.dll"
if not defined DSM if exist "%ProgramFiles(x86)%\NAPS2\lib\_win64\twaindsm.dll" set "DSM=%ProgramFiles(x86)%\NAPS2\lib\_win64\twaindsm.dll"
if not defined DSM if exist "%LOCALAPPDATA%\Programs\NAPS2\lib\_win64\twaindsm.dll" set "DSM=%LOCALAPPDATA%\Programs\NAPS2\lib\_win64\twaindsm.dll"

if not defined DSM (
    echo.
    echo   No TWAIN manager found on this PC.
    echo.
    echo   This test borrows one from a scanning program. The easiest is NAPS2,
    echo   which is free:  https://www.naps2.com
    echo.
    echo   Install it, then run this test again. Or, if you already have one
    echo   somewhere else, drag its twaindsm.dll onto this .bat file.
    echo.
    pause
    exit /b 1
)
echo         using %DSM%

REM -------------------------------------------------- 4. start the session agent
echo.
echo   [4/5] Starting the session agent in loopback mode...

REM Any agent left over from a previous run would hold the pipe this one needs.
REM
REM Matched on the command line, not the image name: the session agent is now a role of
REM ScanBridge.Server.exe, and killing every process with that name would stop an installed
REM service too — on a machine that is both a client and a server, that is somebody else's
REM scanning session.
call :STOP_AGENTS

start "ScanBridge session agent (loopback)" /min ^
    "%HERE%ScanBridge.Server.exe" --session-agent --loopback

REM Give it time to start and connect to the tray app before the driver looks for it.
REM Polled rather than a fixed wait: a cold start on a loaded machine can take several
REM seconds, and a fixed wait either fails on a slow PC or wastes time on a fast one.
set "AGENT_UP="
for /l %%N in (1,1,15) do (
    if not defined AGENT_UP (
        ping -n 2 127.0.0.1 >nul
        call :AGENT_RUNNING && set "AGENT_UP=1"
    )
)

if not defined AGENT_UP (
    echo.
    echo   The session agent did not start. See the log:
    echo     %LocalAppData%\ScanBridge\logs\sessionagent-*.log
    echo.
    pause
    exit /b 1
)
echo         started.

REM -------------------------------------------------- 5. scan
echo.
echo   [5/5] Scanning one page. This takes up to a minute...
echo.

del "%OUTPUT%" >nul 2>&1

REM Two scans, because "a page transferred" is not one fact.
REM
REM TWAIN has four transfer mechanisms and they share almost no code inside the driver. Memory
REM -file transfer goes first: it is the one NAPS2 asks for, and it was missing while the other
REM three worked - which produced a scan that ran the scanner, sent the page, and then failed
REM at the last step with an error dialog on top of an image the user never received. A test
REM that exercises only one mechanism cannot see that.
echo   - memory-file transfer ^(the one NAPS2 asks for^)
"%HERE%x64\dsmprobe.exe" "%DSM%" "%HERE%x64\ScanBridge.ds" --scan "%OUTPUT%" --memfile --timeout 150
set "RESULT=%errorlevel%"

if "%RESULT%"=="0" (
    echo.
    echo   - file transfer
    "%HERE%x64\dsmprobe.exe" "%DSM%" "%HERE%x64\ScanBridge.ds" --scan "%OUTPUT%" --timeout 150
    set "RESULT=!errorlevel!"
)

call :STOP_AGENTS

echo.
echo   ============================================
if "%RESULT%"=="0" (
    echo     PASSED
    echo   ============================================
    echo.
    echo   A page was scanned through the ScanBridge driver and saved to:
    echo     %OUTPUT%
    echo.
    echo   Opening it now - if you see your page, everything except the RDP
    echo   connection is working correctly.
    echo.
    if exist "%OUTPUT%" start "" "%OUTPUT%"
) else (
    echo     FAILED
    echo   ============================================
    echo.
    echo   The scan did not complete. The messages above say how far it got.
    echo   The logs have the detail:
    echo     %LocalAppData%\ScanBridge\logs\
    echo.
    echo   Most useful, in this order:
    echo     twainds-*.log       what the driver did
    echo     sessionagent-*.log  whether it reached the tray app
    echo     agent-*.log         whether the tray app reached the scanner
    echo.
    echo   Run COLLECT-LOGS.bat to bundle them all into one zip file.
)
echo.
pause
exit /b %RESULT%

REM ---------------------------------------------------------------------------
REM  Helpers. Both match on the command line rather than the image name, because
REM  the session agent shares its executable with the server service and killing
REM  by name would stop a real one.
REM ---------------------------------------------------------------------------

:STOP_AGENTS
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
    "Get-CimInstance Win32_Process -Filter \"Name='ScanBridge.Server.exe'\" | Where-Object { $_.CommandLine -like '*--session-agent*' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }" >nul 2>&1
exit /b 0

:AGENT_RUNNING
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
    "$p = Get-CimInstance Win32_Process -Filter \"Name='ScanBridge.Server.exe'\" | Where-Object { $_.CommandLine -like '*--session-agent*' }; if ($p) { exit 0 } else { exit 1 }" >nul 2>&1
exit /b %errorlevel%
