<#
.SYNOPSIS
    Removes the ScanBridge server component.

.DESCRIPTION
    Stops and deletes the service, removes the virtual TWAIN data source from both TWAIN
    search paths, and deletes the installed files.

    Removing the .ds cleanly matters: a stale data source left in twain_32 or twain_64 shows
    up in every scanning application's device list and fails when selected, which looks like
    a broken scanner rather than a leftover file.

    Requires administrator.
#>
[CmdletBinding()]
param(
    [string] $InstallDirectory = (Join-Path $env:ProgramFiles 'ScanBridge'),
    [switch] $KeepLogs
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-Step($message) { Write-Host "==> $message" -ForegroundColor Cyan }

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "This script must be run as administrator."
}

$serviceName = 'ScanBridge'

Write-Step "Stopping the service"
$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $serviceName -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    & sc.exe delete $serviceName | Out-Null
    Start-Sleep -Seconds 2
}

Write-Step "Stopping session agents"
Get-Process -Name 'ScanBridge.SessionAgent' -ErrorAction SilentlyContinue | ForEach-Object {
    $_ | Stop-Process -Force
    $_.WaitForExit(5000) | Out-Null
}

Write-Step "Removing the virtual TWAIN data source"

# Both locations the installer writes: the per-vendor sub-folder that a TWAIN 2.x manager
# reads, and the top-level copy that the legacy Windows manager reads. Leaving either behind
# would keep a dead scanner in every application's device list.
foreach ($path in @(
    (Join-Path $env:SystemRoot 'twain_64\ScanBridge'),
    (Join-Path $env:SystemRoot 'twain_32\ScanBridge'),
    (Join-Path $env:SystemRoot 'twain_64\ScanBridge.ds'),
    (Join-Path $env:SystemRoot 'twain_32\ScanBridge.ds'))) {

    if (-not (Test-Path $path)) { continue }

    try {
        Remove-Item -Path $path -Recurse -Force
        Write-Host "    removed $path"
    } catch {
        # The .ds stays mapped while any application that enumerated it is still running.
        Write-Host "    ! $path is in use. Close all scanning applications and re-run." -ForegroundColor Yellow
    }
}

Write-Step "Removing per-session secrets"
# Each session agent stored a DPAPI-protected key under its own user's hive; the ones under
# HKLM and any orphaned machine state go here.
Remove-Item -Path 'HKLM:\SOFTWARE\ScanBridge' -Recurse -Force -ErrorAction SilentlyContinue

Write-Step "Removing the event log source"
if ([System.Diagnostics.EventLog]::SourceExists('ScanBridge')) {
    Remove-EventLog -Source 'ScanBridge'
}

Write-Step "Deleting installed files"
if (Test-Path $InstallDirectory) {
    try {
        Remove-Item -Path $InstallDirectory -Recurse -Force
    } catch {
        Write-Host "    ! Some files are still in use; reboot and re-run to finish." -ForegroundColor Yellow
    }
}

if (-not $KeepLogs) {
    Write-Step "Removing logs and spooled scan data"
    $dataDir = Join-Path $env:ProgramData 'ScanBridge'
    if (Test-Path $dataDir) { Remove-Item -Path $dataDir -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Host ""
Write-Host "Server component removed." -ForegroundColor Green
Write-Host "Users must sign out and back in for applications to stop listing the remote scanner."
