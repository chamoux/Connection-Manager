#Requires -RunAsAdministrator
# Removes Connection Manager: the service, the tray app, its sign-in entry, Start menu shortcut and Apps & Features
# entry, the event log source, and a Phase 1 logon task if one is left. What the service changed in Windows (download
# caps, metered networks) is put back first. Settings and logs stay unless you add -RemoveSettings.
# It runs from the extracted package or from the install folder's setup\ (Apps & Features), which it deletes: PowerShell
# has read this script and common.ps1 by then, and nothing below reads a file next to it again.
param([switch]$RemoveSettings)
$ErrorActionPreference = "Stop"
$serviceName = "ConnectionManager"
$legacyTask = "Connection Manager"   # Phase 1

# A 32-bit PowerShell sees "Program Files (x86)" and the 32-bit registry, so it would miss the install.
if ([Environment]::Is64BitOperatingSystem -and -not [Environment]::Is64BitProcess) { throw "Run uninstall from 64-bit PowerShell (double-click uninstall.cmd in Explorer)." }
# Windows cannot delete a folder that is a process's current folder, and this one may be started in setup\.
[Environment]::CurrentDirectory = [Environment]::SystemDirectory

. (Join-Path $PSScriptRoot "common.ps1")
# Folders from admin-only registry values, not $env:ProgramFiles / $env:ProgramData or the known folders built on them:
# a user can override those, and they reach this elevated run.
$programData = Get-MachineFolder "SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList" "ProgramData"
$installDir = Join-Path (Get-MachineFolder "SOFTWARE\Microsoft\Windows\CurrentVersion" "ProgramFilesDir") "ConnectionManager"
$dataDir = Join-Path $programData "ConnectionManager"
$sc = Join-Path ([Environment]::GetFolderPath("System")) "sc.exe"
$serviceExe = Join-Path $installDir "ConnectionManager.Service.exe"
$runKey = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run"
$startMenu = Get-MachineFolder "SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders" "Common Programs" @{ ProgramData = $programData }
$shortcut = Join-Path $startMenu "Connection Manager.lnk"
$eventSource = "HKLM:\SYSTEM\CurrentControlSet\Services\EventLog\Application\$serviceName"
$restored = $true

# sc.exe gets its arguments as one string, exactly as written. $Allow lists exit codes that are not a failure.
function Invoke-Sc([string]$Arguments, [int[]]$Allow = @()) {
    $info = New-Object Diagnostics.ProcessStartInfo $sc, $Arguments
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $process = [Diagnostics.Process]::Start($info)
    $output = $process.StandardOutput.ReadToEnd()
    $process.WaitForExit()
    if ($process.ExitCode -ne 0 -and $process.ExitCode -notin $Allow) { throw "sc.exe $Arguments failed (exit $($process.ExitCode)): $($output.Trim())" }
}

# Every tray (each signed-in user's, and Phase 1's elevated one), so its files can be deleted. Phase 1's WFP filters go with it.
Stop-ScheduledTask -TaskName $legacyTask -ErrorAction SilentlyContinue
$left = Stop-ProcessAndWait (Get-Process ConnectionManager -ErrorAction SilentlyContinue).Id
if ($left) { Write-Warning "Connection Manager is still running (process $($left.Id -join ', ')); its files may not all be deleted." }
Unregister-ScheduledTask -TaskName $legacyTask -Confirm:$false -ErrorAction SilentlyContinue

# Stopping the service removes its filters and restores what it changed.
$service = Get-Service $serviceName -ErrorAction SilentlyContinue
if ($service -and $service.Status -ne "Stopped") {
    Stop-Service $serviceName -Force -NoWait -ErrorAction SilentlyContinue
    try { $service.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30)) }
    catch {
        # Deleted first, so recovery does not start it again. Its filters go with the process; the restore below
        # puts back the rest.
        Write-Warning "The $serviceName service did not stop within 30 seconds, so it is ended."
        Invoke-Sc "delete $serviceName" -Allow 1072
        $servicePid = (Get-CimInstance Win32_Service -Filter "Name='$serviceName'").ProcessId
        if ($servicePid -and (Stop-ProcessAndWait $servicePid)) { Write-Warning "The service process $servicePid did not end." }
    }
}

# Put back what the service journaled, also after a crash or a stop that could not restore. It needs the service's
# files, so it runs before they are deleted.
if (Test-Path -LiteralPath $serviceExe -PathType Leaf) {
    # Native errors go to stderr. When this script's output is redirected (tools\run-elevated.ps1), Windows PowerShell
    # turns each stderr line into an error, which "Stop" would make fatal. Show them and trust the exit code instead.
    $ErrorActionPreference = "Continue"
    & $serviceExe --restore-only 2>&1 | ForEach-Object { Write-Host "  $_" }
    $code = $LASTEXITCODE
    $ErrorActionPreference = "Stop"
    if ($code -ne 0) {
        $restored = $false
        Write-Warning "Some Windows settings Connection Manager changed could not be put back (exit $code). See the log in $dataDir\logs."
    }
    # Run as you, the restore may have started a log file that you own. Administrators must own everything here, or an
    # install by another admin sets the folder aside as untrusted. Item by item, and only in a folder users cannot
    # change, so nothing outside it is touched and nothing a user put there becomes the admins'.
    if (Test-Path -LiteralPath $dataDir) {
        $problem = Get-DataFolderProblem $dataDir -AllowMine
        if ($problem) { Write-Warning "$problem, so its owners were left as they are. The next install moves it aside and settings start fresh." }
        else {
            try { Set-AdminOwner $dataDir }
            catch { Write-Warning "Could not make Administrators the owner of everything in ${dataDir}: $($_.Exception.Message)" }
        }
    }
}
elseif (Test-Path -LiteralPath (Join-Path $dataDir "journal.json")) {
    $restored = $false
    Write-Warning "$serviceExe is missing, so the Windows settings listed in $dataDir\journal.json could not be put back."
}

if ($service) {
    Invoke-Sc "delete $serviceName" -Allow 1060, 1072   # already gone / already marked for deletion
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Service $serviceName -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
}
Remove-ItemProperty -LiteralPath $runKey -Name $serviceName -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $shortcut -Force -ErrorAction SilentlyContinue
if (Test-Path -LiteralPath $eventSource) { [Diagnostics.EventLog]::DeleteEventSource($serviceName) }
Remove-Item -LiteralPath $installDir -Recurse -Force -ErrorAction SilentlyContinue
# Apps & Features runs the uninstaller kept in the install folder: while that is still there, keep its entry to run it again.
if (-not (Test-Path -LiteralPath (Join-Path $installDir "setup\uninstall.ps1"))) { Remove-Item -LiteralPath $UninstallKey -Recurse -Force -ErrorAction SilentlyContinue }
if ($RemoveSettings) {
    # The journal is the only record of what is left to put back: keep it.
    if ($restored) { Remove-Item -LiteralPath $dataDir -Recurse -Force -ErrorAction SilentlyContinue }
    else { Write-Warning "$dataDir was kept: it lists what could not be put back. Install again to retry, then uninstall." }
}

# Say what is left instead of claiming success.
$left = @()
if (Get-Service $serviceName -ErrorAction SilentlyContinue) { $left += "the $serviceName service" }
if (Get-ScheduledTask -TaskName $legacyTask -ErrorAction SilentlyContinue) { $left += "the '$legacyTask' task" }
if (Test-Path -LiteralPath $installDir) { $left += $installDir }
if (Get-ItemProperty -LiteralPath $runKey -Name $serviceName -ErrorAction SilentlyContinue) { $left += "the sign-in entry $runKey\$serviceName" }
if (Test-Path -LiteralPath $shortcut) { $left += $shortcut }
if (Test-Path -LiteralPath $eventSource) { $left += "the event log source $serviceName" }
if (Test-Path -LiteralPath $UninstallKey) { $left += "the Apps & Features entry $UninstallKey" }
if ($RemoveSettings -and $restored -and (Test-Path -LiteralPath $dataDir)) { $left += $dataDir }
if ($left) { Write-Warning "Not everything could be removed: $($left -join '; '). Restart the PC and run uninstall again." }
elseif ($restored) { Write-Host "Connection Manager is removed." }
else { Write-Host "Connection Manager is removed, but see the warning above about Windows settings." }
