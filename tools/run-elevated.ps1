# Runs one command in an elevated PowerShell (one UAC prompt the user must approve) and prints its output here.
# Example: powershell -ExecutionPolicy Bypass -File tools\run-elevated.ps1 -Command "dotnet test tests\ConnectionManager.Integration.Tests"
param(
    [Parameter(Mandatory = $true)][string]$Command,
    [string]$Log = (Join-Path $env:TEMP "cm-elevated.log")
)
Remove-Item -LiteralPath $Log -Force -ErrorAction SilentlyContinue
# The paths go into single-quoted strings, so their quotes are doubled (this also covers the typographic quotes
# PowerShell reads as single quotes). Out-File -LiteralPath, because a *> redirection cannot write to a path with [ ].
$here = [Management.Automation.Language.CodeGeneration]::EscapeSingleQuotedStringContent((Get-Location).Path)
$logPath = [Management.Automation.Language.CodeGeneration]::EscapeSingleQuotedStringContent($Log)
# A script-stopping error (throw, -ErrorAction Stop) is not caught by *>&1, so write it to the log and exit 1.
$inner = "Set-Location -LiteralPath '$here'; try { & { $Command } *>&1 | Out-File -LiteralPath '$logPath' } catch { `$_ | Out-File -LiteralPath '$logPath' -Append; exit 1 }; exit [int]`$LASTEXITCODE"
# Encoded, so no quote or space in the command or the paths changes on the way through the command line. Started in the
# system folder: Start-Process cannot start in a folder with [ ] in its name, and the command changes folder itself.
$encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($inner))
try {
    $process = Start-Process powershell -Verb RunAs -Wait -PassThru -WorkingDirectory ([Environment]::SystemDirectory) -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', $encoded)
}
catch {
    Write-Error "The admin prompt was cancelled or failed: $($_.Exception.Message)"
    exit 1
}
if (Test-Path -LiteralPath $Log) { Get-Content -LiteralPath $Log }
exit $process.ExitCode
