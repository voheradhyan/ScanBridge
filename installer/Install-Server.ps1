<#
.SYNOPSIS
    Installs the RemoteScanner server component on a Windows Server RDS host.

.DESCRIPTION
    Installs the virtual TWAIN data source into both TWAIN search paths, installs the control
    service, and registers the Windows Event Log source.

    Requires administrator: it writes to C:\Windows\twain_32 and twain_64, creates a service,
    and creates an event log source.

    The data source is installed in BOTH bitnesses on purpose. TWAIN has no in-process
    bitness bridge, so a 32-bit application (which most line-of-business ERP software still
    is) can only load a 32-bit .ds, and a 64-bit application such as Adobe Acrobat can only
    load a 64-bit one. Installing one would leave half the applications with no scanner.

.PARAMETER Source
    Directory holding the built payload. Defaults to ..\build\server next to this script.

.EXAMPLE
    .\Install-Server.ps1
#>
[CmdletBinding()]
param(
    [string] $Source,
    [string] $InstallDirectory = (Join-Path $env:ProgramFiles 'RemoteScanner'),
    [switch] $NoStart
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path

# This script runs from two different places and has to find the payload in both:
#   * deployed  - it sits INSIDE the payload folder that was copied to the server
#   * developing - it sits in installer\ and the payload is in ..\build\server
# The deployed case is checked first, because that is the one real users hit.
if (-not $Source) {
    if (Test-Path (Join-Path $scriptRoot 'RemoteScanner.Service.exe')) {
        $Source = $scriptRoot
    } else {
        $Source = Join-Path (Split-Path -Parent $scriptRoot) 'build\server'
    }
}

function Write-Step($message) { Write-Host "==> $message" -ForegroundColor Cyan }
function Write-Warn($message) { Write-Host "    ! $message" -ForegroundColor Yellow }

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "This script must be run as administrator."
}

if (-not (Test-Path $Source)) {
    throw "Payload not found at '$Source'. Run installer\Build-All.ps1 first."
}

$serviceName = 'RemoteScanner'

Write-Step "Stopping any existing service"
$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing) {
    if ($existing.Status -ne 'Stopped') {
        Stop-Service -Name $serviceName -Force
        $existing.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    # sc.exe delete is asynchronous; the files stay locked until every handle closes.
    & sc.exe delete $serviceName | Out-Null
    Start-Sleep -Seconds 2
}

# Stopping the service does not stop the agents it started: they run in users' sessions and
# outlive it. An agent left behind keeps the named pipe its session's data sources connect to,
# while its virtual channel back to the user's PC is dead — so applications connect, are
# authenticated, and then wait for a reply that can never come. It looks exactly like the
# scanner being offline.
Write-Step "Stopping session agents left over from a previous install"
$orphans = @(Get-Process -Name 'RemoteScanner.SessionAgent' -ErrorAction SilentlyContinue)
if ($orphans.Count -eq 0) {
    Write-Host "    none running"
} else {
    foreach ($orphan in $orphans) {
        try {
            $orphan.Kill()
            $orphan.WaitForExit(5000) | Out-Null
            Write-Host "    stopped pid $($orphan.Id)"
        } catch {
            # Another user's session; the service will replace it when that user reconnects,
            # and the agent itself now clears stale ones in its own session at startup.
            Write-Warn "could not stop pid $($orphan.Id): $($_.Exception.Message)"
        }
    }
}

Write-Step "Installing to $InstallDirectory"
New-Item -ItemType Directory -Force -Path $InstallDirectory | Out-Null

# Re-running the installer from inside the install directory would copy the folder onto
# itself and error on every file.
$sourceFull = (Resolve-Path $Source).Path.TrimEnd('\')
$targetFull = (Resolve-Path $InstallDirectory).Path.TrimEnd('\')
if ($sourceFull -ieq $targetFull) {
    Write-Host "    Already installed here; skipping the file copy."
} else {
    Copy-Item -Path (Join-Path $Source '*') -Destination $InstallDirectory -Recurse -Force
}

# ---------------------------------------------------------------- TWAIN data source

# 64-bit TWAINDSM.dll searches C:\Windows\twain_64; the 32-bit DSM and the legacy
# twain_32.dll search C:\Windows\twain_32. Each gets the matching build.
$twainTargets = @(
    @{ Dir = Join-Path $env:SystemRoot 'twain_64\RemoteScanner'; Source = Join-Path $Source 'x64\RemoteScanner.ds' },
    @{ Dir = Join-Path $env:SystemRoot 'twain_32\RemoteScanner'; Source = Join-Path $Source 'x86\RemoteScanner.ds' }
)

Write-Step "Installing the virtual TWAIN data source"
foreach ($target in $twainTargets) {
    if (-not (Test-Path $target.Source)) {
        Write-Warn "$($target.Source) is missing; applications of that bitness will not see the scanner."
        continue
    }

    New-Item -ItemType Directory -Force -Path $target.Dir | Out-Null
    Copy-Item -Path $target.Source -Destination (Join-Path $target.Dir 'RemoteScanner.ds') -Force
    Write-Host "    $($target.Dir)\RemoteScanner.ds"

    # The per-vendor sub-folder is the only correct location, and an earlier version of this
    # installer also dropped a copy directly in twain_32\ / twain_64\ on the theory that the
    # legacy manager Windows ships would read the top level. It does not - that twain_32.dll
    # only re-presents WIA devices and never reads .ds files at all - while TWAINDSM.dll scans
    # BOTH levels and therefore lists the scanner twice. Measured against the real manager:
    # sub-folder alone gives one entry, sub-folder plus top-level gives two.
    #
    # So the stale copy is removed rather than refreshed, which also repairs a machine that
    # was installed with the earlier version.
    $stale = Join-Path (Split-Path -Parent $target.Dir) 'RemoteScanner.ds'
    if (Test-Path $stale) {
        try {
            Remove-Item -Path $stale -Force
            Write-Host "    removed the duplicate at $stale"
        } catch {
            Write-Warn "$stale is in use and could not be removed; close all scanning applications and re-run, or the scanner will be listed twice."
        }
    }
}

# ---------------------------------------------------------------------- service

Write-Step "Registering the service"
$serviceExe = Join-Path $InstallDirectory 'RemoteScanner.Service.exe'
if (-not (Test-Path $serviceExe)) { throw "RemoteScanner.Service.exe is missing from the payload." }

# LocalSystem is required: WTSQueryUserToken and CreateProcessAsUser need SeTcbPrivilege to
# launch the per-session agent inside a user's RDP session.
& sc.exe create $serviceName binPath= "`"$serviceExe`"" start= auto obj= LocalSystem `
    DisplayName= "Remote Scanner Redirection" | Out-Null
& sc.exe description $serviceName `
    "Makes scanners attached to the client PC available to applications in an RDP session." | Out-Null

# Restart on failure rather than leaving sessions without a scanner until someone notices.
& sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null

Write-Step "Registering the event log source"
if (-not [System.Diagnostics.EventLog]::SourceExists('RemoteScanner')) {
    New-EventLog -LogName Application -Source 'RemoteScanner'
}

Write-Step "Setting the default log level"
New-Item -Path 'HKLM:\SOFTWARE\RemoteScanner' -Force | Out-Null
Set-ItemProperty -Path 'HKLM:\SOFTWARE\RemoteScanner' -Name 'LogLevel' -Value 'Information' -Type String

# ProgramData holds logs written by every session agent, so all users need to write there.
Write-Step "Preparing %ProgramData%\RemoteScanner"
$dataDir = Join-Path $env:ProgramData 'RemoteScanner'
New-Item -ItemType Directory -Force -Path (Join-Path $dataDir 'logs') | Out-Null
& icacls $dataDir /grant '*S-1-5-32-545:(OI)(CI)M' /T /Q | Out-Null   # BUILTIN\Users: modify

if (-not $NoStart) {
    Write-Step "Starting the service"
    Start-Service -Name $serviceName
    (Get-Service -Name $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
}

Write-Host ""
Write-Host "Server component installed." -ForegroundColor Green
Write-Host ""
Write-Host "Applications in an RDP session will now list a scanner named"
Write-Host "  'Remote Scanner (<client PC name>)'"
Write-Host "once a client connects with the RemoteScanner add-in registered."
Write-Host ""
Write-Host "Logs: $dataDir\logs"
