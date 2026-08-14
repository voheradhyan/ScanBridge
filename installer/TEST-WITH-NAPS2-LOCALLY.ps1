<#
.SYNOPSIS
    Reproduces a NAPS2 scan on THIS PC, with no server and no RDP.

.DESCRIPTION
    The fault being chased lives between NAPS2 and the data source, not in the transport, so
    the server and the RDP hop are noise: they add a copy step and a sign-out to every attempt
    and cannot influence the outcome. This installs the data source on this machine, starts the
    session agent in loopback mode, and leaves NAPS2 talking to the local scanner through the
    real driver.

    Reverses itself with -Remove.

    Requires administrator: C:\Windows\twain_64 is the only place a TWAIN manager looks.

.EXAMPLE
    .\TEST-WITH-NAPS2-LOCALLY.ps1
    .\TEST-WITH-NAPS2-LOCALLY.ps1 -Remove
#>
[CmdletBinding()]
param(
    [switch] $Remove,
    [string] $Source
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Source) {
    $Source = if (Test-Path (Join-Path $scriptRoot 'x64\RemoteScanner.ds')) { $scriptRoot }
              else { Join-Path (Split-Path -Parent $scriptRoot) 'build\server' }
}

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this from an ADMINISTRATOR PowerShell window - it writes to C:\Windows\twain_64."
}

$targets = @(
    @{ Dir = Join-Path $env:SystemRoot 'twain_64\RemoteScanner'; File = Join-Path $Source 'x64\RemoteScanner.ds' },
    @{ Dir = Join-Path $env:SystemRoot 'twain_32\RemoteScanner'; File = Join-Path $Source 'x86\RemoteScanner.ds' }
)

# The session agent shares an executable with the server service, so both of these match on the
# command line. Matching on the image name would stop a running service — on a machine that is
# both a client and a server, that is somebody's live scanning session.
function Get-SessionAgents {
    Get-CimInstance Win32_Process -Filter "Name='RemoteScanner.Service.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -like '*--session-agent*' }
}

function Stop-SessionAgents {
    foreach ($agent in @(Get-SessionAgents)) {
        try { Stop-Process -Id $agent.ProcessId -Force -ErrorAction Stop } catch {}
    }
}

if ($Remove) {
    Stop-SessionAgents
    foreach ($t in $targets) {
        if (Test-Path $t.Dir) {
            try { Remove-Item -Recurse -Force $t.Dir; Write-Host "removed $($t.Dir)" }
            catch { Write-Host "could not remove $($t.Dir) - close NAPS2 and re-run" -ForegroundColor Yellow }
        }
    }
    Write-Host "`nLocal test setup removed." -ForegroundColor Green
    return
}

# The tray app owns the scanner; without it the loopback agent has nothing to talk to.
if (-not (Get-Process -Name 'RemoteScanner.Client' -ErrorAction SilentlyContinue)) {
    throw "The Remote Scanner tray app is not running. Start it, then run this again."
}

Write-Host "==> Installing the data source locally"
foreach ($t in $targets) {
    if (-not (Test-Path $t.File)) { Write-Host "    $($t.File) missing, skipped" -ForegroundColor Yellow; continue }
    New-Item -ItemType Directory -Force -Path $t.Dir | Out-Null
    Copy-Item $t.File (Join-Path $t.Dir 'RemoteScanner.ds') -Force
    Write-Host "    $($t.Dir)\RemoteScanner.ds"
}

Write-Host "==> Starting the session agent in loopback mode"
Stop-SessionAgents
Start-Process -FilePath (Join-Path $Source 'RemoteScanner.Service.exe') `
              -ArgumentList '--session-agent', '--loopback' -WindowStyle Minimized
Start-Sleep -Seconds 3
if (-not (Get-SessionAgents)) {
    throw "The session agent did not start. See %ProgramData%\RemoteScanner\logs\sessionagent-*.log"
}
Write-Host "    running"

Write-Host @"

Ready. Now, in NAPS2 on this PC:

  1. Profiles -> Add, driver TWAIN, choose device
  2. pick "Remote Scanner (this PC's name)"
  3. Scan

Then read the newest log, which now records what NAPS2 asked for:

  Get-ChildItem `$env:ProgramData\RemoteScanner\logs\twainds-*.log |
    Sort-Object LastWriteTime | Select-Object -Last 1 | Get-Content

Undo with:  .\TEST-WITH-NAPS2-LOCALLY.ps1 -Remove
"@ -ForegroundColor Cyan
