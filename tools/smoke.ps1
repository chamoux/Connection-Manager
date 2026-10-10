#Requires -RunAsAdministrator
# Dev-PC smoke test of the real install. Only SYSTEM can own the service's pipe, so the app must run as the installed
# service: this installs artifacts\ConnectionManager (run tools\publish.ps1 first), then through IpcProbe:
#   Game Mode on gives block filters; killing the service like a crash removes them; the service manager restarts it
#   with Game Mode back on Auto; Data Saver on adds its block-all filter.
# Then it uninstalls (settings kept) and puts settings.json back. It starts with default settings.
# It refuses to run over an existing install unless you pass -Reinstall. Either way the app is NOT installed afterwards.
param(
    [string]$Package = (Join-Path $PSScriptRoot "..\artifacts\ConnectionManager"),
    [string]$Probe = (Join-Path $PSScriptRoot "..\artifacts\IpcProbe\ConnectionManager.IpcProbe.exe"),
    [switch]$Reinstall
)
$ErrorActionPreference = "Stop"
$serviceName = "ConnectionManager"
$results = [ordered]@{}

foreach ($file in (Join-Path $Package "install.ps1"), (Join-Path $Package "uninstall.ps1"), (Join-Path $Package "common.ps1"), (Join-Path $Package "app\ConnectionManager.Service.exe"), $Probe) {
    if (-not (Test-Path -LiteralPath $file)) { throw "$file was not found. Run tools\publish.ps1 first." }
}
. (Join-Path $Package "common.ps1")
# Folders from admin-only registry values, as install.ps1 finds them, not $env:ProgramFiles / $env:ProgramData: a user
# can override those, and they reach this elevated run.
$installDir = Join-Path (Get-MachineFolder "SOFTWARE\Microsoft\Windows\CurrentVersion" "ProgramFilesDir") "ConnectionManager"
$data = Join-Path (Get-MachineFolder "SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList" "ProgramData") "ConnectionManager"
$settings = Join-Path $data "settings.json"
$backup = "$settings.smoke-backup"
$dump = Join-Path $env:TEMP "cm-smoke-filters.xml"
$installed = @()
if (Get-Service $serviceName -ErrorAction SilentlyContinue) { $installed += "the $serviceName service" }
if (Test-Path -LiteralPath $installDir) { $installed += $installDir }
if (Get-ScheduledTask -TaskName "Connection Manager" -ErrorAction SilentlyContinue) { $installed += "the Phase 1 'Connection Manager' task" }
if ($installed -and -not $Reinstall) {
    throw "Connection Manager is installed on this PC ($($installed -join '; ')). The smoke test installs its own copy and uninstalls it at the end, so it would replace that install. Run it again with -Reinstall to allow that, then run install.cmd afterwards."
}
# A backup still here means an earlier run did not finish: it holds the real settings, so never overwrite it.
if (Test-Path -LiteralPath $backup) { throw "$backup is left from an earlier smoke run. Rename it back to settings.json (or delete it), then run again." }

function Check([string]$What, [bool]$Ok) {
    $results[$What] = $Ok
    Write-Host ("{0} {1}" -f $(if ($Ok) { "PASS" } else { "FAIL" }), $What)
}

# Polls until the condition holds or the time is up; the last check counts.
function Wait-Until([scriptblock]$Condition, [int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        if (& $Condition) { return $true }
        Start-Sleep -Milliseconds 500
    }
    [bool](& $Condition)
}

# Native programs may write to stderr, which a redirected run (tools\run-elevated.ps1) would turn into fatal errors.
function Invoke-Probe([string[]]$Arguments) {
    $ErrorActionPreference = "Continue"
    $output = @(& $Probe @Arguments 2>&1 | ForEach-Object { "$_" })
    if ($LASTEXITCODE -ne 0) { throw "IpcProbe $Arguments failed (exit $LASTEXITCODE): $($output -join ' ')" }
    $output -join "`n"
}

# Our block filters, each with its number of conditions. Data Saver's block-all is the one with none.
function Get-BlockFilters {
    $ErrorActionPreference = "Continue"
    # verbose=on: by default netsh may hide filters it thinks are unlikely to affect connectivity.
    netsh wfp show filters file="$dump" verbose=on | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "netsh wfp show filters failed (exit $LASTEXITCODE)" }
    $xml = New-Object Xml.XmlDocument
    $xml.XmlResolver = $null
    $xml.Load($dump)
    $filters = @($xml.SelectNodes("//item[starts-with(displayData/name, 'Connection Manager: block')]") | ForEach-Object {
        [pscustomobject]@{ Name = $_.displayData.name; Conditions = $_.SelectNodes("filterCondition/item").Count }
    })
    # Each filter's name is in the dump once. If the query found fewer, netsh's XML layout changed, and a count of 0
    # after the kill would pass for the wrong reason.
    $named = [regex]::Matches($xml.OuterXml, "Connection Manager: block").Count
    if ($named -ne $filters.Count) { throw "netsh's XML layout is not what this script expects ($named names, $($filters.Count) filters found)." }
    $filters
}

function Get-ServiceInfo { Get-CimInstance Win32_Service -Filter "Name='$serviceName'" }

# The test starts from default settings: the service creates a fresh settings.json. A move keeps the file's owner
# (Administrators or SYSTEM); a copy made here would be yours, and an install by another admin sets aside a folder
# holding such files.
$hadSettings = Test-Path -LiteralPath $settings
if ($hadSettings) { Move-Item -LiteralPath $settings $backup }
try {
    & (Join-Path $Package "install.ps1")

    Invoke-Probe "set-game", "on" | Write-Host
    $blocks = 0
    $null = Wait-Until { $script:blocks = @(Get-BlockFilters).Count; $script:blocks -gt 0 } 10
    Check "Game Mode on: $blocks block filters (expect more than 0)" ($blocks -gt 0)

    $killed = (Get-ServiceInfo).ProcessId
    try { Stop-Process -Id $killed -Force }
    catch { taskkill.exe /F /PID $killed | Out-Null } # PowerShell 7 may lack the debug privilege that Windows PowerShell enables
    $left = -1
    $null = Wait-Until { $script:left = @(Get-BlockFilters).Count; $script:left -eq 0 } 5
    Check "Service killed (process $killed): $left block filters left (expect 0 within 5 s)" ($left -eq 0)

    $restarted = Wait-Until { $info = Get-ServiceInfo; $info.State -eq "Running" -and $info.ProcessId -notin 0, $killed } 20
    Check "The service manager restarted the service within 20 s" $restarted
    if ($restarted) {
        $status = Invoke-Probe "status" | ConvertFrom-Json
        Check "After the restart Game Mode is $($status.gameMode) (expect auto: manual On resets at start)" ($status.gameMode -eq "auto")

        Invoke-Probe "set-saver", "on" | Write-Host
        $blockAll = Wait-Until { @(Get-BlockFilters | Where-Object Conditions -eq 0).Count -gt 0 } 10
        Check "Data Saver on: a block-all filter (a 'Connection Manager: block' filter with no conditions) exists" $blockAll
        Invoke-Probe "set-saver", "auto" | Write-Host
    }
}
catch {
    Check "No errors ($($_.Exception.Message))" $false
}
finally {
    try {
        & (Join-Path $Package "uninstall.ps1")
        Check "Uninstalled (the service is gone)" (-not (Get-Service $serviceName -ErrorAction SilentlyContinue))
    }
    catch { Check "Uninstalled ($($_.Exception.Message))" $false }
    # The service wrote its own settings.json; put the real one back.
    Remove-Item -LiteralPath $settings -Force -ErrorAction SilentlyContinue
    if ($hadSettings) { Move-Item -LiteralPath $backup $settings }
}

if ($installed) { Write-Warning "Connection Manager is no longer installed. Run install.cmd to install it again." }
if ($results.Count -gt 0 -and -not ($results.Values -contains $false)) { "SMOKE PASSED"; exit 0 } else { "SMOKE FAILED"; exit 1 }
