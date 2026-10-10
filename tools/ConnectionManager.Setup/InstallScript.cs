using System.Diagnostics;

namespace ConnectionManager.Setup;

/// Runs the unpacked install.ps1 in Windows PowerShell, in this console, so its progress shows here.
internal static class InstallScript
{
    private static readonly string[] RuntimeSettings = ["COR_", "COMPLUS_", "DOTNET_", "CORECLR_"];

    /// install.ps1's exit code.
    public static int Run(string staging, string programFiles)
    {
        var system = Environment.SystemDirectory;
        var start = new ProcessStartInfo(Path.Combine(system, @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = false,
            WorkingDirectory = system,
        };
        foreach (var argument in (string[])["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(staging, "install.ps1")])
            start.ArgumentList.Add(argument);
        CleanEnvironment(start.Environment, $@"{system}\WindowsPowerShell\v1.0\Modules;{programFiles}\WindowsPowerShell\Modules");

        using var powershell = Process.Start(start) ?? throw new InvalidOperationException("Windows PowerShell did not start.");
        // Ctrl+C reaches PowerShell too, which stops the script; setup stays to delete the staging folder.
        ConsoleCancelEventHandler stay = (_, e) => e.Cancel = true;
        Console.CancelKeyPress += stay;
        try
        {
            powershell.WaitForExit();
        }
        finally
        {
            Console.CancelKeyPress -= stay;
        }
        return powershell.ExitCode;
    }

    /// The elevated PowerShell would get the user's own environment (HKCU\Environment). Without the COR_ and COMPLUS_
    /// names, no .NET Framework profiler the user set loads into it (DOTNET_ and CORECLR_ do the same for .NET), and
    /// PSModulePath lists only the admin-only module folders, so no module from Documents loads. Everything else is kept.
    internal static void CleanEnvironment(IDictionary<string, string?> environment, string modulePath)
    {
        foreach (var name in environment.Keys.ToList())
        {
            if (name.Equals("PSModulePath", StringComparison.OrdinalIgnoreCase)
                || RuntimeSettings.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                environment.Remove(name);
        }
        environment["PSModulePath"] = modulePath;
    }
}
