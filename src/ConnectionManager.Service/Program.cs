using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using ConnectionManager.Core.Caps;
using ConnectionManager.Core.DataSaver;
using ConnectionManager.Core.Engine;
using ConnectionManager.Core.Ipc;
using ConnectionManager.Core.Logging;
using ConnectionManager.Core.Native;
using ConnectionManager.Core.Network;
using ConnectionManager.Core.Processes;
using ConnectionManager.Core.Qos;
using ConnectionManager.Core.Reports;
using ConnectionManager.Core.Settings;
using ConnectionManager.Core.Steam;
using ConnectionManager.Core.Traffic;
using ConnectionManager.Core.Wfp;
using ConnectionManager.Core.Wifi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Win32;

// Native DLLs from System32 only, never from the app folder or the current folder.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace ConnectionManager.Service;

/// The ConnectionManager service, running as LocalSystem. It runs as a console app when not started by the service
/// manager. `--restore-only` puts back every journaled change and exits; the uninstaller runs it.
internal static class Program
{
    public const string ServiceName = "ConnectionManager";
    private const string RestoreOnlyArgument = "--restore-only";

    private static int Main(string[] args)
    {
        // The installer and uninstaller are the only callers, so anything else is a mistake.
        var restoreOnly = args is [RestoreOnlyArgument];
        if (args.Length > 0 && !restoreOnly)
        {
            Console.Error.WriteLine($"Usage: ConnectionManager.Service.exe [{RestoreOnlyArgument}]");
            return 2;
        }

        // Before any file is opened: as SYSTEM, never follow a junction a normal user made (logged once the log exists).
        var redirectionGuard = ProcessHardening.EnforceRedirectionTrust();

        // Before anything reads settings from the folder or writes logs to it, so a problem here cannot be logged.
        var dataFolder = SettingsStore.DefaultFolder;
        if (DataFolder.EnsureSafe(dataFolder) is { } unsafeFolder)
        {
            ReportFatal(unsafeFolder);
            return 1;
        }

        var log = new FileLog(Path.Combine(dataFolder, "logs"));
        if (redirectionGuard is not null) log.Warn($"RedirectionGuard could not be turned on, so junctions are checked only where paths are: {redirectionGuard}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => log.Error("Unhandled error", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            log.Error("Unobserved task error", e.Exception);
            e.SetObserved();
        };

        // Every change outside the data folder and the dynamic WFP session, each journaled in <data>\journal.json before
        // it is made. One journal for both: each keeps the other's entries when it saves, on one thread at a time.
        var journal = new PolicyJournal(dataFolder, log);
        var caps = new DoCapManager(new HklmRegistryStore(), journal, log, DoCapManager.RefreshMachinePolicy);
        var metered = new MeteredToggle(new WlanClient(), journal, log);
        var restore = new RestoreHooks(
        [
            new Restorable("Windows download caps", () => caps.Restore().Problem),
            new Restorable("metered Wi-Fi networks", () => metered.RestoreAll().Problem, userChoice: true),
        ], log);
        if (restoreOnly)
        {
            var restored = restore.RunAll("for the uninstaller", includeUserChoices: true);
            // A crashed service can leave upload caps behind until the next reboot.
            var capsRemoved = new UploadCapManager(new CimQosStore(), log).RemoveAll() is null;
            return restored && capsRemoved ? 0 : 1;
        }

        try
        {
            return Run(dataFolder, log, restore, caps, metered);
        }
        catch (Exception ex)
        {
            Fail(log, "The Connection Manager service failed", ex);
            return 1;
        }
    }

    private static int Run(string dataFolder, ILog log, RestoreHooks restore, DoCapManager caps, MeteredToggle metered)
    {
        log.Info(Invariant($"Connection Manager service starting (pid {Environment.ProcessId})"));
        var store = new SettingsStore(Path.Combine(dataFolder, "settings.json"));
        var (settings, settingsProblem) = ServiceStartup.LoadSettings(store, log);
        restore.RunAll("at start", includeUserChoices: false); // before anything else changes the system

        WfpEngine wfp;
        try
        {
            wfp = new WfpEngine();
        }
        catch (Exception ex) when (ex is WfpException or PlatformNotSupportedException)
        {
            Fail(log, "Could not open the Windows Filtering Platform; Connection Manager must run as the ConnectionManager service (LocalSystem)", ex);
            return 1;
        }

        ServiceEngine engine;
        ServiceEngine? wakeable = null; // the Wi-Fi tuner's reconnect signal, from a WLAN thread
        PipeServerHost? pipe = null;
        try
        {
            var user = new ActiveUser(log);
            var meter = new TrafficMeter(log);
            var parts = new EngineParts(wfp, new SystemProcessSource(), new NetworkWatcher(log),
                () => SteamLibraries.ReadSteamPathForUser(user.Current?.Sid), new TimedFileReader(log).Read, user.ExpandUserEnv,
                AppContext.BaseDirectory)
            {
                Saver = new DataSaverPlanner(log),
                Caps = new DoCapController(caps),
                Wifi = new WifiController(new WifiTuner(new WlanClient(), log), () => wakeable?.Wake()),
                Meter = meter,
                Reports = new ReportSource(meter, new ReportStore(dataFolder, log), log),
                Metered = new MeteredToggleAdapter(metered),
                UploadCaps = new UploadCapController(new UploadCapManager(new CimQosStore(), log)), // ActiveStore: cleared at reboot
                ProcessLookup = SystemProcessSource.Describe,
                Restore = restore,
                SettingsProblem = settingsProblem,
            };
            engine = new ServiceEngine(parts, settings, store, status => pipe?.Publish(status), TimeProvider.System, log);
            wakeable = engine;
            pipe = new PipeServerHost(engine.HandleAsync, log);
        }
        catch
        {
            wfp.Dispose();
            throw;
        }

        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = AppContext.BaseDirectory,
            ApplicationName = ServiceName, // the event log source the installer registers, used for the host's own errors
        });
        builder.Services.AddWindowsService(o => o.ServiceName = ServiceName);
        builder.Services.AddHostedService(_ => new ServiceWorker(engine, pipe, log));
        using var host = builder.Build();
        host.Run();
        return 0;
    }

    /// Logs a fatal problem and reports it where an admin can see it without the log.
    public static void Fail(ILog log, string what, Exception ex)
    {
        log.Error(what, ex);
        ReportFatal($"{what}: {ex.Message}");
    }

    /// The Application event log (source "ConnectionManager", registered by the installer), and stderr for a console run.
    public static void ReportFatal(string message)
    {
        Console.Error.WriteLine(message);
        try
        {
            // Writing to an unregistered source would register it, which is an HKLM write the service must not make.
            using var source = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\EventLog\Application\" + ServiceName);
            if (source is not null) EventLog.WriteEntry(ServiceName, message, EventLogEntryType.Error);
        }
        catch (Exception ex) when (ex is SecurityException or InvalidOperationException or Win32Exception or IOException or UnauthorizedAccessException)
        {
            // stderr has it
        }
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
