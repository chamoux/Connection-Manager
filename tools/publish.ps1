# Builds a self-contained release (the service and the tray share one app\ folder) and zips it with the installer.
# Then it builds artifacts\ConnectionManagerSetup-<version>.exe, one native exe with the zip inside: the file to copy to
# the gaming PC. Also builds IpcProbe into artifacts\IpcProbe for tools\smoke.ps1; it is not shipped.
# Run it without admin rights: it starts the merged tray to test it, and the tray must never run elevated.
param([string]$Version = "1.0.0")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "artifacts\ConnectionManager"
$app = Join-Path $out "app"
$staging = Join-Path $root "artifacts\staging"
$probe = Join-Path $root "artifacts\IpcProbe"
$zip = Join-Path $root "artifacts\ConnectionManager-$Version.zip"
$setupBuild = Join-Path $root "artifacts\setup"
$setup = Join-Path $root "artifacts\ConnectionManagerSetup-$Version.exe"

# Checked first, so a build is not wasted on a launch test that cannot run.
if (([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run publish.ps1 from a window without admin rights: it starts the tray to test it, and the tray must never run elevated."
}
$running = @(Get-Process ConnectionManager -ErrorAction SilentlyContinue)
if ($running) { throw "Connection Manager is running (process $($running.Id -join ', ')). Exit it from its tray icon first: only one tray runs at a time, so the launch test would not start the new one." }

# pnpm and dotnet write progress to stderr. When this script's output is redirected, Windows PowerShell turns those
# lines into errors, and "Stop" makes the first one fatal. Run them with "Continue" and trust the exit code instead.
function Invoke-Native([string]$What, [scriptblock]$Command) {
    $ErrorActionPreference = "Continue"
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$What failed (exit $LASTEXITCODE)" }
}

# Assembly version first (what .NET binds by), then file version. A native file has assembly version 0.0.0.0.
function Get-FileRank([string]$Path) {
    $assembly = [version]"0.0.0.0"
    try { $assembly = [Reflection.AssemblyName]::GetAssemblyName($Path).Version } catch [BadImageFormatException] { }
    $info = (Get-Item -LiteralPath $Path).VersionInfo
    [pscustomobject]@{ Assembly = $assembly; File = New-Object version $info.FileMajorPart, $info.FileMinorPart, $info.FileBuildPart, $info.FilePrivatePart }
}

# The settings UI: the tray project copies web\dist to ui\. pnpm only: pnpm-lock.yaml is the one lockfile.
$web = Join-Path $root "web"
$hasWeb = Test-Path -LiteralPath (Join-Path $web "package.json")
if ($hasWeb) {
    Push-Location -LiteralPath $web
    try {
        Invoke-Native "pnpm install" { pnpm install --frozen-lockfile }
        Invoke-Native "pnpm build" { pnpm build }
    }
    finally { Pop-Location }
}

# Each app on its own first, then merged. --disable-build-servers: build servers left running would keep an elevated
# "Start-Process -Wait" caller (tools\run-elevated.ps1) waiting for minutes.
Remove-Item -LiteralPath $out, $staging -Recurse -Force -ErrorAction SilentlyContinue
foreach ($name in "Service", "Tray") {
    Invoke-Native "dotnet publish ($name)" {
        dotnet publish (Join-Path $root "src\ConnectionManager.$name\ConnectionManager.$name.csproj") -c Release -r win-x64 --self-contained true `
            --disable-build-servers -p:Version=$Version -p:DebugType=None -o (Join-Path $staging $name)
    }
}

# Most files are the same in both. A few differ: TraceEvent brings Microsoft.Extensions.* 6.0 into the tray while the
# service host uses 10.0, and the tray's Windows Forms runtime has its own System.Drawing and Microsoft.VisualBasic.
# Keep the higher version of each, because .NET loads a higher assembly version for a lower reference but never the
# reverse: published over the service, the tray's 6.0 files would stop the service from starting. Ties keep the service's.
New-Item -ItemType Directory -Force -Path $app | Out-Null
Get-ChildItem -LiteralPath (Join-Path $staging "Service") -Force | Copy-Item -Destination $app -Recurse
$trayRoot = (Get-Item -LiteralPath (Join-Path $staging "Tray")).FullName
foreach ($file in Get-ChildItem -LiteralPath $trayRoot -Recurse -File) {
    $relative = $file.FullName.Substring($trayRoot.Length + 1)
    $target = Join-Path $app $relative
    if (-not (Test-Path -LiteralPath $target)) {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        Copy-Item -LiteralPath $file.FullName $target
        continue
    }
    if ((Get-FileHash -LiteralPath $target).Hash -eq (Get-FileHash -LiteralPath $file.FullName).Hash) { continue }
    $serviceRank = Get-FileRank $target
    $trayRank = Get-FileRank $file.FullName
    $trayWins = $trayRank.Assembly -gt $serviceRank.Assembly -or ($trayRank.Assembly -eq $serviceRank.Assembly -and $trayRank.File -gt $serviceRank.File)
    if ($trayWins) { Copy-Item -LiteralPath $file.FullName $target -Force }
    Write-Host ("Merged {0}: service {1} ({2}), tray {3} ({4}), kept the {5}'s" -f $relative, $serviceRank.Assembly, $serviceRank.File,
        $trayRank.Assembly, $trayRank.File, $(if ($trayWins) { "tray" } else { "service" }))
}
Remove-Item -LiteralPath $staging -Recurse -Force

# TraceEvent extras the app never loads, about 10 MB. msdia140.dll is only for reading PDB symbols (TraceEvent's
# SymbolReader). x86\ and arm64\ are for other process architectures. amd64\KernelTraceControl.dll stays: TraceEvent
# loads it for the "NT Kernel Logger" session, heap tracing and ETL merges. The traffic meter's own real-time session
# does not need it (EnableKernelProvider uses TraceSetInformation for a named session), but it is small, and keeping
# it means a TraceEvent change there cannot break the meter.
foreach ($extra in "amd64\msdia140.dll", "x86", "arm64") {
    Remove-Item -LiteralPath (Join-Path $app $extra) -Recurse -Force -ErrorAction SilentlyContinue
}
$required = @("ConnectionManager.exe", "ConnectionManager.Service.exe", "Microsoft.Diagnostics.Tracing.TraceEvent.dll", "amd64\KernelTraceControl.dll")
if ($hasWeb) { $required += "ui\index.html" }
foreach ($file in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $app $file))) { throw "The release is missing app\$file" }
}

# The merge must not leave either app with an assembly older than it was built against: .NET refuses to load it, at
# start or only when that code first runs. Each app's deps.json lists the assembly version of every runtime file.
$depsFiles = @(Get-ChildItem -LiteralPath $app -Filter "*.deps.json" -File)
if ($depsFiles.Count -ne 2) { throw "app\ should hold two deps.json files, the service's and the tray's, not $($depsFiles.Count)." }
$tooOld = foreach ($depsFile in $depsFiles) {
    $deps = Get-Content -LiteralPath $depsFile.FullName -Raw | ConvertFrom-Json
    foreach ($library in $deps.targets.($deps.runtimeTarget.name).PSObject.Properties) {
        if (-not $library.Value.runtime) { continue }
        foreach ($asset in $library.Value.runtime.PSObject.Properties) {
            $expected = $asset.Value.assemblyVersion
            $file = Join-Path $app ([IO.Path]::GetFileName($asset.Name))
            if (-not $expected -or -not (Test-Path -LiteralPath $file -PathType Leaf)) { continue }
            $actual = (Get-FileRank $file).Assembly
            if ($actual -lt [version]$expected) { "$($depsFile.Name) expects $([IO.Path]::GetFileName($asset.Name)) $expected or higher, app\ has $actual" }
        }
    }
}
if ($tooOld) { throw "The merge kept assemblies older than an app expects:`n$($tooOld -join "`n")" }

# Both apps must start from the merged folder. The service only parses its arguments: anything but --restore-only exits
# 2 before it touches anything.
$serviceOutput = & {
    $ErrorActionPreference = "Continue"
    & (Join-Path $app "ConnectionManager.Service.exe") --bogus 2>&1 | ForEach-Object { "$_" }
}
if ($LASTEXITCODE -ne 2) { throw "The merged service did not start and refuse a bad argument (exit $LASTEXITCODE, expected 2): $($serviceOutput -join ' ')" }

# The tray runs for 5 s without admin rights. With no service to talk to it waits, disconnected, and must neither exit,
# log an error, nor write into app\ (that would end up in the zip).
$trayLogs = Join-Path ([Environment]::GetFolderPath("LocalApplicationData")) "ConnectionManager\logs"
$logSizes = @{}
Get-ChildItem -LiteralPath $trayLogs -Filter "app-*.log" -File -ErrorAction SilentlyContinue | ForEach-Object { $logSizes[$_.Name] = $_.Length }
$appFiles = @(Get-ChildItem -LiteralPath $app -Recurse -Force | ForEach-Object { $_.FullName })
# Not Start-Process: Windows PowerShell's cannot start a program in a folder with [ ] in its name.
$trayStart = New-Object Diagnostics.ProcessStartInfo (Join-Path $app "ConnectionManager.exe")
$trayStart.UseShellExecute = $false
$trayStart.WorkingDirectory = $app
$tray = [Diagnostics.Process]::Start($trayStart)
try {
    Start-Sleep -Seconds 5
    $trayRan = -not $tray.HasExited
}
finally {
    if (-not $tray.HasExited) {
        Stop-Process -Id $tray.Id -Force
        $null = $tray.WaitForExit(10000)
    }
}
if (-not $trayRan) { throw "The merged tray exited within 5 s (exit $($tray.ExitCode)). See its log in $trayLogs." }
$trayErrors = foreach ($log in Get-ChildItem -LiteralPath $trayLogs -Filter "app-*.log" -File -ErrorAction SilentlyContinue) {
    $bytes = [IO.File]::ReadAllBytes($log.FullName)
    $start = [int]$logSizes[$log.Name]
    if ($bytes.Length -gt $start) {
        [Text.Encoding]::UTF8.GetString($bytes, $start, $bytes.Length - $start) -split "\r?\n" | Where-Object { $_ -match '^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d ERROR ' }
    }
}
if ($trayErrors) { throw "The merged tray logged errors:`n$($trayErrors -join "`n")" }
$added = @(Get-ChildItem -LiteralPath $app -Recurse -Force | ForEach-Object { $_.FullName } | Where-Object { $_ -notin $appFiles })
if ($added) { throw "The merged tray wrote into app\: $($added -join ', ')" }
Write-Host "Launch test passed: the merged service refused a bad argument (exit 2) and the merged tray ran for 5 s."

# For tools\smoke.ps1 only. Framework-dependent: the dev PC has the .NET runtime.
Remove-Item -LiteralPath $probe -Recurse -Force -ErrorAction SilentlyContinue
Invoke-Native "dotnet publish (IpcProbe)" {
    dotnet publish (Join-Path $root "tools\ConnectionManager.IpcProbe\ConnectionManager.IpcProbe.csproj") -c Release `
        --disable-build-servers -p:DebugType=None -o $probe
}

Get-ChildItem -LiteralPath (Join-Path $root "install") -File | Copy-Item -Destination $out
Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
Compress-Archive -LiteralPath (Get-ChildItem -LiteralPath $out).FullName -DestinationPath $zip
Write-Host ("Created {0} ({1:N1} MB)" -f $zip, ((Get-Item -LiteralPath $zip).Length / 1MB))

# The setup exe: the zip inside one NativeAOT exe, which needs the MSVC linker. The compiler finds it through
# VsDevCmd.bat, which runs vswhere.exe by name from the Visual Studio Installer folder; with
# NoDefaultCurrentDirectoryInExePath set, cmd only looks for it on PATH.
$installer = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer"
if (Test-Path -LiteralPath (Join-Path $installer "vswhere.exe")) { $env:PATH = "$installer;$env:PATH" }
Remove-Item -LiteralPath $setupBuild, $setup -Recurse -Force -ErrorAction SilentlyContinue
Invoke-Native "dotnet publish (Setup)" {
    dotnet publish (Join-Path $root "tools\ConnectionManager.Setup\ConnectionManager.Setup.csproj") -c Release -r win-x64 `
        --disable-build-servers -p:PayloadZip=$zip -p:Version=$Version -o $setupBuild
}
Copy-Item -LiteralPath (Join-Path $setupBuild "ConnectionManagerSetup.exe") $setup
Remove-Item -LiteralPath $setupBuild -Recurse -Force

# It must start and refuse a bad argument (exit 2) before it looks at anything else. This window has no admin rights,
# so nothing is installed.
$setupOutput = & {
    $ErrorActionPreference = "Continue"
    & $setup --bogus 2>&1 | ForEach-Object { "$_" }
}
if ($LASTEXITCODE -ne 2) { throw "The setup exe did not start and refuse a bad argument (exit $LASTEXITCODE, expected 2): $($setupOutput -join ' ')" }
Write-Host ("Created {0} ({1:N1} MB), the file to copy to the gaming PC" -f $setup, ((Get-Item -LiteralPath $setup).Length / 1MB))
