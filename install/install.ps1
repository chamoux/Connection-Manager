#Requires -RunAsAdministrator
# Installs or updates Connection Manager: the ConnectionManager service (LocalSystem), which does the blocking, and the
# tray app, which starts without admin rights at every user's sign-in and controls the service. Replaces a Phase 1
# install (an elevated tray started by a logon task). This script never starts the tray, so the tray never inherits
# its admin rights: ConnectionManagerSetup.exe or install.cmd starts it afterwards from its own, unelevated window.
# It also registers the Apps & Features entry, which runs a copy of uninstall.ps1 kept in the install folder.
$ErrorActionPreference = "Stop"
$serviceName = "ConnectionManager"
$legacyTask = "Connection Manager"   # Phase 1

# A 32-bit PowerShell sees "Program Files (x86)" and the 32-bit registry, so the parts would not find each other.
if ([Environment]::Is64BitOperatingSystem -and -not [Environment]::Is64BitProcess) { throw "Run install from 64-bit PowerShell (double-click install.cmd in Explorer)." }

. (Join-Path $PSScriptRoot "common.ps1")
# Folders from admin-only registry values, not $env:ProgramFiles / $env:ProgramData or the known folders built on them:
# a user can override those, and they reach this elevated run.
$programData = Get-MachineFolder "SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList" "ProgramData"
$programFiles = Get-MachineFolder "SOFTWARE\Microsoft\Windows\CurrentVersion" "ProgramFilesDir"
$installDir = Join-Path $programFiles "ConnectionManager"
$dataDir = Join-Path $programData "ConnectionManager"
$startMenu = Get-MachineFolder "SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders" "Common Programs" @{ ProgramData = $programData }
$sc = Join-Path ([Environment]::GetFolderPath("System")) "sc.exe"
$source = Join-Path $PSScriptRoot "app"
# Only admins can change it. A random name, so nobody can make it first.
$stage = Join-Path ([Environment]::GetFolderPath("Windows")) "Temp\ConnectionManager-install-$([guid]::NewGuid())"
$stageApp = Join-Path $stage "app"
$tray = Join-Path $installDir "ConnectionManager.exe"
$serviceExe = Join-Path $installDir "ConnectionManager.Service.exe"
$runKey = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run"

# sc.exe gets its arguments as one string, exactly as written, so the quotes around the service path survive in every
# PowerShell version (PowerShell's own quoting of native arguments differs between 5.1 and 7).
function Invoke-Sc([string]$Arguments) {
    $info = New-Object Diagnostics.ProcessStartInfo $sc, $Arguments
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $process = [Diagnostics.Process]::Start($info)
    $output = $process.StandardOutput.ReadToEnd()
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "sc.exe $Arguments failed (exit $($process.ExitCode)): $($output.Trim())" }
}

# The useful part of an error from a .NET method call ("Exception calling ... with 0 argument(s): ...").
function Get-Reason($ErrorRecord) {
    if ($ErrorRecord.Exception.InnerException) { $ErrorRecord.Exception.InnerException.Message } else { $ErrorRecord.Exception.Message }
}

# Why the service did not start: the end of its log, and what it and the service manager wrote to the event logs.
function Show-ServiceProblem([datetime]$Since) {
    $log = Get-ChildItem -LiteralPath (Join-Path $dataDir "logs") -Filter "app-*.log" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime | Select-Object -Last 1
    if ($log) {
        Write-Host "Last lines of $($log.FullName):"
        Get-Content -LiteralPath $log.FullName -Tail 15 | ForEach-Object { Write-Host "  $_" }
    }
    $events = @(Get-WinEvent -FilterHashtable @{ LogName = "Application"; ProviderName = $serviceName, ".NET Runtime", "Application Error"; StartTime = $Since } -ErrorAction SilentlyContinue |
        Where-Object { $_.ProviderName -eq $serviceName -or $_.Message -like "*ConnectionManager.Service*" }) +
        @(Get-WinEvent -FilterHashtable @{ LogName = "System"; ProviderName = "Service Control Manager"; StartTime = $Since } -ErrorAction SilentlyContinue |
        Where-Object { $_.Message -like "*Connection Manager*" })
    foreach ($entry in ($events | Sort-Object TimeCreated | Select-Object -Last 5)) {
        Write-Host "Event log, $($entry.ProviderName), $($entry.TimeCreated):"
        Write-Host "  $($entry.Message)"
    }
}

# You can change the folder the zip was extracted to while this runs, and Copy-Item -Recurse copies what a link points
# to. So the app is first copied, without links, to a folder only admins can change, and every check and the install
# itself use that copy. Everything that can fail on its own is checked before anything is stopped or deleted.
$sourceItem = Get-Item -LiteralPath $source -Force -ErrorAction SilentlyContinue
if (-not $sourceItem -or -not $sourceItem.PSIsContainer -or ($sourceItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw "The app folder next to install.ps1 is missing or is a link. Extract the zip again, then run install again."
}
$staged = $false
try {
    New-LockedFolder $stage "O:BAD:P(A;OICI;FA;;;BA)(A;OICI;FA;;;SY)"
    $staged = $true
    Copy-Tree $source $stageApp
    # The uninstaller and what it dot-sources, for Apps & Features, staged the same way.
    Copy-Tree $PSScriptRoot $stage "uninstall.ps1", "common.ps1"
    foreach ($file in "app\ConnectionManager.exe", "app\ConnectionManager.Service.exe", "uninstall.ps1", "common.ps1") {
        if (-not (Test-Path -LiteralPath (Join-Path $stage $file) -PathType Leaf)) { throw "$file was not found next to install.ps1" }
    }

    # Settings and logs: admins and SYSTEM may write, users may only read. Any user can create folders in ProgramData, so
    # an existing folder may belong to someone else, hold links, or let users add files. Only one the service would
    # accept is kept: users cannot change it while this runs. Any other is moved aside once the service is stopped,
    # and settings start fresh, because repairing it in place would keep whatever a user put inside.
    $dataExists = Test-Path -LiteralPath $dataDir
    $dataProblem = if ($dataExists) { Get-DataFolderProblem $dataDir -AllowMine }

    # Phase 1 started an elevated tray from a logon task. Remove the task, so nothing starts the old way again.
    if (Get-ScheduledTask -TaskName $legacyTask -ErrorAction SilentlyContinue) {
        Stop-ScheduledTask -TaskName $legacyTask -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $legacyTask -Confirm:$false
        if (Get-ScheduledTask -TaskName $legacyTask -ErrorAction SilentlyContinue) { throw "Could not remove the old '$legacyTask' task. Delete it in Task Scheduler, then run install again." }
    }

    # Every tray (Phase 1's elevated one, and each signed-in user's), so its files are free. Phase 1's WFP filters go with it.
    $running = Stop-ProcessAndWait (Get-Process ConnectionManager -ErrorAction SilentlyContinue).Id
    if ($running) { throw "Connection Manager is still running (process $($running.Id -join ', ')). Close it from its tray icon, then run install again." }

    # The service removes its filters and restores what it changed when it stops.
    $existing = Get-Service $serviceName -ErrorAction SilentlyContinue
    if ($existing -and $existing.Status -ne "Stopped") {
        Stop-Service $serviceName -Force -NoWait -ErrorAction SilentlyContinue
        try { $existing.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30)) }
        catch { throw "The $serviceName service did not stop within 30 seconds. Restart the PC, then run install again." }
    }

    # Before the files are replaced, so a problem here leaves the old version in place.
    if ($dataProblem) {
        Rename-Item -LiteralPath $dataDir -NewName "ConnectionManager.untrusted-$(Get-Date -Format yyyyMMddHHmmss)"
        Write-Warning "$dataProblem. It was moved aside and settings start fresh."
    }
    if ($dataExists -and -not $dataProblem) {
        # The whole security descriptor, owner and DACL, so no extra entry an admin added survives.
        $security = New-Object Security.AccessControl.DirectorySecurity
        $security.SetSecurityDescriptorSddlForm($DataFolderSddl)
        Set-Acl -LiteralPath $dataDir -AclObject $security
        # What an earlier admin run made under the "object creator" owner policy, such as uninstall's restore, is yours.
        Set-AdminOwner $dataDir
    }
    else {
        New-LockedFolder $dataDir $DataFolderSddl
    }
    $dataProblem = Get-DataFolderProblem $dataDir
    if ($dataProblem) { throw "$dataProblem. Run install again." }

    # Replace every file, so nothing from an older version is left behind.
    New-Item -ItemType Directory -Force -Path $installDir | Out-Null
    Get-ChildItem -LiteralPath $installDir -Force | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    if (Get-ChildItem -LiteralPath $installDir -Force) { throw "Some files in $installDir are in use. Restart the PC, then run install again." }
    Copy-Tree $stageApp $installDir
    Copy-Tree $stage (Join-Path $installDir "setup") "uninstall.ps1", "common.ps1"
    # A downloaded zip marks its files as "from the internet". Without this, every start from the Start menu asks to confirm.
    Get-ChildItem -LiteralPath $installDir -Recurse -File | Unblock-File
}
finally {
    # Only a folder made here: anything else at that path is not ours to delete.
    if ($staged) { Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue }
}

# Apps & Features, registered as soon as the files are in place, so it can also remove an install that fails below.
# DisplayVersion leaves out the build's commit id (1.0.0+<commit>).
$version = (Get-Item -LiteralPath $serviceExe).VersionInfo.ProductVersion -replace '\+.*$'
$size = (Get-ChildItem -LiteralPath $installDir -Recurse -File -Force | Measure-Object Length -Sum).Sum
New-Item -Path $UninstallKey -Force | Out-Null
$entry = [ordered]@{
    DisplayName = "Connection Manager"; DisplayVersion = $version; Publisher = "Connection Manager"; InstallLocation = $installDir
    DisplayIcon = "$tray,0"; UninstallString = Get-UninstallCommand $installDir $programFiles
}
foreach ($name in $entry.Keys) { New-ItemProperty -LiteralPath $UninstallKey -Name $name -Value $entry[$name] -PropertyType String -Force | Out-Null }
$numbers = [ordered]@{ EstimatedSize = [int][Math]::Ceiling($size / 1KB); NoModify = 1; NoRepair = 1 }
foreach ($name in $numbers.Keys) { New-ItemProperty -LiteralPath $UninstallKey -Name $name -Value $numbers[$name] -PropertyType DWord -Force | Out-Null }

# Where the service reports a problem it cannot write to its log (a data folder it does not trust).
if (-not (Test-Path -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Services\EventLog\Application\$serviceName")) {
    [Diagnostics.EventLog]::CreateEventSource($serviceName, "Application")
}

# The service: started at boot, as LocalSystem. Recovery restarts it after a crash or a fatal error (exit code 1),
# 5 s after the first and second failure of a day and 60 s after later ones.
$verb = if ($existing) { "config" } else { "create" }
Invoke-Sc "$verb $serviceName binPath= `"\`"$serviceExe\`"`" start= auto obj= LocalSystem password= `"`" DisplayName= `"Connection Manager`""
Invoke-Sc "description $serviceName `"Blocks updaters and background downloads while you play, and saves data on metered or hotspot connections. The Connection Manager tray app controls it.`""
Invoke-Sc "sidtype $serviceName unrestricted"
Invoke-Sc "failure $serviceName reset= 86400 actions= restart/5000/restart/5000/restart/60000"
Invoke-Sc "failureflag $serviceName 1"
$config = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
if ($config.PathName -ne "`"$serviceExe`"" -or $config.StartMode -ne "Auto" -or $config.StartName -ne "LocalSystem") {
    throw "The $serviceName service is not set up as expected: path $($config.PathName), start $($config.StartMode), account $($config.StartName)."
}

# Every user's tray starts at sign-in, without admin rights. The Start menu shortcut starts it by hand.
New-ItemProperty -LiteralPath $runKey -Name $serviceName -Value "`"$tray`"" -PropertyType String -Force | Out-Null
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut((Join-Path $startMenu "Connection Manager.lnk"))
$shortcut.TargetPath = $tray
$shortcut.WorkingDirectory = $installDir
$shortcut.Save()

# Running is reported as soon as the service host starts, so also check it is still running, as the same process, a
# few seconds later: a service that cannot open WFP or its pipe exits right after.
$startedAt = Get-Date
try {
    $service = Get-Service $serviceName
    $service.Start()
    $service.WaitForStatus("Running", [TimeSpan]::FromSeconds(30))
    $processId = (Get-CimInstance Win32_Service -Filter "Name='$serviceName'").ProcessId
    Start-Sleep -Seconds 5
    $now = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
    if ($now.State -ne "Running" -or $now.ProcessId -ne $processId) { throw "It stopped right after starting." }
}
catch {
    Show-ServiceProblem $startedAt
    throw "The $serviceName service did not start: $(Get-Reason $_)"
}
Write-Host "Connection Manager is installed and its service is running. The tray app starts at every sign-in (look for the round icon, it may be under the ^ arrow)."
