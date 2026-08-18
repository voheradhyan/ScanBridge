<#
.SYNOPSIS
    Builds every component and lays out the client and server payloads.

.DESCRIPTION
    Produces two directories under build\:

      build\client\            ScanBridge.Client.exe (tray agent) + dependencies
      build\client\x64\        ScanHost x64, DvcPlugin x64
      build\client\x86\        ScanHost x86, DvcPlugin x86

      build\server\            ScanBridge.Server.exe (service, session agent, pairing)
      build\server\x64\        ScanBridge.ds (64-bit)
      build\server\x86\        ScanBridge.ds (32-bit)

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

# A freshly written file in a synced folder is sometimes held for a moment by whatever indexes
# it. Failing a whole build over that, having done nothing wrong, is not acceptable; neither is
# retrying forever and hiding a genuine lock.
function Find-ByteRun([byte[]] $haystack, [byte[]] $needle) {
    # Substring search over raw bytes. PowerShell has no built-in for this, and converting
    # 110 MB of executable to a string to use one would be worse than the loop.
    $first = $needle[0]
    $limit = $haystack.Length - $needle.Length
    for ($i = 0; $i -le $limit; $i++) {
        if ($haystack[$i] -ne $first) { continue }
        $matched = $true
        for ($j = 1; $j -lt $needle.Length; $j++) {
            if ($haystack[$i + $j] -ne $needle[$j]) { $matched = $false; break }
        }
        if ($matched) { return $true }
    }
    return $false
}

function Copy-WithRetry($source, $destination, $attempts = 5) {
    for ($i = 1; $i -le $attempts; $i++) {
        try {
            Copy-Item $source -Destination $destination -Force
            return
        } catch {
            if ($i -eq $attempts) { throw }
            Start-Sleep -Milliseconds (200 * $i)
        }
    }
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
& dotnet (Join-Path $scriptRoot 'ConstantCheck\bin\Release\net8.0\ScanBridge.ConstantCheck.dll') $repoRoot
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

# ------------------------------------------------------- payload the client carries
#
# Published before the solution is built, because the client embeds it as a resource and MSBuild
# reads the file at compile time. Get this order wrong and the build still succeeds — it simply
# produces a client with nothing inside it, which looks identical and fails at install.
#
# Single file so that what is embedded is one thing rather than a folder of a hundred, and
# self-contained because it exists to load 32-bit scanner drivers on machines where nobody
# asked for a 32-bit .NET runtime.

if (-not $SkipNative) {
    Write-Step "Publishing the 32-bit scan host (carried inside the client)"
    $payloadOut = Join-Path $buildRoot 'payload\x86'
    New-Item -ItemType Directory -Force -Path $payloadOut | Out-Null

    # Published into TEMP and copied in, rather than straight into build\.
    #
    # A single-file publish rewrites its output in place at the end, and this repository can sit
    # in a synced folder — OneDrive, Dropbox — whose indexer holds a handle on a file that has
    # just appeared. That surfaces as "the process cannot access the file", from a build step
    # that did nothing wrong, at random. TEMP is not synced, and the copy afterwards retries.
    $hostStaging = Join-Path $env:TEMP "rs-scanhost-$([guid]::NewGuid().ToString('N').Substring(0,8))"

    & dotnet publish (Join-Path $repoRoot 'src\ScanBridge.ScanHost\ScanBridge.ScanHost.csproj') `
        -c $Configuration -r win-x86 --self-contained true `
        -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none `
        -o $hostStaging --nologo -v quiet
    Invoke-Checked "32-bit scan host publish"

    Copy-WithRetry (Join-Path $hostStaging 'ScanBridge.ScanHost.exe') `
                   (Join-Path $payloadOut 'ScanBridge.ScanHost.exe')
    Remove-Item $hostStaging -Recurse -Force -ErrorAction SilentlyContinue

    $hostSize = [math]::Round((Get-Item (Join-Path $payloadOut 'ScanBridge.ScanHost.exe')).Length / 1MB, 1)
    Write-Host "    build\payload\x86\ScanBridge.ScanHost.exe  ($hostSize MB)"
}

# ----------------------------------------------------------------------- managed

Write-Step "Restoring and building the solution"
& dotnet build (Join-Path $repoRoot 'ScanBridge.sln') -c $Configuration --nologo -v minimal
Invoke-Checked "Solution build"

if (-not $SkipTests) {
    Write-Step "Running unit tests"
    & dotnet test (Join-Path $repoRoot 'tests\Unit\ScanBridge.Tests.Unit.csproj') `
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
& dotnet publish (Join-Path $repoRoot 'src\ScanBridge.Client\ScanBridge.Client.csproj') `
    -c $Configuration -r win-x64 --self-contained true -o $clientOut --nologo -v quiet
Invoke-Checked "Client publish"

& dotnet publish (Join-Path $repoRoot 'src\ScanBridge.ScanHost\ScanBridge.ScanHost.csproj') `
    -c $Configuration -r win-x64 --self-contained true -o "$clientOut\x64" --nologo -v quiet
Invoke-Checked "ScanHost x64 publish"

# The x86 host is self-contained: it exists to load 32-bit scanner drivers, and requiring
# users to also install the x86 .NET runtime for that would be a needless support burden.
& dotnet publish (Join-Path $repoRoot 'src\ScanBridge.ScanHost\ScanBridge.ScanHost.csproj') `
    -c $Configuration -r win-x86 --self-contained true -o "$clientOut\x86" --nologo -v quiet
Invoke-Checked "ScanHost x86 publish"

foreach ($arch in @('x64', 'x86')) {
    $plugin = Join-Path $repoRoot "build\$arch\ScanBridge.DvcPlugin.dll"
    if (Test-Path $plugin) {
        Copy-Item $plugin -Destination (Join-Path $clientOut $arch) -Force
    } elseif (-not $SkipNative) {
        throw "ScanBridge.DvcPlugin.dll ($arch) was not produced by the native build."
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
& dotnet publish (Join-Path $repoRoot 'src\ScanBridge.Server\ScanBridge.Server.csproj') `
    -c $Configuration -r win-x64 --self-contained true -o $serverOut --nologo -v quiet
Invoke-Checked "Server publish"

foreach ($arch in @('x64', 'x86')) {
    $dataSource = Join-Path $repoRoot "build\$arch\ScanBridge.ds"
    if (Test-Path $dataSource) {
        Copy-Item $dataSource -Destination (Join-Path $serverOut $arch) -Force
    } elseif (-not $SkipNative) {
        throw "ScanBridge.ds ($arch) was not produced by the native build."
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

# Diagnostics, not installers.
#
# Installing is the executables' own job now — "--install" does everything the old
# Install-Server.ps1 and Install-Client.ps1 did, from inside the build being installed, so
# those scripts are gone. What remains here is equipment: things that answer a question about
# a machine, or reproduce a fault, and are useless without a scanner in the room.
#
# They travel with the developer payload folders rather than inside the two distributable
# executables, because that is who they are for.
Copy-Item (Join-Path $scriptRoot 'CHECK-MY-SCANNER.bat')      -Destination $clientOut -Force
Copy-Item (Join-Path $scriptRoot 'ALLOW-DIRECT-CONNECTION.bat') -Destination $clientOut -Force
Copy-Item (Join-Path $scriptRoot 'COLLECT-LOGS.bat')          -Destination $clientOut -Force
Copy-Item (Join-Path $scriptRoot 'Collect-Logs.ps1')          -Destination $clientOut -Force
Copy-Item (Join-Path $scriptRoot 'TURN-ON-LOGGING-MY-PC.bat') -Destination $clientOut -Force
Copy-Item (Join-Path $scriptRoot 'READ-ME-FIRST-CLIENT.txt') `
    -Destination (Join-Path $clientOut 'READ-ME-FIRST.txt') -Force

Copy-Item (Join-Path $scriptRoot 'Check-Server.ps1')       -Destination $serverOut -Force
Copy-Item (Join-Path $scriptRoot 'CHECK-SERVER.bat')       -Destination $serverOut -Force
Copy-Item (Join-Path $scriptRoot 'TURN-ON-LOGGING.bat')    -Destination $serverOut -Force
Copy-Item (Join-Path $scriptRoot 'CHECK-TWAIN.bat')        -Destination $serverOut -Force
Copy-Item (Join-Path $scriptRoot 'SELF-TEST.bat')          -Destination $serverOut -Force
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

$distOut = Join-Path $buildRoot 'dist'
New-Item -ItemType Directory -Force -Path $distOut | Out-Null

# Every single-file publish stages through TEMP.
#
# The bundler writes its output, then rewrites the same file in place to append the payload.
# In a synced folder — this repository can live in OneDrive — the sync client opens the file
# the moment it appears, and the rewrite fails with "the process cannot access the file". The
# build had done nothing wrong; it simply lost a race with a file watcher. TEMP is not synced.
function Publish-SingleFile($project, $producedName, $distName, $description) {
    $staging = Join-Path $env:TEMP "sb-pack-$([guid]::NewGuid().ToString('N').Substring(0,8))"

    & dotnet publish $project `
        -c $Configuration -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none `
        -o $staging --nologo -v quiet
    Invoke-Checked $description

    $produced = Join-Path $staging $producedName
    if (-not (Test-Path $produced)) { throw "$description produced no executable." }

    Copy-WithRetry $produced (Join-Path $distOut $distName)
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue

    return [math]::Round((Get-Item (Join-Path $distOut $distName)).Length / 1MB, 1)
}

Write-Step "Packing the server into one file"
$packedSize = Publish-SingleFile (Join-Path $repoRoot 'src\ScanBridge.Server\ScanBridge.Server.csproj') `
    'ScanBridge.Server.exe' 'ScanBridge-Server.exe' 'Server single-file publish'
Write-Host "    build\dist\ScanBridge-Server.exe  ($packedSize MB, carries both data sources)"

Write-Step "Packing the client into one file"
$clientSize = Publish-SingleFile (Join-Path $repoRoot 'src\ScanBridge.Client\ScanBridge.Client.csproj') `
    'ScanBridge.Client.exe' 'ScanBridge-Client.exe' 'Client single-file publish'
Write-Host "    build\dist\ScanBridge-Client.exe  ($clientSize MB, carries the add-in and the 32-bit host)"

# The payload has to be inside, not merely intended to be. A build that missed it looks
# identical from outside and fails at the last step of an install, on somebody else's machine.
Write-Step "Checking both installers carry their payload"
foreach ($installer in @('ScanBridge-Server.exe', 'ScanBridge-Client.exe')) {
    $probe = Join-Path $env:TEMP "rs-payload-check-$([guid]::NewGuid().ToString('N').Substring(0,8))"
    & (Join-Path $distOut $installer) --extract $probe | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "$installer carries no payload (exit $LASTEXITCODE)." }

    $carried = @(Get-ChildItem $probe -Recurse -File)
    Write-Host "    $installer : $($carried.Count) file(s)"
    Remove-Item $probe -Recurse -Force -ErrorAction SilentlyContinue
}

# ---------------------------------------------------------------- icon on the binaries

# Does each shipped executable actually carry the mark?
#
# This is a gate rather than a glance because the answer was silently "no" for one of them and
# nothing noticed. ScanBridge.Server and ScanBridge.ScanHost had no ApplicationIcon at all, so
# they shipped with the default .NET apphost icon, through a full green build and a tagged
# release candidate. A person spotted it by looking at the file.
#
# The obvious check does not work. Icon.ExtractAssociatedIcon and the shell hand back the
# default icon when a PE has no icon resource, rather than failing, so an executable with the
# right icon and one with no icon at all are indistinguishable through that API - both were
# reporting a perfectly healthy "32x32". So this reads the frames out of scanbridge.ico and
# looks for those exact bytes in the file: .NET stores each frame as its own RT_ICON resource
# with the pixel data unchanged, and a single-file apphost does not compress Win32 resources,
# so a frame that is there is there verbatim.

Write-Step "Checking the mark is on the binaries"

$icoPath = Join-Path $repoRoot 'assets\scanbridge.ico'
if (-not (Test-Path $icoPath)) { throw "assets\scanbridge.ico is missing." }

$ico = [IO.File]::ReadAllBytes($icoPath)
if ([BitConverter]::ToUInt16($ico, 0) -ne 0 -or [BitConverter]::ToUInt16($ico, 2) -ne 1) {
    throw "assets\scanbridge.ico is not a valid icon file."
}

$frames = @()
$frameCount = [BitConverter]::ToUInt16($ico, 4)
for ($i = 0; $i -lt $frameCount; $i++) {
    $entry = 6 + $i * 16
    $width = $ico[$entry]; if ($width -eq 0) { $width = 256 }
    $size = [BitConverter]::ToUInt32($ico, $entry + 8)
    $offset = [BitConverter]::ToUInt32($ico, $entry + 12)
    $frames += [pscustomobject]@{
        Width = $width
        Bytes = $ico[$offset..($offset + $size - 1)]
    }
}

$iconTargets = @(
    (Join-Path $distOut 'ScanBridge-Server.exe'),
    (Join-Path $distOut 'ScanBridge-Client.exe'),
    (Join-Path $payloadOut 'ScanBridge.ScanHost.exe')
)

foreach ($target in $iconTargets) {
    if (-not (Test-Path $target)) { throw "expected $target to exist by now." }

    $bytes = [IO.File]::ReadAllBytes($target)
    $missing = @()
    foreach ($frame in $frames) {
        # IndexOf over the raw bytes. A frame is a few KB and the largest file here is ~110 MB,
        # so this is a second or two per binary and needs no PE parser.
        if (-not (Find-ByteRun $bytes $frame.Bytes)) { $missing += $frame.Width }
    }

    $name = Split-Path $target -Leaf
    if ($missing.Count -gt 0) {
        throw "$name is missing icon frame(s): $($missing -join ', ')px. Check <ApplicationIcon> in its .csproj."
    }
    Write-Host "    $name : all $($frames.Count) frames present"
}

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
$dataSource = Join-Path $serverOut 'x64\ScanBridge.ds'

if (-not $manager) {
    Write-Host "    No TWAIN manager installed on this machine; skipping." -ForegroundColor Yellow
    Write-Host "    Install NAPS2 to have every build verified automatically."
} elseif (-not (Test-Path $probe) -or -not (Test-Path $dataSource)) {
    Write-Host "    dsmprobe.exe or ScanBridge.ds is missing; skipping." -ForegroundColor Yellow
} else {
    & $probe $manager $dataSource | Write-Host
    if ($LASTEXITCODE -ne 0) {
        throw "A real TWAIN manager could not discover ScanBridge.ds (dsmprobe exit $LASTEXITCODE). " +
              "No scanning application would list this build. See docs/04-TROUBLESHOOTING.md."
    }
    Write-Host "    A real TWAIN manager discovers the data source." -ForegroundColor Green
}

Write-Host ""
Write-Host "Build complete." -ForegroundColor Green
Write-Host ""
Write-Host "  What you ship:" -ForegroundColor Green
Write-Host "    $distOut\ScanBridge-Server.exe --install     (on the server, as administrator)"
Write-Host "    $distOut\ScanBridge-Client.exe --install     (on the PC with the scanner, NOT elevated)"
Write-Host ""
Write-Host "  Developer payloads, with the diagnostic tools:"
Write-Host "    $clientOut"
Write-Host "    $serverOut"
