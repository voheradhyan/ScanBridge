@echo off
setlocal enabledelayedexpansion
rem Builds the native components for both bitnesses.
rem   x64 -> loaded by 64-bit hosts (Acrobat, most modern apps) and by mstsc.exe
rem   x86 -> loaded by 32-bit hosts, which is still most line-of-business ERP software
rem
rem Usage: build.cmd [x64|x86|both]   (default: both)
rem
rem Failures are reported with explicit "if errorlevel" checks rather than the shorter
rem "cl ... || exit /b 1" form. That form looks equivalent and is not: combined with
rem "call :label" and parenthesised blocks it silently returned 0 from a build in which the
rem compiler had failed, so a broken native build looked like a clean one and the packaging
rem step happily shipped the previous binaries.

set "ROOT=%~dp0"
set "OUT=%ROOT%..\build"
set "ARCH=%~1"
if "%ARCH%"=="" set "ARCH=both"

set "VCVARS=%ProgramFiles(x86)%\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvarsall.bat"
if not exist "%VCVARS%" set "VCVARS=%ProgramFiles%\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvarsall.bat"
if not exist "%VCVARS%" (
    echo ERROR: Visual Studio C++ build tools not found.
    exit /b 1
)

if /i "%ARCH%"=="both" (
    call :build x64
    if errorlevel 1 exit /b 1
    call :build x86
    if errorlevel 1 exit /b 1
    exit /b 0
)

call :build %ARCH%
if errorlevel 1 exit /b 1
exit /b 0

rem ---------------------------------------------------------------------------

:build
set "TARGET=%~1"
echo.
echo === Building %TARGET% ===
if not exist "%OUT%\%TARGET%" mkdir "%OUT%\%TARGET%"

call "%VCVARS%" %TARGET% >nul
if errorlevel 1 (
    echo ERROR: vcvarsall.bat failed for %TARGET%.
    exit /b 1
)

rem /EHsc  : C++ exceptions; nothing may escape DS_Entry into the host process
rem /GS    : buffer security checks - this DLL parses data off a wire
rem /guard:cf, /DYNAMICBASE, /NXCOMPAT, /HIGHENTROPYVA : exploit mitigations, since we are
rem          loaded into third-party processes and must not weaken them
set "CFLAGS=/nologo /c /EHsc /O2 /MT /W4 /GS /guard:cf /std:c++17 /DUNICODE /D_UNICODE /DWIN32_LEAN_AND_MEAN /I"%ROOT%include""
set "LFLAGS=/nologo /DLL /guard:cf /DYNAMICBASE /NXCOMPAT"
if /i "%TARGET%"=="x64" set "LFLAGS=%LFLAGS% /HIGHENTROPYVA"

pushd "%OUT%\%TARGET%"

echo --- RemoteScanner.ds
cl %CFLAGS% "%ROOT%RemoteScanner.TwainDS\ds.cpp" /Fo:ds.obj
if errorlevel 1 goto :failed

link %LFLAGS% /DEF:"%ROOT%RemoteScanner.TwainDS\RemoteScanner.def" ^
     /OUT:RemoteScanner.ds ds.obj ^
     kernel32.lib user32.lib advapi32.lib shell32.lib ole32.lib windowscodecs.lib bcrypt.lib crypt32.lib
if errorlevel 1 goto :failed

echo --- RemoteScanner.DvcPlugin.dll
if exist "%ROOT%RemoteScanner.DvcPlugin\plugin.cpp" (
    cl %CFLAGS% "%ROOT%RemoteScanner.DvcPlugin\plugin.cpp" /Fo:plugin.obj
    if errorlevel 1 goto :failed

    link %LFLAGS% /DEF:"%ROOT%RemoteScanner.DvcPlugin\Plugin.def" ^
         /OUT:RemoteScanner.DvcPlugin.dll plugin.obj ^
         kernel32.lib user32.lib advapi32.lib shell32.lib ole32.lib oleaut32.lib bcrypt.lib crypt32.lib
    if errorlevel 1 goto :failed
)

rem Diagnostic tools. Shipped with the server payload because "the driver is installed but
rem the application does not list it" cannot be diagnosed from outside the machine - these
rem show whether the data source loads, what it reports, and what a real TWAIN manager
rem actually returns to an application.
echo --- diagnostic tools
set "TOOLFLAGS=/nologo /EHsc /O2 /MT /W3 /std:c++17 /DWIN32_LEAN_AND_MEAN /I"%ROOT%include""
set "TOOLLIBS=kernel32.lib user32.lib shlwapi.lib advapi32.lib bcrypt.lib crypt32.lib"

rem pipetest is a test, not a diagnostic: it runs during the build (below) and its first check
rem is the one that catches the deadlock which made scanning over RDP impossible.
for %%T in (dstest dsmenum dsmprobe pipetest) do (
    cl %TOOLFLAGS% "%ROOT%tools\%%T.cpp" /Fe:%%T.exe /Fo:%%T.obj /link /SUBSYSTEM:CONSOLE %TOOLLIBS%
    if errorlevel 1 goto :failed
)

popd
echo === %TARGET% OK ===
exit /b 0

:failed
popd
echo *** %TARGET% BUILD FAILED ***
exit /b 1
