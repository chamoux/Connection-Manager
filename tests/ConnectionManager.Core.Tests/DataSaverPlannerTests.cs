using ConnectionManager.Core.DataSaver;
using ConnectionManager.Core.Engine;
using ConnectionManager.Core.Network;
using ConnectionManager.Core.Processes;
using ConnectionManager.Core.Settings;
using ConnectionManager.Core.Wfp;

namespace ConnectionManager.Core.Tests;

public sealed class DataSaverPlannerTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 20, 0, 0, TimeSpan.Zero);
    private static readonly string ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    private static readonly string ServiceExe = Path.Combine(ProgramFiles, @"ConnectionManager\ConnectionManager.Service.exe");

    private readonly TempTree _tree = new();
    private readonly ListLog _log = new();
    private readonly HashSet<string> _missingServices = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _serviceLookups = [];

    public void Dispose() => _tree.Dispose();

    private DataSaverPlanner NewPlanner() => new(_log, name =>
    {
        _serviceLookups.Add(name);
        return !_missingServices.Contains(name);
    });

    private static TickContext Context(AppSettings? settings = null, IReadOnlyList<RunningProcess>? running = null,
        IReadOnlyList<string>? games = null, string? steam = null) =>
        new(Now, settings ?? new AppSettings(), NetworkSnapshot.None, running ?? [], ControllerStatus.Starting with { RunningGames = games ?? [] },
            SaverActive: true, steam, [], _ => false, ServiceExe);

    private static RunningProcess Process(string path) => new(1000, path);

    private static FilterRule Permit(RuleKind kind, string value, RuleWeight weight = RuleWeight.SaverPermit) => new(kind, value, RuleAction.Permit, weight, "");

    private static FilterRule Essential(RuleKind kind, string value) => Permit(kind, value, RuleWeight.EssentialPermit);

    private static IEnumerable<string> Apps(IReadOnlySet<FilterRule> rules) =>
        rules.Where(r => r is { Kind: RuleKind.App, Action: RuleAction.Permit, Weight: RuleWeight.SaverPermit }).Select(r => r.Value);

    [Fact]
    public void With_nothing_running_it_blocks_all_and_allows_only_the_essentials_and_itself()
    {
        var rules = NewPlanner().Plan(Context());

        FilterRule[] expected =
        [
            new(RuleKind.All, "", RuleAction.Block, RuleWeight.SaverBlockAll, ""),
            Essential(RuleKind.Service, "Dnscache"), Essential(RuleKind.Service, "Dhcp"), Essential(RuleKind.Service, "NlaSvc"),
            Essential(RuleKind.Service, "W32Time"), Essential(RuleKind.Service, "WinDefend"),
            Essential(RuleKind.RemotePort, "53"), Essential(RuleKind.LocalPort, "68/udp"), Essential(RuleKind.LocalPort, "546/udp"),
            Permit(RuleKind.App, ServiceExe),
        ];
        Assert.True(rules.SetEquals(expected), string.Join("\n", rules));
        Assert.All(rules, r => Assert.False(string.IsNullOrWhiteSpace(r.Label))); // filter names show it
    }

    [Fact]
    public void A_service_whose_sid_does_not_resolve_is_skipped_and_looked_up_only_once()
    {
        _missingServices.Add("W32Time");
        var planner = NewPlanner();

        var first = planner.Plan(Context());
        var second = planner.Plan(Context());

        Assert.DoesNotContain(first, r => r.Value == "W32Time");
        Assert.Contains(Essential(RuleKind.Service, "Dnscache"), second);
        Assert.Single(_serviceLookups, s => s == "W32Time");
        Assert.Equal(5, _serviceLookups.Count);
    }

    [Fact]
    public void The_real_lookup_knows_installed_services_from_missing_ones()
    {
        Assert.True(DataSaverPlanner.ServiceSidResolves("Dnscache"));
        Assert.False(DataSaverPlanner.ServiceSidResolves("ConnectionManagerNoSuchService"));
    }

    [Fact]
    public void Windows_downloads_add_their_three_services_only_when_allowed()
    {
        _missingServices.Add("DoSvc");
        var planner = NewPlanner();

        var off = planner.Plan(Context(new AppSettings { AllowWindowsDownloads = false }));
        var on = planner.Plan(Context(new AppSettings { AllowWindowsDownloads = true }));

        Assert.DoesNotContain(off, r => r.Value is "wuauserv" or "BITS" or "DoSvc");
        Assert.Contains(Permit(RuleKind.Service, "wuauserv"), on);
        Assert.Contains(Permit(RuleKind.Service, "BITS"), on);
        Assert.DoesNotContain(on, r => r.Value == "DoSvc"); // not installed
    }

    [Fact]
    public void Steam_programs_that_exist_in_the_steam_folder_are_allowed()
    {
        var steam = Path.Combine(_tree.Root, "Steam");
        var client = _tree.Touch(@"Steam\steam.exe");
        var helper = _tree.Touch(@"Steam\bin\cef\cef.win64\steamwebhelper.exe");

        var rules = NewPlanner().Plan(Context(steam: steam));

        Assert.Equal(new[] { client, helper, ServiceExe }.Order(StringComparer.OrdinalIgnoreCase), Apps(rules).Order(StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain(rules, r => r.Value.EndsWith("steamservice.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Running_steam_programs_count_only_inside_the_steam_folder()
    {
        var steam = Path.Combine(_tree.Root, "Steam");
        var newHelper = Path.Combine(steam, @"bin\cef\cef.win99\steamwebhelper.exe");
        var lookalike = Path.Combine(_tree.Root, @"Downloads\steam.exe");

        var rules = NewPlanner().Plan(Context(steam: steam, running: [Process(newHelper), Process(lookalike)]));

        Assert.Contains(newHelper, Apps(rules));
        Assert.DoesNotContain(lookalike, Apps(rules));
    }

    [Fact]
    public void Running_games_are_allowed()
    {
        var game = Path.Combine(_tree.Root, @"Steam\steamapps\common\Elden Ring\eldenring.exe");

        var rules = NewPlanner().Plan(Context(games: [game]));

        Assert.Contains(Permit(RuleKind.App, game), rules);
    }

    [Theory]
    [InlineData("EasyAntiCheat_EOS.exe")]
    [InlineData("easyanticheat.exe")]
    [InlineData("BEService_x64.exe")]
    [InlineData("FACEIT.exe")]
    [InlineData("faceitservice.exe")]
    [InlineData("vgc.exe")]
    public void Running_anti_cheat_is_allowed_by_name(string name)
    {
        var path = Path.Combine(_tree.Root, "Game", name);

        Assert.Contains(path, Apps(NewPlanner().Plan(Context(running: [Process(path)]))));
    }

    [Theory]
    [InlineData("vgc2.exe")]
    [InlineData("myvgc.exe")]
    [InlineData("EasyAntiCheat.dll")]
    [InlineData("chrome.exe")]
    public void Other_running_programs_are_not_allowed(string name)
    {
        var path = Path.Combine(_tree.Root, "Game", name);

        Assert.DoesNotContain(path, Apps(NewPlanner().Plan(Context(running: [Process(path)]))));
    }

    [Fact]
    public void Running_security_software_is_allowed_only_from_admin_only_folders()
    {
        var defender = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            @"Microsoft\Windows Defender\Platform\4.18.25010.11-0\MsMpEng.exe");
        var kaspersky = Path.Combine(ProgramFiles, @"Kaspersky Lab\Kaspersky 21.20\avp.exe");
        var eset = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"ESET\ekrn.exe");
        // The Windows folder is not trusted: users can write to parts of it (Temp, System32\Tasks, spool\drivers\color).
        var nis = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Temp\NisSrv.exe");
        var userCopy = _tree.Touch(@"Downloads\avp.exe");
        var escape = ProgramFiles + @"\..\" + Path.GetRelativePath(Path.GetPathRoot(_tree.Root)!, Path.Combine(_tree.Root, "ekrn.exe"));
        var otherProgramData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Evil\MsMpEng.exe");

        var apps = Apps(NewPlanner().Plan(Context(running: [.. new[] { defender, kaspersky, eset, nis, userCopy, escape, otherProgramData }.Select(Process)]))).ToList();

        Assert.Contains(defender, apps);
        Assert.Contains(kaspersky, apps);
        Assert.Contains(eset, apps);
        Assert.DoesNotContain(nis, apps);
        Assert.DoesNotContain(userCopy, apps);           // a lookalike in a user folder gets nothing
        Assert.DoesNotContain(apps, a => a.EndsWith(@"\ekrn.exe", StringComparison.OrdinalIgnoreCase) && a != eset); // .. can't escape
        Assert.DoesNotContain(otherProgramData, apps);   // only Defender's own ProgramData folder
    }

    [Fact]
    public void Allowed_apps_go_through_safe_paths()
    {
        var chat = _tree.Touch(@"Apps\chat.exe");
        _tree.Touch(@"Real\tool.exe");
        var link = _tree.Junction(Path.Combine(_tree.Root, "Link"), Path.Combine(_tree.Root, "Real"));
        var settings = new AppSettings
        {
            AllowApps = [chat, @"\\server\share\tool.exe", @"\\?\C:\Tools\tool.exe", "tool.exe", Path.Combine(link, "tool.exe"), @"C:\Tools\tool.exe:x"],
        };
        var planner = NewPlanner();

        var rules = planner.Plan(Context(settings));
        planner.Plan(Context(settings));

        Assert.Equal(new[] { chat, ServiceExe }.Order(StringComparer.OrdinalIgnoreCase), Apps(rules).Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(5, _log.Lines.Count(l => l.StartsWith("WARN Not allowed during Data Saver", StringComparison.Ordinal))); // once each
    }

    [Fact]
    public void A_running_program_on_a_network_share_is_skipped()
    {
        var rules = NewPlanner().Plan(Context(running: [Process(@"\\server\share\vgc.exe")], games: [@"\\server\games\game.exe"]));

        Assert.Equal([ServiceExe], Apps(rules));
    }

    [Fact]
    public void Every_program_appears_once_whatever_the_case()
    {
        var game = Path.Combine(_tree.Root, @"Games\vgc.exe");
        var settings = new AppSettings { AllowApps = [game.ToUpperInvariant()] };

        var rules = NewPlanner().Plan(Context(settings, running: [Process(game)], games: [game]));

        Assert.Single(Apps(rules), a => string.Equals(a, game, StringComparison.OrdinalIgnoreCase));
    }
}
