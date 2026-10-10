using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Microsoft.Win32;

// As in the other projects: a P/Invoke loads its DLL from System32 only, never from the folder setup runs from.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace ConnectionManager.Setup;

internal enum SetupMode { Interactive, Elevated, Unattended }

/// ConnectionManagerSetup.exe: the release zip in one exe. Double-clicked, it starts itself elevated with --elevated,
/// which unpacks the zip into a folder only admins can change and runs its install.ps1. Then this window, without admin
/// rights, starts the tray, which must never run elevated. --unattended is the elevated part alone, for scripts.
/// Exit codes: 0 installed, 1 failed or cancelled, 2 bad arguments, 3 not elevated, otherwise install.ps1's.
internal static class Program
{
    private const string Usage = "Usage: ConnectionManagerSetup.exe [--unattended]";
    private const string ServiceName = "ConnectionManager";
    private const int ErrorCancelled = 1223; // the UAC prompt was declined

    private static int Main(string[] args)
    {
        // Before anything else: from here on, DLLs load from System32 only, never from the folder setup is in.
        if (!Native.SetDefaultDllDirectories(Native.LoadLibrarySearchSystem32))
        {
            Console.Error.WriteLine($"Setup could not limit where Windows loads DLLs from (error {Marshal.GetLastPInvokeError()}).");
            return 1;
        }
        if (ParseArguments(args) is not { } mode)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }
        using var payload = typeof(Program).Assembly.GetManifestResourceStream(Payload.ResourceName);
        if (payload is null)
        {
            Console.Error.WriteLine(@"This build has no app inside; build it with tools\publish.ps1");
            return 1;
        }
        Console.WriteLine($"Connection Manager {typeof(Program).Assembly.GetName().Version?.ToString(3)} setup");
        if (Payload.Problem(payload) is { } problem)
        {
            Console.Error.WriteLine($"The app inside this setup is damaged: {problem}.");
            return 1;
        }

        var elevated = Native.IsElevated();
        switch (mode)
        {
            case SetupMode.Unattended when !elevated:
                Console.Error.WriteLine("--unattended must run from an elevated (admin) prompt.");
                return 3;
            case SetupMode.Unattended:
            {
                var code = Install(payload);
                Console.WriteLine(code == 0 ? "Installed." : "Setup failed: see the messages above.");
                return code;
            }
            case SetupMode.Elevated when !elevated:
                return Close(3, "Setup did not get admin rights.");
            case SetupMode.Elevated:
            {
                var code = Install(payload);
                if (code != 0) return Close(code, "Setup failed: see the messages above.");
                Console.WriteLine("Installed."); // this window closes; the first one says what is next
                return 0;
            }
            case SetupMode.Interactive when elevated:
            {
                // "Run as administrator": no window without admin rights is left to start the tray from.
                var code = Install(payload);
                return Close(code, code == 0
                    ? "Installed. Connection Manager starts at every sign-in, or start it now from Start menu > Connection Manager."
                    : "Setup failed: see the messages above.");
            }
            default:
                return InstallWithPrompt();
        }
    }

    /// Exactly one of the flags, or none. Null for anything else.
    internal static SetupMode? ParseArguments(string[] args) => args switch
    {
        [] => SetupMode.Interactive,
        ["--elevated"] => SetupMode.Elevated,
        ["--unattended"] => SetupMode.Unattended,
        _ => null,
    };

    /// The elevated part: unpacks the zip straight into a new folder only admins can change, runs its install.ps1, and
    /// deletes the folder. install.ps1's exit code, or 1 when it could not run.
    private static int Install(Stream payload)
    {
        string? staging = null;
        try
        {
            var programFiles = ProgramFiles();
            staging = Staging.Create(Path.Combine(Native.WindowsFolder(), "Temp"));
            Console.WriteLine("Unpacking...");
            payload.Position = 0;
            ZipFile.ExtractToDirectory(payload, staging);
            return InstallScript.Run(staging, programFiles);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        finally
        {
            if (staging is not null) Staging.Delete(staging);
        }
    }

    /// Double-clicked: the elevated part runs in an admin window of its own, then the tray starts from this one.
    private static int InstallWithPrompt()
    {
        Console.WriteLine("Windows will ask for permission to install.");
        int code;
        try
        {
            code = Native.RunElevated(Environment.ProcessPath!, "--elevated", Environment.SystemDirectory);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return Close(1, "Setup was cancelled.");
        }
        catch (Win32Exception ex)
        {
            return Close(1, $"Setup could not ask for admin rights: {ex.Message}");
        }
        if (code != 0) return Close(code, $"Setup failed (code {code}). The admin window showed why.");

        string tray;
        try
        {
            tray = Path.Combine(ProgramFiles(), "ConnectionManager", "ConnectionManager.exe");
        }
        catch (InvalidOperationException ex)
        {
            return Close(1, ex.Message);
        }
        if (!File.Exists(tray) || !Native.IsServiceRunning(ServiceName))
            return Close(1, "Setup finished, but the Connection Manager service is not running, so the app was not started. Restart the PC: it starts at sign-in.");
        try
        {
            Process.Start(new ProcessStartInfo(tray) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(tray) })?.Dispose();
        }
        catch (Win32Exception ex)
        {
            return Close(1, $"Connection Manager is installed but did not start ({ex.Message}). Start it from the Start menu.");
        }
        return Close(0, "Done. Connection Manager is running: look for its icon in the notification area (it may be under the ^ arrow).");
    }

    /// Program Files from its admin-only registry value, as common.ps1's Get-MachineFolder reads it, never from
    /// %ProgramFiles%, which a user can override. Only %SystemDrive% is expanded, from the system folder's drive.
    internal static string ProgramFiles()
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion");
        var raw = key?.GetValue("ProgramFilesDir", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        var path = (raw ?? "").Replace("%SystemDrive%", Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\' || path.Contains('%'))
            throw new InvalidOperationException($@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\ProgramFilesDir is '{raw}', which is not a local folder setup can use.");
        return path.TrimEnd('\\');
    }

    // Waits for Enter, so a double-clicked window stays open until it has been read.
    private static int Close(int code, string message)
    {
        Console.WriteLine(message);
        Console.WriteLine("Press Enter to close.");
        Console.ReadLine();
        return code;
    }
}
