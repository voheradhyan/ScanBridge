@echo off
setlocal
REM ============================================================================
REM  Remote Scanner - pair this Remote Desktop session with the PC that has
REM  the scanner.
REM
REM  RUN THIS INSIDE YOUR REMOTE DESKTOP SESSION, on the server.
REM
REM  You only need this if scanning does not work on its own. Normally the
REM  scanner is carried by the Remote Desktop connection itself and no pairing
REM  is required. When that channel will not carry data - which some Remote
REM  Desktop clients and some group policies prevent - the server connects to
REM  your PC directly instead, and the two ends need a shared code first.
REM
REM  Get the code from the PC with the scanner: open Remote Scanner there and
REM  click "Pairing Code". It is copied to the clipboard, so you can paste it
REM  straight into this window.
REM
REM  The code is per person and per server. It is stored for your account only,
REM  so other people signed in to this server cannot use your scanner.
REM ============================================================================

title Remote Scanner - Pair With My PC
color 0B

set "HERE=%~dp0"

echo.
echo   ============================================
echo     REMOTE SCANNER - PAIRING
echo   ============================================
echo.
echo   On the PC with the scanner: open Remote Scanner,
echo   click "Pairing Code", and copy it.
echo.
echo   Then paste it here. To paste, right-click in this
echo   window, or press Ctrl+V.
echo.

set "CODE="
set /p "CODE=  Pairing code: "

if not defined CODE (
    echo.
    echo   Nothing entered. Nothing was changed.
    echo.
    pause
    exit /b 1
)

echo.
"%HERE%RemoteScanner.SessionAgent.exe" --pair="%CODE%"
set "RESULT=%errorlevel%"

if "%RESULT%"=="0" (
    echo   ============================================
    echo     PAIRED
    echo   ============================================
    echo.
    echo   Now sign out of this server and sign back in, then scan.
) else (
    echo   ============================================
    echo     NOT PAIRED
    echo   ============================================
    echo.
    echo   The code was not accepted. Check that all of it was copied -
    echo   it is 32 characters, in groups of five.
)

echo.
pause
exit /b %RESULT%
