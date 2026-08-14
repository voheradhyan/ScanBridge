<#
.SYNOPSIS
    Bundles every ScanBridge log on this machine into one zip on the Desktop.

.DESCRIPTION
    Each file is copied through a sharing-tolerant read before being zipped, and the running
    processes are recorded alongside.

    Compress-Archive on its own quietly skips any file another process holds open, which meant
    the log being written at the moment of the fault — the only one that could explain it — was
    the one guaranteed to be missing, with nothing in the bundle to say it had been left out.
    A silently incomplete bundle is worse than none: it sends the reader looking for a cause in
    the wrong place.

    This lives in its own script rather than inline in the .bat because the batch escaping for
    a PowerShell one-liner this size is unreadable and got the job wrong.
#>
[CmdletBinding()]
param(
    [string] $Destination = [Environment]::GetFolderPath('Desktop')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Two locations, and both are needed to understand a fault.
#
# Anything running as the signed-in user — the tray agent, the session agent, the data source
# inside the scanning application, the plugin inside mstsc.exe — logs into that user's own
# profile, so that one user on a Session Host cannot read another's. Only the machine-wide
# service logs to ProgramData. Collecting one without the other leaves half the story.
$sources = @(
    Join-Path $env:LOCALAPPDATA 'ScanBridge\logs',
    Join-Path $env:ProgramData 'ScanBridge\logs'
) | Where-Object { Test-Path $_ }

if ($sources.Count -eq 0) {
    Write-Host "   No logs found. Has anything been run yet?" -ForegroundColor Yellow
    exit 1
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$stage = Join-Path $env:TEMP "rs-logs-$stamp"
New-Item -ItemType Directory -Force -Path $stage | Out-Null

$copied = 0
$failed = @()

foreach ($file in ($sources | ForEach-Object { Get-ChildItem $_ -File })) {
    try {
        # FileShare.ReadWrite: the interesting file is usually one a live process is appending to.
        $in = [IO.File]::Open($file.FullName, 'Open', 'Read', 'ReadWrite')
        try {
            $out = [IO.File]::Create((Join-Path $stage $file.Name))
            try { $in.CopyTo($out) } finally { $out.Close() }
        } finally {
            $in.Close()
        }
        $copied++
    } catch {
        $failed += $file.Name
    }
}

# Which processes were alive, and where they were running from. "Two agents were running" and
# "it was an old copy from another folder" are both invisible in the logs themselves.
$processes = Get-Process |
    Where-Object { $_.ProcessName -like 'ScanBridge*' -or $_.ProcessName -eq 'mstsc' } |
    Select-Object Id, ProcessName, SessionId, StartTime, Path |
    Format-Table -AutoSize | Out-String

$info = @(
    "machine  : $env:COMPUTERNAME"
    "user     : $env:USERNAME"
    "when     : $(Get-Date)"
    "os       : $([Environment]::OSVersion.VersionString)"
    ""
    "running processes:"
    $processes
    ""
    "log files collected : $copied"
    "unreadable log files: $(if ($failed) { $failed -join ', ' } else { 'none' })"
)
$info | Set-Content (Join-Path $stage '_collection-info.txt') -Encoding utf8

$zip = Join-Path $Destination "ScanBridge-logs-$env:COMPUTERNAME-$stamp.zip"
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force
Remove-Item $stage -Recurse -Force

Write-Host "   Collected $copied log file(s)." -ForegroundColor Green
if ($failed) {
    Write-Host "   Could NOT read: $($failed -join ', ')" -ForegroundColor Yellow
}
Write-Host "   Saved to: $zip" -ForegroundColor Green
Write-Host ("   Size: {0:N0} KB" -f ((Get-Item $zip).Length / 1KB))
