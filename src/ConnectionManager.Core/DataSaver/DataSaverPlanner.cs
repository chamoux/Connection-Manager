using System.Security.Principal;
using ConnectionManager.Core.Engine;
using ConnectionManager.Core.Logging;
using ConnectionManager.Core.Paths;
using ConnectionManager.Core.Wfp;

namespace ConnectionManager.Core.DataSaver;

/// Data Saver's rules while it is on. Everything is blocked at the lowest weight; the essentials (DNS, DHCP, the
/// network status check, time, Defender) are allowed above Game Mode; below it, what may still connect: Steam, running
/// games and anti-cheat, running security software, the user's allowed apps, this service, and Windows downloads when
/// allowed. WFP opens every program path as SYSTEM, so each one passes SafePaths first, even a running process's (an exe
/// can run from a network share). One thread only: the engine's.
public sealed class DataSaverPlanner : IDataSaverPlanner
{
    public static readonly IReadOnlyList<string> EssentialServices = ["Dnscache", "Dhcp", "NlaSvc", "W32Time", "WinDefend"];
    public static readonly IReadOnlyList<string> WindowsDownloadServices = ["wuauserv", "BITS", "DoSvc"];

    /// Where Steam keeps its three programs, under its folder. The web helper's folder name changes between versions,
    /// so a running one anywhere under the Steam folder counts too.
    public static readonly IReadOnlyList<string> SteamPrograms =
        ["steam.exe", @"bin\steamservice.exe", @"bin\cef\cef.win7x64\steamwebhelper.exe", @"bin\cef\cef.win64\steamwebhelper.exe"];

    public static readonly IReadOnlyList<string> SecurityNames =
    [
        "MsMpEng.exe", "NisSrv.exe", "MpDefenderCoreService.exe", "avp.exe", "avpui.exe", "ekrn.exe", "AvastSvc.exe", "AVGSvc.exe",
        "bdservicehost.exe", "vsserv.exe", "MBAMService.exe", "NortonSecurity.exe",
    ];

    private const int MaxLoggedRejections = 256;

    private static readonly string[] SteamNames = ["steam.exe", "steamservice.exe", "steamwebhelper.exe"];
    private static readonly string[] AntiCheatPrefixes = ["EasyAntiCheat", "BEService", "faceit"];

    private static readonly FilterRule BlockAll = new(RuleKind.All, "", RuleAction.Block, RuleWeight.SaverBlockAll, "everything (Data Saver)");

    private static readonly FilterRule[] EssentialPorts =
    [
        new(RuleKind.RemotePort, "53", RuleAction.Permit, RuleWeight.EssentialPermit, "DNS"),
        new(RuleKind.LocalPort, "68/udp", RuleAction.Permit, RuleWeight.EssentialPermit, "DHCP"),
        new(RuleKind.LocalPort, "546/udp", RuleAction.Permit, RuleWeight.EssentialPermit, "DHCPv6"),
    ];

    private readonly ILog _log;
    private readonly Func<string, bool> _serviceExists;
    private readonly string[] _securityFolders; // each ends with '\'
    private readonly Dictionary<string, bool> _services = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _loggedRejections = new(StringComparer.OrdinalIgnoreCase);

    /// serviceExists says whether a service's SID (NT SERVICE\name) resolves; it is asked once per name, since a rule
    /// for a service that isn't installed can't be added. Tests pass a fake.
    public DataSaverPlanner(ILog log, Func<string, bool>? serviceExists = null)
    {
        _log = log;
        _serviceExists = serviceExists ?? ServiceSidResolves;
        // Known folders, never environment variables, which a process can set to anything. A missing one ("") is skipped.
        _securityFolders = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows Defender"),
            }
            .Select(PathRules.FolderPrefix).OfType<string>().ToArray();
    }

    public IReadOnlySet<FilterRule> Plan(TickContext context)
    {
        var rules = new HashSet<FilterRule> { BlockAll };
        AddServices(rules, EssentialServices, RuleWeight.EssentialPermit);
        rules.UnionWith(EssentialPorts);

        var steamFolder = PathRules.FolderPrefix(context.SteamPath);
        if (context.SteamPath is { } steam)
            foreach (var relative in SteamPrograms)
                if (SafePaths.TryLocal(Path.Combine(steam, relative), out var full) && File.Exists(full)) AddApp(rules, full, "Steam");

        // By name first, so only the few candidates pay for SafePaths; the folder checks use its normalised path.
        foreach (var process in context.Running)
        {
            var name = Path.GetFileName(process.Path);
            string what;
            string[]? onlyUnder; // null: anywhere
            if (IsAntiCheat(name)) (what, onlyUnder) = ("anti-cheat", null);
            else if (IsOneOf(name, SecurityNames)) (what, onlyUnder) = ("security software", _securityFolders); // a lookalike in a user folder gets nothing
            else if (IsOneOf(name, SteamNames) && steamFolder is not null) (what, onlyUnder) = ("Steam", [steamFolder]);
            else continue;
            if (!SafePaths.TryLocal(process.Path, out var full)) continue; // a share or a link: skipped, not logged every tick
            if (onlyUnder is null || onlyUnder.Any(f => PathRules.IsUnder(full, f))) AddApp(rules, full, what);
        }

        foreach (var game in context.Game.RunningGames) AddUntrusted(rules, game, "game");
        foreach (var app in context.Settings.AllowApps) AddUntrusted(rules, app, "allowed app");
        if (context.ServiceExe is { } self) AddUntrusted(rules, self, "Connection Manager");

        if (context.Settings.AllowWindowsDownloads) AddServices(rules, WindowsDownloadServices, RuleWeight.SaverPermit);
        return rules;
    }

    /// True when NT SERVICE\name maps to a SID, which it does only for an installed service.
    public static bool ServiceSidResolves(string name)
    {
        try
        {
            new NTAccount("NT SERVICE", name).Translate(typeof(SecurityIdentifier));
            return true;
        }
        catch (SystemException) // IdentityNotMappedException, or the lookup itself failed
        {
            return false;
        }
    }

    private void AddServices(HashSet<FilterRule> rules, IEnumerable<string> services, RuleWeight weight)
    {
        foreach (var service in services)
        {
            if (!_services.TryGetValue(service, out var exists))
            {
                exists = _serviceExists(service);
                _services[service] = exists;
                if (!exists) _log.Info($"Data Saver: the {service} service is not installed, so it gets no rule");
            }
            if (exists) rules.Add(new FilterRule(RuleKind.Service, service, RuleAction.Permit, weight, service));
        }
    }

    private void AddUntrusted(HashSet<FilterRule> rules, string path, string what)
    {
        if (SafePaths.TryLocal(path, out var full)) AddApp(rules, full, what);
        else if (_loggedRejections.Count < MaxLoggedRejections && _loggedRejections.Add(path))
            _log.Warn($"Not allowed during Data Saver: {SafePaths.Printable(path)} ({what}) is not a plain path on a local drive, or it goes through a link");
    }

    private static void AddApp(HashSet<FilterRule> rules, string full, string what) =>
        rules.Add(new FilterRule(RuleKind.App, full, RuleAction.Permit, RuleWeight.SaverPermit, $"{Path.GetFileName(full)} ({what})"));

    private static bool IsAntiCheat(string name) =>
        string.Equals(name, "vgc.exe", StringComparison.OrdinalIgnoreCase)
        || (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && AntiCheatPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)));

    private static bool IsOneOf(string name, IEnumerable<string> names) => names.Contains(name, StringComparer.OrdinalIgnoreCase);
}
