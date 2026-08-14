<#
.SYNOPSIS
    Installs the ScanBridge client on the PC the scanner is plugged into.

.DESCRIPTION
    Copies the client payload, registers the RDP add-in with the Remote Desktop client, and
    starts the tray agent.

    Deliberately does NOT require administrator. Everything the client needs is per-user:
      * the add-in registration lives under HKCU, which is where mstsc.exe reads it from
      * the agent talks to the user's own scanners and serves a pipe ACLed to that user
    Running it elevated would put the add-in under the wrong user's hive and it would never
    load.

.PARAMETER Source
    Directory holding the built payload. Defaults to ..\build\client next to this script.

.PARAMETER InstallDirectory
    Where to install. Defaults to %LOCALAPPDATA%\Programs\ScanBridge.

.EXAMPLE
    .\Install-Client.ps1
#>
[CmdletBinding()]
param(
    [string] $Source,
    [string] $InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\ScanBridge'),
    [switch] $NoStart
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path

# Runs both from inside the copied payload folder (what users do) and from installer\
# during development. The deployed case is checked first.
if (-not $Source) {
    if (Test-Path (Join-Path $scriptRoot 'ScanBridge.Client.exe')) {
        $Source = $scriptRoot
    } else {
        $Source = Join-Path (Split-Path -Parent $scriptRoot) 'build\client'
    }
}

function Write-Step($message) { Write-Host "==> $message" -ForegroundColor Cyan }
function Write-Warn($message) { Write-Host "    ! $message" -ForegroundColor Yellow }

# Reads an optional registry value. Under Set-StrictMode, touching a property that does not
# exist is a terminating error, so the property has to be probed before it is read - the
# usual "(Get-ItemProperty ...).Name" idiom throws whenever the value is absent, which for
# optional policy values is the normal case.
function Get-RegValue($path, $name) {
    if (-not (Test-Path $path)) { return $null }
    $props = Get-ItemProperty -Path $path -ErrorAction SilentlyContinue
    if (-not $props) { return $null }
    if ($props.PSObject.Properties.Name -notcontains $name) { return $null }
    return $props.$name
}

# Elevation would register the add-in in the administrator's hive, not the user's.
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Warn "Running elevated. The RDP add-in will be registered for '$($identity.Name)'."
    Write-Warn "If that is not the account you use Remote Desktop with, run this without elevation."
}

if (-not (Test-Path $Source)) {
    throw "Payload not found at '$Source'. Run installer\Build-All.ps1 first."
}

$pluginPath = Join-Path $Source 'x64\ScanBridge.DvcPlugin.dll'
if (-not (Test-Path $pluginPath)) {
    throw "ScanBridge.DvcPlugin.dll (x64) is missing from '$Source'. The build is incomplete."
}

Write-Step "Stopping any running agent"
Get-Process -Name 'ScanBridge.Client', 'ScanBridge.Agent', 'ScanBridge.ScanHost' `
    -ErrorAction SilentlyContinue | ForEach-Object {
        $_ | Stop-Process -Force
        $_.WaitForExit(5000) | Out-Null
    }

# mstsc.exe maps ScanBridge.DvcPlugin.dll for the life of the connection and will not
# release it, so an upgrade cannot overwrite the file while a Remote Desktop window is open.
# Killing mstsc would drop the user's session without warning, so we ask instead and wait.
$mstsc = @(Get-Process mstsc -ErrorAction SilentlyContinue)
if ($mstsc.Count -gt 0) {
    Write-Host ""
    Write-Warn "Remote Desktop is open ($($mstsc.Count) window(s))."
    Write-Warn "It is holding a file that has to be replaced."
    Write-Host ""
    Write-Host "    Please CLOSE all Remote Desktop windows now." -ForegroundColor White
    Write-Host "    This script will continue by itself once they are closed."
    Write-Host "    (Waiting up to 3 minutes. Press Ctrl+C to cancel.)"
    Write-Host ""

    $deadline = (Get-Date).AddMinutes(3)
    while (@(Get-Process mstsc -ErrorAction SilentlyContinue).Count -gt 0) {
        if ((Get-Date) -gt $deadline) {
            throw "Remote Desktop is still open. Close every Remote Desktop window, then run this installer again."
        }
        Start-Sleep -Seconds 2
        Write-Host "." -NoNewline
    }

    Write-Host ""
    Write-Host "    Remote Desktop closed. Continuing."
    # Windows needs a moment to actually unmap the DLL after the process exits.
    Start-Sleep -Seconds 2
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
    try {
        Copy-Item -Path (Join-Path $Source '*') -Destination $InstallDirectory -Recurse -Force -ErrorAction Stop
    } catch [System.IO.IOException] {
        # Almost always a file still mapped by a process we did not catch above. Naming the
        # likely culprits is far more use than the raw "used by another process" error.
        $holders = @(Get-Process mstsc, ScanBridge.Client, ScanBridge.Agent, `
                                 ScanBridge.ScanHost -ErrorAction SilentlyContinue)
        Write-Host ""
        Write-Warn "A file is still in use and could not be replaced."
        if ($holders.Count -gt 0) {
            Write-Warn "These programs are still running and are probably holding it:"
            foreach ($p in $holders) { Write-Host "        $($p.ProcessName)  (id $($p.Id))" }
        }
        Write-Host ""
        Write-Host "    Close all Remote Desktop windows, then run this installer again." -ForegroundColor White
        Write-Host "    If it still fails, restart the PC and run it once more."
        throw "Install stopped: a file is in use."
    }
}

# mstsc.exe is 64-bit on 64-bit Windows, so that is the plugin it will load.
$installedPlugin = Join-Path $InstallDirectory 'x64\ScanBridge.DvcPlugin.dll'

Write-Step "Registering the RDP add-in"
$addInKey = 'HKCU:\Software\Microsoft\Terminal Server Client\Default\AddIns\ScanBridge'
New-Item -Path $addInKey -Force | Out-Null
Set-ItemProperty -Path $addInKey -Name 'Name' -Value $installedPlugin -Type String
Write-Host "    $installedPlugin"

# This is the single most common cause of "installed but nothing happens".
Write-Step "Checking group policy"
$blocked = $false
foreach ($hive in @('HKLM:', 'HKCU:')) {
    $policy = "$hive\Software\Policies\Microsoft\Windows NT\Terminal Services\Client"
    if (-not (Test-Path $policy)) { continue }

    $disable = Get-RegValue $policy 'DisableAddIns'
    if ($disable) {
        Write-Warn "Policy 'DisableAddIns' is set at $policy. Remote Desktop will not load ANY add-in."
        $blocked = $true
    }

    $allowed = Get-RegValue $policy 'AllowedAddIns'
    if ($allowed -and $allowed -notmatch 'ScanBridge') {
        Write-Warn "Policy 'AllowedAddIns' at $policy does not list ScanBridge."
        $blocked = $true
    }
}
if (-not $blocked) { Write-Host "    No blocking policy found." }

# The Remote Desktop virtual channel is the primary transport and needs no rule at all. This
# is for the case where it opens but carries nothing — a real and, until now, undiagnosable
# failure that left scanning completely dead with both ends reporting themselves healthy. The
# server then connects to this PC directly instead.
#
# Scoped to the local subnet, TCP only, one port. The listener refuses anything that cannot
# prove it holds this user's shared secret, and everything after that handshake is encrypted.
Write-Step "Allowing direct connections from Remote Desktop servers"
$ruleName = 'ScanBridge (direct connection)'
$lanPort = 47214
$agentExe = Join-Path $InstallDirectory 'ScanBridge.Client.exe'

# A firewall rule needs administrator rights, and the rest of this installer must NOT have
# them — everything else it writes belongs to the current user's account. So exactly this one
# step is elevated, on its own, and the install carries on regardless of the answer.
#
# netsh rather than New-NetFirewallRule: it is one command that can be handed to an elevated
# cmd.exe without marshalling a script block across the boundary.
if (Test-Path $agentExe) {
    $netsh = "netsh advfirewall firewall delete rule name=`"$ruleName`" >nul 2>&1 & " +
             "netsh advfirewall firewall add rule name=`"$ruleName`" dir=in action=allow " +
             "protocol=TCP localport=$lanPort remoteip=LocalSubnet program=`"$agentExe`" enable=yes"

    Write-Host "    Windows will ask for administrator permission for this one step."
    Write-Host "    It is safe to say No - scanning still works whenever Remote Desktop"
    Write-Host "    can carry the scanner itself, which is the normal case."

    try {
        $elevated = Start-Process -FilePath 'cmd.exe' -ArgumentList '/c', $netsh `
            -Verb RunAs -WindowStyle Hidden -Wait -PassThru -ErrorAction Stop

        if ($elevated.ExitCode -eq 0) {
            Write-Host "    Allowed: TCP $lanPort, local subnet only." -ForegroundColor Green
        } else {
            Write-Warn "The firewall command reported exit code $($elevated.ExitCode)."
            Write-Warn "Run ALLOW-DIRECT-CONNECTION.bat as administrator to try again."
        }
    }
    catch {
        # Almost always the user declining the prompt. Not a failure of the install.
        Write-Warn "Skipped - permission was not granted."
        Write-Warn "If scanning over Remote Desktop does not work, run"
        Write-Warn "ALLOW-DIRECT-CONNECTION.bat as administrator and try again."
    }
}

Write-Step "Configuring startup"
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
Set-ItemProperty -Path $runKey -Name 'ScanBridge' -Value "`"$agentExe`"" -Type String

# The program lives under AppData, which Explorer hides by default. Without shortcuts there
# is no way for an ordinary user to start it again after closing it, so both a Start Menu
# entry and a Desktop icon are created.
Write-Step "Creating shortcuts"
$shell = New-Object -ComObject WScript.Shell

$shortcutTargets = @(
    (Join-Path ([Environment]::GetFolderPath('Programs')) 'ScanBridge.lnk'),
    (Join-Path ([Environment]::GetFolderPath('Desktop'))  'ScanBridge.lnk')
)

foreach ($linkPath in $shortcutTargets) {
    try {
        $link = $shell.CreateShortcut($linkPath)
        $link.TargetPath = $agentExe
        $link.WorkingDirectory = $InstallDirectory
        $link.Description = 'Use this PC''s scanner inside a Remote Desktop session'
        $link.IconLocation = "$agentExe,0"
        $link.Save()
        Write-Host "    $linkPath"
    } catch {
        Write-Warn "Could not create $linkPath - $($_.Exception.Message)"
    }
}

[System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell) | Out-Null

if (-not $NoStart) {
    Write-Step "Starting the agent"
    Start-Process -FilePath $agentExe -WorkingDirectory $InstallDirectory
}

Write-Host ""
Write-Host "Client installed." -ForegroundColor Green
Write-Host ""
Write-Host "Important: scanner redirection only works through mstsc.exe."
Write-Host "The Microsoft Store 'Windows App' cannot load RDP add-ins and will never work."
Write-Host ""
Write-Host "Next: run installer\Install-Server.ps1 on the Windows Server, as administrator."
