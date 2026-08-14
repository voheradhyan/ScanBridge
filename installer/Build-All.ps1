<#
.SYNOPSIS
    Builds every component and lays out the client and server payloads.

.DESCRIPTION
    Produces two directories under build\:

      build\client\            RemoteScanner.Client.exe (tray agent) + dependencies
      build\client\x64\        ScanHost x64, DvcPlugin x64
      build\client\x86\        ScanHost x86, DvcPlugin x86

      build\server\            RemoteScanner.Service.exe (service, session agent, pairing)
      build\server\x64\        RemoteScanner.ds (64-bit)
      build\server\x86\        RemoteScanner.ds (32-bit)

    Both bitnesses are produced throughout because TWAIN has no in-process bitness bridge:
    a 32-bit application can only load a 32-bit data source, and mstsc.exe can only load a
    plugin matching its own architecture.

.PARAMETER Configuration
    Release (default) or Debug.

.PARAMETER SkipNative
    Skip the C++ build. Only useful when iterating on managed code.
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release',
    [switch] $SkipNative,
    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptRoot
$buildRoot = Join-Path $repoRoot 'build'
$clientOut = Join-Path $buildRoot 'client'
$serverOut = Join-Path $buildRoot 'server'

function Write-Step($message) { Write-Host "`n==> $message" -ForegroundColor Cyan }
function Invoke-Checked($description) {
    if ($LASTEXITCODE -ne 0) { throw "$description failed with exit code $LASTEXITCODE." }
}

# ------------------------------------------------------- TWAIN constants, first of all
#
# Before anything is compiled, because a wrong constant makes everything built after it
# meaningless in a way no later step can detect. The manager forwards DG/DAT/MSG untouched, so
# the data source and dsmprobe share a header, agree with each other, and both disagree with
# every real application. Six wrong values here survived the entire suite below and were found
# by reading a log from the user's machine.

Write-Step "Checking TWAIN constants against a real TWAIN library"
$constantCheck = Join-Path $scriptRoot 'ConstantCheck\ConstantCheck.csproj'
& dotnet build $constantCheck -c Release --nologo -v quiet
Invoke-Checked "Constant-check build"
& dotnet (Join-Path $scriptRoot 'ConstantCheck\bin\Release\net8.0\RemoteScanner.ConstantCheck.dll') $repoRoot
Invoke-Checked "TWAIN constant check"

# ------------------------------------------------------------------------ native

if (-not $SkipNative) {
    Write-Step "Building native components (TWAIN data source, DVC plugin)"

    # vcvarsall.bat writes harmless notices to stderr (it probes for vswhere.exe). Windows
    # PowerShell turns any stderr from a native command into a terminating error, so the
    # preference is relaxed here and success is judged on the exit code, which is what
    # actually reports whether cl.exe and link.exe succeeded.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & cmd.exe /c "`"$(Join-Path $repoRoot 'native\build.cmd')`" both"
    } finally {
        $ErrorActionPreference = $previous
    }
    Invoke-Checked "Native build"
} else {
    Write-Host "Skipping the native build." -ForegroundColor Yellow
}

# ----------------------------------------------------------------------- managed

Write-Step "Restoring and building the solution"
& dotnet build (Join-Path $repoRoot 'RemoteScanner.sln') -c $Configuration --nologo -v minimal
Invoke-Checked "Solution build"

if (-not $SkipTests) {
    Write-Step "Running unit tests"
    & dotnet test (Join-Path $repoRoot 'tests\Unit\RemoteScanner.Tests.Unit.csproj') `
        -c $Configuration --nologo -v quiet
    Invoke-Checked "Unit tests"

    # The native transport has its own tests, and they are not optional. The first of them
    # covers the defect that made scanning over RDP impossible while every managed test
    # passed: a write on the DVC plugin's pipe serialised behind its reader thread and never
    # arrived. Nothing in the managed suite can see that, so it is gated here instead.
    if (-not $SkipNative) {
        Write-Step "Running native transport tests"
        foreach ($arch in @('x64', 'x86')) {
            $pipeTest = Join-Path $buildRoot "$arch\pipetest.exe"
            if (-not (Test-Path $pipeTest)) { throw "pipetest.exe is missing for $arch." }

            Write-Host "  --- $arch"
            & $pipeTest
            Invoke-Checked "Native transport tests ($arch)"
        }
    }
}

# ------------------------------------------------------------------ client payload

Write-Step "Publishing the client payload"
Remove-Item $clientOut -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $clientOut, "$clientOut\x64", "$clientOut\x86" | Out-Null

# Self-contained on purpose. This gets copied to colleagues' PCs by people who are not
# administrators of anything, and "first install the .NET 8 Desktop Runtime" is a step that
# turns a two-minute rollout into a support ticket. The extra ~140 MB is worth it.
& dotnet publish (Join-Path $repoRoot 'src\RemoteScanner.Client.UI\RemoteScanner.Client.UI.csproj') `
    -c $Configuration -r win-x64 --self-contained true -o $clientOut --nologo -v quiet
Invoke-Checked "Client publish"

& dotnet publish (Join-Path $repoRoot 'src\RemoteScanner.ScanHost\RemoteScanner.ScanHost.csproj') `
    -c $Configuration -r win-x64 --self-contained true -o "$clientOut\x64" --nologo -v quiet
Invoke-Checked "ScanHost x64 publish"

# The x86 host is self-contained: it exists to load 32-bit scanner drivers, and requiring
# users to also install the x86 .NET runtime for that would be a needless support burden.
& dotnet publish (Join-Path $repoRoot 'src\RemoteScanner.ScanHost\RemoteScanner.ScanHost.csproj') `
    -c $Configuration -r win-x86 --self-contained true -o "$clientOut\x86" --nologo -v quiet
Invoke-Checked "ScanHost x86 publish"

foreach ($arch in @('x64', 'x86')) {
    $plugin = Join-Path $repoRoot "build\$arch\RemoteScanner.DvcPlugin.dll"
    if (Test-Path $plugin) {
        Copy-Item $plugin -Destination (Join-Path $clientOut $arch) -Force
    } elseif (-not $SkipNative) {
        throw "RemoteScanner.DvcPlugin.dll ($arch) was not produced by the native build."
    }
}

# ------------------------------------------------------------------ server payload

Write-Step "Publishing the server payload"
Remove-Item $serverOut -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $serverOut, "$serverOut\x64", "$serverOut\x86" | Out-Null

# Self-contained: a production RDS host often has no .NET installed, and a change request to
# add one takes longer than shipping the runtime alongside.
#
# One publish, not two. The session agent is a role of this executable rather than a program of
# its own, so there is no second binary to keep in step.
& dotnet publish (Join-Path $repoRoot 'src\RemoteScanner.Service\RemoteScanner.Service.csproj') `
    -c $Configuration -r win-x64 --self-contained true -o $serverOut --nologo -v quiet
Invoke-Checked "Server publish"

foreach ($arch in @('x64', 'x86')) {
    $dataSource = Join-Path $repoRoot "build\$arch\RemoteScanner.ds"
    if (Test-Path $dataSource) {
        Copy-Item $dataSource -Destination (Join-Path $serverOut $arch) -Force
    } elseif (-not $SkipNative) {
        throw "RemoteScanner.ds ($arch) was not produced by the native build."
    }

    # Diagnostic tools travel with the server payload: a scanner that fails to appear can
    # only be diagnosed on the machine the scanning application runs on.
    foreach ($tool in @('dsmenum.exe', 'dstest.exe', 'dsmprobe.exe', 'pipetest.exe')) {
        $toolPath = Join-Path $repoRoot "build\$arch\$tool"
        if (Test-Path $toolPath) {
            Copy-Item $toolPath -Destination (Join-Path $serverOut $arch) -Force
        }
    }
}

# The .bat files are what a non-technical user actually double-clicks; the .ps1 files are
# what they call. Both are shipped so the scripts stay inspectable.
Copy-Item (Join-Path $scriptRoot 'Install-Client.ps1')     -Destination $clientOut -Force
Copy-Item (Join-Path $scriptRoot 'Uninstall-Client.ps1')   -Destination $clientOut -Force
Copy-Item (Join-Path $scriptRoot 'INSTALL-ON-MY-PC.bat')   -Destination $clientOut -Force
Copy-Item (Join-Path $scriptRoot 'CHECK-MY-SCANNER.bat')   -Destination $clientOut -Force
Copy-Item (Join-Path $scriptRoot 'ALLOW-DIRECT-CONNECTION.bat') -Destination $clientOut -Force
Copy-Item (Join-Path $scriptRoot 'UNINSTALL.bat')          -Destination $clientOut -Force

Copy-Item (Join-Path $scriptRoot 'COLLECT-LOGS.bat')          -Destination $clientOut -Force
Copy-Item (Join-Path $scriptRoot 'Collect-Logs.ps1')          -Destination $clientOut -Force
Copy-Item (Join-Path $scriptRoot 'TURN-ON-LOGGING-MY-PC.bat') -Destination $clientOut -Force
Copy-Item (Join-Path $scriptRoot 'READ-ME-FIRST-CLIENT.txt') `
    -Destination (Join-Path $clientOut 'READ-ME-FIRST.txt') -Force

Copy-Item (Join-Path $scriptRoot 'Install-Server.ps1')     -Destination $serverOut -Force
Copy-Item (Join-Path $scriptRoot 'Uninstall-Server.ps1')   -Destination $serverOut -Force
Copy-Item (Join-Path $scriptRoot 'Check-Server.ps1')       -Destination $serverOut -Force
Copy-Item (Join-Path $scriptRoot 'INSTALL-ON-SERVER.bat')  -Destination $serverOut -Force
Copy-Item (Join-Path $scriptRoot 'UNINSTALL-SERVER.bat')   -Destination $serverOut -Force
Copy-Item (Join-Path $scriptRoot 'CHECK-SERVER.bat')       -Destination $serverOut -Force
Copy-Item (Join-Path $scriptRoot 'TURN-ON-LOGGING.bat')    -Destination $serverOut -Force
Copy-Item (Join-Path $scriptRoot 'CHECK-TWAIN.bat')        -Destination $serverOut -Force
Copy-Item (Join-Path $scriptRoot 'SELF-TEST.bat')          -Destination $serverOut -Force
# Reproduces a NAPS2 scan on the PC with the scanner, with no server and no RDP hop. Shipped
# in the payload rather than kept in installer\ because it is used on whichever machine is
# convenient, and because the payload is the folder that gets copied around.
Copy-Item (Join-Path $scriptRoot 'TEST-WITH-NAPS2-LOCALLY.ps1') -Destination $serverOut -Force
Copy-Item (Join-Path $scriptRoot 'PAIR-WITH-MY-PC.bat')    -Destination $serverOut -Force
Copy-Item (Join-Path $scriptRoot 'COLLECT-LOGS.bat')       -Destination $serverOut -Force
Copy-Item (Join-Path $scriptRoot 'Collect-Logs.ps1')       -Destination $serverOut -Force

Copy-Item (Join-Path $scriptRoot 'READ-ME-FIRST-SERVER.txt') `
    -Destination (Join-Path $serverOut 'READ-ME-FIRST.txt') -Force

# ------------------------------------------------------- the distributable single file
#
# What a person actually downloads. The folder above stays because the diagnostic tools
# (dsmprobe, pipetest, the self-test) are developer equipment and have no business inside a
# user-facing installer.
#
# Compression roughly halves it at the cost of a second or so of first-run extraction, which is
# the right trade for something copied across a network to a server.

Write-Step "Packing the server into one file"
$distOut = Join-Path $buildRoot 'dist'
New-Item -ItemType Directory -Force -Path $distOut | Out-Null

& dotnet publish (Join-Path $repoRoot 'src\RemoteScanner.Service\RemoteScanner.Service.csproj') `
    -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none `
    -o (Join-Path $buildRoot 'dist-staging') --nologo -v quiet
Invoke-Checked "Server single-file publish"

$packed = Join-Path $buildRoot 'dist-staging\RemoteScanner.Service.exe'
if (-not (Test-Path $packed)) { throw "The single-file publish produced no executable." }

Copy-Item $packed -Destination (Join-Path $distOut 'RemoteScanner-Server.exe') -Force
Remove-Item (Join-Path $buildRoot 'dist-staging') -Recurse -Force -ErrorAction SilentlyContinue

$packedSize = [math]::Round((Get-Item (Join-Path $distOut 'RemoteScanner-Server.exe')).Length / 1MB, 1)
Write-Host "    build\dist\RemoteScanner-Server.exe  ($packedSize MB, carries both data sources)"

# ------------------------------------------------------------------ verification

# Ask a real TWAIN manager whether it can discover the data source we just built.
#
# This exists because the data source once exported DSM_Entry instead of DS_Entry. It loaded,
# it initialised, it answered every direct call correctly, the unit tests passed, and no
# scanning application on earth could see it — a manager resolves DS_Entry, finds nothing, and
# drops the file without a word. Nothing we can assert about the data source from the outside
# catches that. Only a real manager does, so one is asked here, every build.
#
# Skipped rather than failed when no manager is installed: not every build machine has one,
# and refusing to produce a payload over a missing test dependency helps nobody.

Write-Step "Verifying the data source against a real TWAIN manager"

$managerCandidates = @(
    (Join-Path $env:ProgramFiles 'NAPS2\lib\_win64\twaindsm.dll'),
    (Join-Path ${env:ProgramFiles(x86)} 'NAPS2\lib\_win64\twaindsm.dll'),
    (Join-Path $env:LOCALAPPDATA 'Programs\NAPS2\lib\_win64\twaindsm.dll')
)

$manager = $managerCandidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
$probe = Join-Path $serverOut 'x64\dsmprobe.exe'
$dataSource = Join-Path $serverOut 'x64\RemoteScanner.ds'

if (-not $manager) {
    Write-Host "    No TWAIN manager installed on this machine; skipping." -ForegroundColor Yellow
    Write-Host "    Install NAPS2 to have every build verified automatically."
} elseif (-not (Test-Path $probe) -or -not (Test-Path $dataSource)) {
    Write-Host "    dsmprobe.exe or RemoteScanner.ds is missing; skipping." -ForegroundColor Yellow
} else {
    & $probe $manager $dataSource | Write-Host
    if ($LASTEXITCODE -ne 0) {
        throw "A real TWAIN manager could not discover RemoteScanner.ds (dsmprobe exit $LASTEXITCODE). " +
              "No scanning application would list this build. See docs/04-TROUBLESHOOTING.md."
    }
    Write-Host "    A real TWAIN manager discovers the data source." -ForegroundColor Green
}

Write-Host ""
Write-Host "Build complete." -ForegroundColor Green
Write-Host "  Client payload: $clientOut"
Write-Host "  Server payload: $serverOut"
Write-Host ""
Write-Host "Install with:"
Write-Host "  on the PC with the scanner   :  $clientOut\Install-Client.ps1"
Write-Host "  on the Windows Server (admin):  $serverOut\Install-Server.ps1"
