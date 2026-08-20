<#
.SYNOPSIS
    Removes an installation of the product from before it was renamed to ScanBridge.

.DESCRIPTION
    ScanBridge was called RemoteScanner until 14 August 2026. The rename changed every name
    the two halves use to find each other — the service, both named pipes, the RDP channel,
    the registry keys and the TWAIN folder — so an old installation and a new one share
    nothing and cannot interfere. They can, however, both appear in a scanner list, and an
    old service left running is a puzzle for whoever finds it next.

    This removes the old one. It does not touch ScanBridge.

    Nothing is deleted unless -Remove is passed. Run it once to see the list, then again to
    act on it.

    Some of what it finds is machine-wide (the service, the system TWAIN folders, HKLM) and
    needs administrator rights; the rest is per-user and must NOT be run elevated, or it will
    look in the administrator's profile and report a clean machine that is not clean. Run it
    normally first, then elevated if it says machine-wide items remain.

.PARAMETER Remove
    Actually delete. Without this, the script only reports.

.EXAMPLE
    .\REMOVE-OLD-REMOTESCANNER.ps1
    .\REMOVE-OLD-REMOTESCANNER.ps1 -Remove
#>
[CmdletBinding()]
param([switch] $Remove)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$elevated = ([Security.Principal.WindowsPrincipal] `
    [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)

$found = @()
$blocked = @()

function Note($what, $where, $needsAdmin = $false) {
    $script:found += [pscustomobject]@{ What = $what; Where = $where; NeedsAdmin = $needsAdmin }
}

Write-Host ""
Write-Host "Looking for an installation of the old RemoteScanner build" -ForegroundColor Cyan
Write-Host ""

# ---------------------------------------------------------------- processes and service

$processes = @(Get-Process -ErrorAction SilentlyContinue |
    Where-Object { $_.ProcessName -like 'RemoteScanner*' })
foreach ($p in $processes) { Note "running process (pid $($p.Id))" $p.ProcessName }

$service = Get-Service -Name 'RemoteScanner' -ErrorAction SilentlyContinue
if ($service) { Note "Windows service" "RemoteScanner ($($service.Status))" $true }

# ---------------------------------------------------------------- files

$directories = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\RemoteScanner'),
    (Join-Path $env:ProgramFiles 'RemoteScanner'),
    (Join-Path $env:LOCALAPPDATA 'RemoteScanner'),
    (Join-Path $env:ProgramData  'RemoteScanner')
)
foreach ($d in $directories) {
    if (Test-Path $d) {
        $machineWide = $d -like "$env:ProgramFiles*" -or $d -like "$env:ProgramData*"
        Note "folder" $d $machineWide
    }
}

# The TWAIN folders are the ones that matter most: a data source left here keeps appearing in
# every scanning application's device list, whether or not anything behind it still works.
foreach ($twain in @('twain_32', 'twain_64')) {
    $dir = Join-Path $env:SystemRoot "$twain\RemoteScanner"
    if (Test-Path $dir) { Note "TWAIN data source" $dir $true }

    $stray = Join-Path $env:SystemRoot "$twain\RemoteScanner.ds"
    if (Test-Path $stray) { Note "stray data source" $stray $true }
}

# ---------------------------------------------------------------- registry

$userKeys = @(
    'HKCU:\Software\RemoteScanner',
    'HKCU:\Software\Microsoft\Terminal Server Client\Default\AddIns\RemoteScanner'
)
foreach ($k in $userKeys) { if (Test-Path $k) { Note "registry key" $k } }

if (Test-Path 'HKLM:\SOFTWARE\RemoteScanner') { Note "registry key" 'HKLM:\SOFTWARE\RemoteScanner' $true }

$run = Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' `
                        -Name 'RemoteScanner' -ErrorAction SilentlyContinue
if ($run) { Note "starts with Windows" 'HKCU:\...\CurrentVersion\Run\RemoteScanner' }

# ---------------------------------------------------------------- report

if ($found.Count -eq 0) {
    Write-Host "  Nothing found. This machine has no old installation." -ForegroundColor Green
    Write-Host ""
    return
}

$found | Format-Table What, Where, NeedsAdmin -AutoSize | Out-String | Write-Host

if (-not $Remove) {
    Write-Host "  Nothing was deleted. Run it again with -Remove to act on the list above." -ForegroundColor Yellow
    if (($found | Where-Object NeedsAdmin) -and -not $elevated) {
        Write-Host "  Some of it needs an elevated window; the per-user items must NOT be." -ForegroundColor Yellow
    }
    Write-Host ""
    return
}

# ---------------------------------------------------------------- removal

Write-Host "Removing" -ForegroundColor Cyan

# The service goes first, before anything is killed. Killing its process instead just hands
# the job to the SCM, which restarts it - and the service spawns a session agent per RDP
# session, so those come back too, and while they are alive they keep the .ds files mapped
# and the TWAIN folders refuse to delete. Measured on a real host: service plus three
# session agents, all of it reappearing.
if ($service) {
    if (-not $elevated) { $blocked += "service RemoteScanner: needs an elevated window" }
    else {
        try {
            # Re-queried rather than reusing the object captured during the scan, whose Status
            # is a snapshot from before any of this ran.
            $live = Get-Service -Name 'RemoteScanner' -ErrorAction SilentlyContinue
            if ($live -and $live.Status -ne 'Stopped') {
                Stop-Service -Name 'RemoteScanner' -Force -ErrorAction SilentlyContinue
                try { $live.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30)) } catch { }
            }

            # Outside the stop, deliberately. A service that will not stop cleanly must still
            # be deregistered, or the next reboot brings the whole thing back.
            & sc.exe delete RemoteScanner | Out-Null
            Write-Host "  removed the service"
        } catch { $blocked += "service RemoteScanner: $($_.Exception.Message)" }
    }
}

# Now whatever is still running: session agents the service will no longer respawn, and a
# service process that ignored the stop. Re-enumerated, because the list from the scan is
# minutes old by this point and the service may have replaced its children since.
foreach ($p in @(Get-Process -ErrorAction SilentlyContinue |
                 Where-Object { $_.ProcessName -like 'RemoteScanner*' })) {
    try { Stop-Process -Id $p.Id -Force -ErrorAction Stop; Write-Host "  stopped $($p.ProcessName)" }
    catch { $blocked += "process $($p.ProcessName): $($_.Exception.Message)" }
}

foreach ($item in $found | Where-Object { $_.What -like '*folder*' -or $_.What -like '*data source*' }) {
    if ($item.NeedsAdmin -and -not $elevated) { $blocked += "$($item.Where): needs an elevated window"; continue }
    try {
        Remove-Item $item.Where -Recurse -Force -ErrorAction Stop
        Write-Host "  removed $($item.Where)"
    } catch {
        # Almost always a scanning application still holding the data source mapped.
        $blocked += "$($item.Where): $($_.Exception.Message)"
    }
}

foreach ($item in $found | Where-Object { $_.What -eq 'registry key' }) {
    if ($item.NeedsAdmin -and -not $elevated) { $blocked += "$($item.Where): needs an elevated window"; continue }
    try { Remove-Item $item.Where -Recurse -Force -ErrorAction Stop; Write-Host "  removed $($item.Where)" }
    catch { $blocked += "$($item.Where): $($_.Exception.Message)" }
}

if ($run) {
    try {
        Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' `
                            -Name 'RemoteScanner' -ErrorAction Stop
        Write-Host "  removed the auto-start entry"
    } catch { $blocked += "auto-start entry: $($_.Exception.Message)" }
}

Write-Host ""
if ($blocked.Count -eq 0) {
    Write-Host "  Done. Sign out and back in so applications stop listing the old scanner." -ForegroundColor Green
} else {
    Write-Host "  Left behind:" -ForegroundColor Yellow
    $blocked | ForEach-Object { Write-Host "    $_" -ForegroundColor Yellow }
    Write-Host ""
    Write-Host "  A data source that will not delete is usually still mapped by a scanning" -ForegroundColor Yellow
    Write-Host "  application. Close every one of them and run this again." -ForegroundColor Yellow
}
Write-Host ""
