<#
.SYNOPSIS
    Removes the RemoteScanner client.

.DESCRIPTION
    Unregisters the RDP add-in, stops the agent, and deletes the installed files. Runs as the
    ordinary user, matching Install-Client.ps1 — the add-in registration is per-user and an
    elevated run would look in the wrong hive.

.PARAMETER KeepLogs
    Leave %ProgramData%\RemoteScanner\logs in place for troubleshooting.
#>
[CmdletBinding()]
param(
    [string] $InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\RemoteScanner'),
    [switch] $KeepLogs
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-Step($message) { Write-Host "==> $message" -ForegroundColor Cyan }

Write-Step "Stopping the agent"
Get-Process -Name 'RemoteScanner.Client', 'RemoteScanner.Agent', 'RemoteScanner.ScanHost' `
    -ErrorAction SilentlyContinue | ForEach-Object {
        $_ | Stop-Process -Force
        $_.WaitForExit(5000) | Out-Null
    }

Write-Step "Unregistering the RDP add-in"
$addInKey = 'HKCU:\Software\Microsoft\Terminal Server Client\Default\AddIns\RemoteScanner'
if (Test-Path $addInKey) { Remove-Item -Path $addInKey -Recurse -Force }

Write-Step "Removing the firewall rule"
Get-NetFirewallRule -DisplayName 'Remote Scanner (direct connection)' -ErrorAction SilentlyContinue |
    Remove-NetFirewallRule -ErrorAction SilentlyContinue

Write-Step "Removing the startup entry"
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
Remove-ItemProperty -Path $runKey -Name 'RemoteScanner' -ErrorAction SilentlyContinue

Write-Step "Removing shortcuts"
foreach ($linkPath in @(
    (Join-Path ([Environment]::GetFolderPath('Programs')) 'Remote Scanner.lnk'),
    (Join-Path ([Environment]::GetFolderPath('Desktop'))  'Remote Scanner.lnk'))) {
    if (Test-Path $linkPath) { Remove-Item $linkPath -Force -ErrorAction SilentlyContinue }
}

Write-Step "Removing the shared secret"
# This is key material; it must not be left behind after an uninstall.
if (Test-Path 'HKCU:\Software\RemoteScanner') {
    Remove-Item -Path 'HKCU:\Software\RemoteScanner' -Recurse -Force
}

Write-Step "Deleting installed files"
if (Test-Path $InstallDirectory) {
    # mstsc.exe keeps the plugin DLL mapped for the life of the connection.
    try {
        Remove-Item -Path $InstallDirectory -Recurse -Force
    } catch {
        Write-Host "    ! Some files are still in use. Close all Remote Desktop windows and re-run." -ForegroundColor Yellow
    }
}

if (-not $KeepLogs) {
    Write-Step "Removing logs and spooled scan data"
    $dataDir = Join-Path $env:ProgramData 'RemoteScanner'
    if (Test-Path $dataDir) { Remove-Item -Path $dataDir -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Host ""
Write-Host "Client removed." -ForegroundColor Green
