<#
.SYNOPSIS
    Checks that the ScanBridge server component is installed and able to work.

.DESCRIPTION
    Run this on the Windows Server after Install-Server.ps1. It reports on each thing that
    has to be true for a remote application to see the redirected scanner, and says plainly
    what to do about anything that is wrong.

    Safe to run at any time. It changes nothing.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Continue'

$script:Problems = @()

function Write-Head($text) {
    Write-Host ""
    Write-Host "  $text" -ForegroundColor Cyan
    Write-Host "  $('-' * 58)" -ForegroundColor DarkGray
}

function Write-Ok($label, $detail) {
    Write-Host "  [ OK ]   " -ForegroundColor Green -NoNewline
    Write-Host "$label" -NoNewline
    if ($detail) { Write-Host "  $detail" -ForegroundColor DarkGray } else { Write-Host "" }
}

function Write-Bad($label, $detail, $fix) {
    Write-Host "  [FAIL]   " -ForegroundColor Red -NoNewline
    Write-Host "$label" -NoNewline
    if ($detail) { Write-Host "  $detail" -ForegroundColor DarkGray } else { Write-Host "" }
    $script:Problems += "$label - $fix"
}

function Write-Note($label, $detail, $advice) {
    Write-Host "  [WARN]   " -ForegroundColor Yellow -NoNewline
    Write-Host "$label" -NoNewline
    if ($detail) { Write-Host "  $detail" -ForegroundColor DarkGray } else { Write-Host "" }
    if ($advice) { Write-Host "           $advice" -ForegroundColor DarkYellow }
}

Write-Host ""
Write-Host "  ==========================================================" -ForegroundColor White
Write-Host "    REMOTE SCANNER - SERVER CHECK" -ForegroundColor White
Write-Host "  ==========================================================" -ForegroundColor White

# ------------------------------------------------------------------ the machine

Write-Head "This server"

$os = Get-CimInstance Win32_OperatingSystem
Write-Ok "Windows" "$($os.Caption) build $($os.BuildNumber)"

# Server Core has no Desktop Experience, so no scanning application can run here anyway.
$isCore = -not (Test-Path "$env:SystemRoot\explorer.exe")
if ($isCore) {
    Write-Bad "Desktop Experience" "not installed (Server Core)" `
        "Scanning applications cannot run on Server Core. Use a Desktop Experience install."
} else {
    Write-Ok "Desktop Experience" "present"
}

# ---------------------------------------------------------------- RDP host

Write-Head "Remote Desktop"

$deny = (Get-ItemProperty 'HKLM:\System\CurrentControlSet\Control\Terminal Server' `
    -Name fDenyTSConnections -ErrorAction SilentlyContinue).fDenyTSConnections
if ($deny -eq 0) {
    Write-Ok "Remote Desktop" "enabled"
} else {
    Write-Bad "Remote Desktop" "disabled" `
        "Enable Remote Desktop in System Properties, or nobody can connect at all."
}

$term = Get-Service TermService -ErrorAction SilentlyContinue
if ($term -and $term.Status -eq 'Running') {
    Write-Ok "Remote Desktop Services" "running"
} else {
    Write-Bad "Remote Desktop Services" "$($term.Status)" "Start the TermService service."
}

# Without the RDS Session Host role, Server 2019 permits only two administrative sessions.
# That is fine for testing and for small deployments; it is not a fault.
#
# Get-WindowsFeature only exists on Server. -ErrorAction cannot suppress a missing cmdlet
# (that is a CommandNotFoundException at parse-and-invoke time), so its presence is tested
# first rather than letting a raw PowerShell error land in front of the user.
if (Get-Command Get-WindowsFeature -ErrorAction SilentlyContinue) {
    $rds = Get-WindowsFeature -Name RDS-RD-Server -ErrorAction SilentlyContinue
    if ($rds -and $rds.Installed) {
        Write-Ok "RDS Session Host role" "installed"
    } else {
        Write-Note "RDS Session Host role" "not installed" `
            "Fine for testing. Without it Windows allows only 2 admin sessions at once."
    }
} else {
    Write-Note "RDS Session Host role" "cannot check" `
        "This is not a Windows Server. Run this script on the server itself."
}

# ---------------------------------------------------------------- our service

Write-Head "ScanBridge service"

$svc = Get-Service ScanBridge -ErrorAction SilentlyContinue
if (-not $svc) {
    Write-Bad "Service" "not installed" "Run INSTALL-ON-SERVER.bat as administrator."
} elseif ($svc.Status -ne 'Running') {
    Write-Bad "Service" "installed but $($svc.Status)" `
        "Start it:  Start-Service ScanBridge   then check the logs below."
} else {
    Write-Ok "Service" "running"

    $wmi = Get-CimInstance Win32_Service -Filter "Name='ScanBridge'"
    if ($wmi.StartName -match 'LocalSystem') {
        Write-Ok "Service account" "LocalSystem"
    } else {
        Write-Bad "Service account" "$($wmi.StartName)" `
            "Must be LocalSystem. It needs SeTcbPrivilege to start agents inside user sessions."
    }
    if ($wmi.StartMode -eq 'Auto') {
        Write-Ok "Start mode" "Automatic"
    } else {
        Write-Note "Start mode" "$($wmi.StartMode)" "Set it to Automatic so it survives a reboot."
    }
}

# ------------------------------------------------------- the virtual scanner driver

Write-Head "Virtual scanner driver"

$ds64 = Join-Path $env:SystemRoot 'twain_64\ScanBridge\ScanBridge.ds'
$ds32 = Join-Path $env:SystemRoot 'twain_32\ScanBridge\ScanBridge.ds'

if (Test-Path $ds64) {
    Write-Ok "64-bit driver" "$ds64"
} else {
    Write-Bad "64-bit driver" "missing" `
        "64-bit programs (Adobe Acrobat) will not see the scanner. Re-run INSTALL-ON-SERVER.bat."
}

if (Test-Path $ds32) {
    Write-Ok "32-bit driver" "$ds32"
} else {
    Write-Bad "32-bit driver" "missing" `
        "32-bit programs (most accounting/ERP software) will not see the scanner. Re-run INSTALL-ON-SERVER.bat."
}

# ------------------------------------------------------- TWAIN manager on the server
#
# A scanning application does not load our .ds directly - it asks a TWAIN Data Source
# Manager to find it. So the server needs a DSM of the matching bitness, otherwise our
# driver is installed but invisible.
#
#   32-bit applications: use C:\Windows\twain_32.dll, which Windows itself ships.
#   64-bit applications: need TWAINDSM.dll. Windows does NOT ship one. Most 64-bit
#                        scanning applications install their own copy next to their .exe,
#                        so a "missing" result here is only a problem if a 64-bit
#                        application actually fails to list the scanner.

Write-Head "TWAIN manager (how programs find the driver)"

$legacyDsm = Join-Path $env:SystemRoot 'twain_32.dll'
if (Test-Path $legacyDsm) {
    Write-Ok "32-bit TWAIN manager" "$legacyDsm"
} else {
    Write-Note "32-bit TWAIN manager" "not found" `
        "32-bit programs may not find the scanner unless they ship their own TWAINDSM.dll."
}

$dsm64 = @(
    (Join-Path $env:SystemRoot 'System32\TWAINDSM.dll'),
    (Join-Path $env:SystemRoot 'TWAINDSM.dll')
) | Where-Object { Test-Path $_ }

if ($dsm64) {
    Write-Ok "64-bit TWAIN manager" "$($dsm64[0])"
} else {
    Write-Note "64-bit TWAIN manager" "not installed system-wide" `
        "Normal. Acrobat and most 64-bit scanners ship their own. Only act if a 64-bit program cannot see the scanner."
}

# ---------------------------------------------------------------- live sessions

Write-Head "Active sessions"

$sessions = @()
try {
    # query.exe output is text; parsing it is the only way without extra modules.
    $raw = & query.exe session 2>$null
    $sessions = $raw | Select-Object -Skip 1 | Where-Object { $_ -match '\S' }
} catch { }

if ($sessions) {
    foreach ($line in $sessions) { Write-Host "           $line" -ForegroundColor DarkGray }
} else {
    Write-Host "           (could not read session list)" -ForegroundColor DarkGray
}

$agents = Get-Process ScanBridge.SessionAgent -ErrorAction SilentlyContinue
if ($agents) {
    Write-Ok "Session agents running" "$($agents.Count)"
} else {
    Write-Note "Session agents running" "0" `
        "Expected when nobody is connected by Remote Desktop right now."
}

# ---------------------------------------------------------------------- logs

Write-Head "Recent log activity"

$logDir = Join-Path $env:ProgramData 'ScanBridge\logs'
if (-not (Test-Path $logDir)) {
    Write-Note "Log folder" "not created yet" "It appears the first time the service starts."
} else {
    Write-Ok "Log folder" $logDir

    foreach ($pattern in @('service-*.log', 'sessionagent-*.log', 'twainds-*.log')) {
        $newest = Get-ChildItem (Join-Path $logDir $pattern) -ErrorAction SilentlyContinue |
                  Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if (-not $newest) { continue }

        Write-Host ""
        Write-Host "           $($newest.Name)" -ForegroundColor White
        Get-Content $newest.FullName -Tail 6 | ForEach-Object {
            Write-Host "             $_" -ForegroundColor DarkGray
        }
    }
}

# ------------------------------------------------- why did the scanner not open

# A scanning application can only ever say "the selected scanner is offline" or something
# equally vague, because that is all a TWAIN condition code carries. The data source wrote the
# real reason to its own log at the moment it failed, so it is read back here and translated.
#
# Without this the same symptom has four unrelated causes and no way to tell them apart.

Write-Head "Last failure reported by the driver"

$twainLogs = Get-ChildItem (Join-Path $env:ProgramData 'ScanBridge\logs\twainds-*.log') `
                -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending |
             Select-Object -First 5

$failure = $null
$failureTime = $null

foreach ($log in $twainLogs) {
    $hit = Get-Content $log.FullName -ErrorAction SilentlyContinue |
           Where-Object { $_ -match '\[ERR\]' } | Select-Object -Last 1
    if ($hit) { $failure = $hit; $failureTime = $log.LastWriteTime; break }
}

if (-not $twainLogs) {
    Write-Note "Driver log" "none" `
        "No application on this server has loaded the scanner driver yet."
} elseif (-not $failure) {
    Write-Ok "No driver errors logged" "last activity $($twainLogs[0].LastWriteTime)"
} else {
    Write-Host "           $failureTime" -ForegroundColor DarkGray
    Write-Host "           $failure" -ForegroundColor Yellow
    Write-Host ""

    # Matched on the text the data source itself writes; each of these is a different fault
    # with a different fix, and they are indistinguishable from inside the application.
    switch -Regex ($failure) {
        'shared secret not present' {
            Write-Bad "The session agent is not running in your Remote Desktop session" "" `
                "Sign out of the session completely and sign back in - the agent is started when the session is created."
            break
        }
        'cannot reach the ScanBridge agent' {
            Write-Bad "The driver could not reach the session agent" "" `
                "Sign out and back in. If it persists, read sessionagent-*.log above for why it stopped."
            break
        }
        'no scanner is available on the local PC' {
            Write-Bad "Your PC answered, but offered no scanner" "" `
                "On your own PC: open ScanBridge, confirm a scanner is listed and that Test Scan works. The tray app must be running."
            break
        }
        'no longer available' {
            Write-Bad "The scanner disappeared mid-session" "" `
                "Switch the scanner on, then click Refresh in ScanBridge on your PC."
            break
        }
        'handshake|authentication|secret mismatch' {
            Write-Bad "The server and your PC are running different builds" "" `
                "Re-run INSTALL-ON-SERVER.bat here AND INSTALL-ON-MY-PC.bat on your PC, from the same folder."
            break
        }
        default {
            Write-Note "Driver reported an error" "see the line above" `
                "Send this line to whoever is supporting you."
        }
    }
}

# -------------------------------------------------------------------- verdict

Write-Host ""
Write-Host "  ==========================================================" -ForegroundColor White

if ($script:Problems.Count -eq 0) {
    Write-Host "    SERVER LOOKS READY" -ForegroundColor Green
    Write-Host "  ==========================================================" -ForegroundColor White
    Write-Host ""
    Write-Host "  Next: on the PC with the scanner, run INSTALL-ON-MY-PC.bat,"
    Write-Host "  then connect here using Remote Desktop Connection (mstsc)."
    Write-Host ""
    Write-Host "  Do NOT use the Microsoft Store 'Windows App' - it cannot" -ForegroundColor Yellow
    Write-Host "  carry scanners, and the scanner will never appear." -ForegroundColor Yellow
} else {
    Write-Host "    $($script:Problems.Count) PROBLEM(S) FOUND" -ForegroundColor Red
    Write-Host "  ==========================================================" -ForegroundColor White
    Write-Host ""
    foreach ($problem in $script:Problems) {
        Write-Host "   * $problem" -ForegroundColor Yellow
    }
}

Write-Host ""
