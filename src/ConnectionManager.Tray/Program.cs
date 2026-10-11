using System.Runtime.InteropServices;
using System.Security.Principal;
using ConnectionManager.Core.Logging;
using ConnectionManager.Core.Tray;
using Microsoft.Win32.SafeHandles;

// Native DLLs from System32 only. WebView2Loader.dll is not affected: the WebView2 package loads it itself.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace ConnectionManager.Tray;

internal static class Program
{
    private const int TokenElevationType = 18;    // TOKEN_INFORMATION_CLASS
    private const int TokenElevationTypeFull = 2; // the elevated half of an admin's split token
    private const uint AsfwAny = unchecked((uint)-1); // ASFW_ANY

    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // The tray runs as the signed-in user and keeps its own log; the service logs to ProgramData.
        var logFolder = LogFolder();
        var log = new FileLog(logFolder);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => log.Error("Unhandled UI error", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => log.Error("Unhandled error", e.ExceptionObject as Exception);

        // The tray needs no rights, and its web page (WebView2) must not run elevated.
        if (StartedAsAdmin())
        {
            log.Warn("Started with admin rights; this copy exits");
            MessageBox.Show("Start Connection Manager from the Start menu, without admin rights.", "Connection Manager", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 1;
        }

        var openWindow = TrayLaunch.OpensWindow(args);
        Mutex single;
        bool first;
        string openEvent;
        try
        {
            // One tray per user per session: Local\ is per session, the SID tells users apart (runas).
            using var me = WindowsIdentity.GetCurrent();
            openEvent = TrayLaunch.OpenEventName(me.User?.Value ?? "unknown");
            single = new Mutex(true, $@"Local\ConnectionManager.Tray.{me.User?.Value}", out first);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            // Another program in the session can create this name first, with rights we lack or as another kind of object.
            log.Error("Could not check whether Connection Manager is already running", ex);
            Fail(ex);
            return 1;
        }

        using (single)
        {
            if (!first)
            {
                // Started again from the Start menu: the user wants the window. This copy was started by the user, so it
                // may hand the running one the right to come to the front.
                if (openWindow) AllowSetForegroundWindow(AsfwAny);
                log.Info(openWindow && OpenRequests.Send(openEvent) ? "Already running; asked it to open its window" : "Already running; this copy exits");
                return 0;
            }
            try
            {
                Application.Run(new TrayContext(log, logFolder, openEvent, openWindow));
                return 0;
            }
            catch (Exception ex)
            {
                log.Error("Connection Manager could not start", ex);
                Fail(ex);
                return 1;
            }
        }
    }

    /// %LOCALAPPDATA%\ConnectionManager\logs, from the known folder rather than the environment variable.
    private static string LogFolder()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local)) local = Path.GetTempPath(); // no profile folder: still log somewhere
        return Path.Combine(local, "ConnectionManager", "logs");
    }

    // "Run as administrator", or started by an elevated program, while the user also has a normal token. With UAC off
    // there is no normal token to run with (TokenElevationTypeDefault), so that is allowed.
    private static bool StartedAsAdmin()
    {
        using var me = WindowsIdentity.GetCurrent();
        return GetTokenInformation(me.AccessToken, TokenElevationType, out var type, sizeof(int), out _) && type == TokenElevationTypeFull;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int infoClass, out int info, int length, out int returned);

    private static void Fail(Exception ex) =>
        MessageBox.Show($"Connection Manager could not start: {ex.Message}", "Connection Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
