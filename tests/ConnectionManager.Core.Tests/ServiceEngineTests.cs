using System.Security.Principal;
using System.Text.Json;
using ConnectionManager.Core.Caps;
using ConnectionManager.Core.DataSaver;
using ConnectionManager.Core.Engine;
using ConnectionManager.Core.Ipc;
using ConnectionManager.Core.Modes;
using ConnectionManager.Core.Network;
using ConnectionManager.Core.Processes;
using ConnectionManager.Core.Qos;
using ConnectionManager.Core.Reports;
using ConnectionManager.Core.Settings;
using ConnectionManager.Core.Wfp;
using ConnectionManager.Core.Wifi;
using Microsoft.Extensions.Time.Testing;

namespace ConnectionManager.Core.Tests;

public sealed class ServiceEngineTests : IAsyncLifetime
{
    private const string TrayExe = @"C:\Program Files\ConnectionManager\ConnectionManager.exe";
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly TempTree _tree = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 9, 20, 0, 0, TimeSpan.Zero));
    private readonly FakeProcessSource _processes = new();
    private readonly FakeWfpEngine _wfp = new();
    private readonly FakeNetwork _network = new();
    private readonly SafeLog _log = new();
    private readonly List<StatusDto> _published = [];
    private readonly string _game;
    private readonly string _updater;
    private ServiceEngine? _engine;

    public ServiceEngineTests()
    {
        _game = _tree.Touch(@"Steam\steamapps\common\Elden Ring\eldenring.exe");
        _updater = _tree.Touch(@"x86\Google\GoogleUpdater\1.0\updater.exe");
    }

    private string SettingsPath => Path.Combine(_tree.Root, "data", "settings.json");

    private StatusDto LastPublished
    {
        get
        {
            lock (_published) return _published[^1];
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_engine is not null) await _engine.DisposeAsync();
        _tree.Dispose();
    }

    private ServiceEngine Start(AppSettings? settings = null, IDataSaverPlanner? saver = null, string? settingsPath = null, RestoreHooks? restore = null,
        Func<EngineParts, EngineParts>? configure = null)
    {
        // Only %ProgramFiles(x86)% points somewhere; every other catalog variable stays unset and matches nothing.
        var parts = new EngineParts(_wfp, _processes, _network, () => Path.Combine(_tree.Root, "Steam"), _ => null,
            text => text.Replace("%ProgramFiles(x86)%", Path.Combine(_tree.Root, "x86")), Path.Combine(_tree.Root, "App"))
        {
            Saver = saver ?? NoDataSaverRules.Instance,
            Restore = restore,
        };
        parts = configure?.Invoke(parts) ?? parts;
        _engine = new ServiceEngine(parts, settings ?? new AppSettings(), new SettingsStore(settingsPath ?? SettingsPath), status =>
        {
            lock (_published) _published.Add(status);
        }, _time, _log);
        _engine.Start();
        return _engine;
    }

    private static ClientIdentity Client(bool mayChange = true) =>
        new(WindowsIdentity.GetCurrent().User!, 1, ClientIdentity.MediumRid, false, 1234, TrayExe, mayChange);

    private static Request Request(Command command, object? args = null) => new(Framing.Version, 7, command,
        args is null ? null : JsonSerializer.SerializeToElement(args, IpcJson.Default.GetTypeInfo(args.GetType())!));

    private Task<Response> Send(Command command, object? args = null, bool mayChange = true) =>
        _engine!.HandleAsync(Client(mayChange), Request(command, args), Ct);

    private static StatusDto StatusOf(Response reply) => reply.Data!.Value.Deserialize(IpcJson.Default.StatusDto)!;

    private async Task<StatusDto> CurrentStatus() => StatusOf(await Send(Command.GetStatus));

    private static FilterRule GameBlock(RuleKind kind, string value) => new(kind, value, RuleAction.Block, RuleWeight.GameBlock, "");

    private static OptionsDto Options(int grace = 2, string hotkey = "Ctrl+Alt+G", int capKBps = 200) =>
        new(grace, true, hotkey, true, false, capKBps, true, true, true, true);

    [Fact]
    public async Task Set_game_mode_on_blocks_and_publishes_it()
    {
        var engine = Start();
        await engine.TickAsync();
        Assert.False(LastPublished.GameActive);

        var reply = await Send(Command.SetGameMode, new ModeArgs(ModeSetting.On));

        Assert.True(reply.Ok);
        Assert.Equal(7, reply.Id);
        var status = StatusOf(reply);
        Assert.Equal((ModeSetting.On, true, 4), (status.GameMode, status.GameActive, status.BlockedCount)); // updater.exe + 3 services
        Assert.Contains(GameBlock(RuleKind.App, _updater), _wfp.Rules);
        Assert.Contains(GameBlock(RuleKind.Service, "DoSvc"), _wfp.Rules);
        Assert.True(LastPublished.GameActive);
        Assert.Equal(ModeSetting.On, new SettingsStore(SettingsPath).Load().Mode); // saved
        Assert.Contains($"INFO Game Mode set to On by {TrayExe} (pid 1234)", _log.Lines);
    }

    [Fact]
    public async Task A_client_whose_session_is_not_active_can_neither_change_nor_read()
    {
        // Another user (a fast user switch, say) sees nothing of the active user's status, usage or reports.
        var meter = new FakeMeter();
        Start(configure: p => p with { Meter = meter, Reports = new ReportSource(meter, new ReportStore(_tree.Root, _log), _log) });

        var refused = await Send(Command.SetGameMode, new ModeArgs(ModeSetting.On), mayChange: false);
        var reads = await Task.WhenAll(new[] { Command.GetStatus, Command.GetUsage, Command.GetReports }.Select(c => Send(c, mayChange: false)));

        Assert.False(refused.Ok);
        Assert.Equal("Only the active user can change settings.", refused.Error);
        Assert.Empty(_wfp.Filters);
        Assert.All(reads, r => Assert.Equal((false, "Only the active user can see the status."), (r.Ok, r.Error)));
        Assert.All(reads, r => Assert.Null(r.Data));
        Assert.True((await Send(Command.GetStatus)).Ok);
    }

    [Theory]
    [InlineData(@"\\server\share\tool.exe")]
    [InlineData(@"\\?\C:\Tools\tool.exe")]
    [InlineData(@"\\.\pipe\tool")]
    [InlineData(@"tool.exe")]
    [InlineData(@"C:\Tools\tool.exe:stream")]
    [InlineData(@"C:\Tools\tool.exe.")]
    public async Task An_unsafe_path_is_refused(string path)
    {
        Start();

        foreach (var command in new[] { Command.AddBlock, Command.AllowApp, Command.AddGame, Command.MarkNotAGame })
        {
            var reply = await Send(command, new PathArgs(path));
            Assert.False(reply.Ok);
            Assert.Contains("local drive", reply.Error);
        }
        var settings = (await CurrentStatus()).Settings;
        Assert.Empty(settings.ExtraBlocks);
        Assert.Empty(settings.AllowApps);
        Assert.Empty(settings.AlwaysGames);
        Assert.Empty(settings.NeverGames);
    }

    [Fact]
    public async Task A_path_through_a_junction_is_refused()
    {
        _tree.Touch(@"Real\tool.exe");
        var link = _tree.Junction(Path.Combine(_tree.Root, "Link"), Path.Combine(_tree.Root, "Real"));
        Start();

        var program = await Send(Command.AddBlock, new PathArgs(Path.Combine(link, "tool.exe")));
        var folder = await Send(Command.AddBlock, new PathArgs(link, IsFolder: true));
        var allow = await Send(Command.AllowApp, new PathArgs(Path.Combine(link, "tool.exe")));

        Assert.All(new[] { program, folder, allow }, r =>Assert.Equal((false, true), (r.Ok, r.Error!.Contains("links"))));
        Assert.Empty((await CurrentStatus()).Settings.ExtraBlocks);
    }

    [Fact]
    public async Task A_local_program_is_added_with_its_full_path()
    {
        var tool = _tree.Touch(@"Tools\tool.exe");
        Start();

        var reply = await Send(Command.AddBlock, new PathArgs(Path.Combine(_tree.Root, @"Tools\.\..\Tools\tool.exe")));

        Assert.True(reply.Ok);
        Assert.Equal(new UserBlock(tool, false), Assert.Single(StatusOf(reply).Settings.ExtraBlocks));
    }

    [Fact]
    public async Task Protected_programs_are_neither_blocked_nor_allowed_one_by_one()
    {
        Start();
        await _engine!.TickAsync(); // finds the Steam library
        var svchost = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "svchost.exe");

        var steamGame = await Send(Command.AddBlock, new PathArgs(_game));
        var windows = await Send(Command.AllowApp, new PathArgs(svchost));
        var windowsGame = await Send(Command.AddGame, new PathArgs(svchost));

        Assert.False(steamGame.Ok);
        Assert.Contains("protected", steamGame.Error);
        Assert.False(windows.Ok);
        Assert.Contains("can't be allowed", windows.Error);
        Assert.False(windowsGame.Ok); // a game may use the internet during Data Saver
    }

    [Theory]
    [InlineData(-1, 200)]
    [InlineData(31, 200)]
    [InlineData(2, 15)]
    [InlineData(2, 100_001)]
    public async Task Options_out_of_range_are_refused(int grace, int capKBps)
    {
        Start();

        var reply = await Send(Command.SetOptions, Options(grace: grace, capKBps: capKBps));

        Assert.False(reply.Ok);
        Assert.Equal(2, (await CurrentStatus()).Settings.Options.GraceMinutes);
    }

    [Theory]
    [InlineData("G")]
    [InlineData("Shift+G")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Alt+Esc")]
    [InlineData("Ctrl+Ctrl+G")]
    [InlineData(" ")]
    public async Task A_bad_hotkey_is_refused(string hotkey)
    {
        Start();

        var reply = await Send(Command.SetOptions, Options(hotkey: hotkey));

        Assert.False(reply.Ok);
        Assert.Equal("Ctrl+Alt+G", (await CurrentStatus()).Settings.Options.Hotkey);
    }

    [Fact]
    public async Task Valid_options_are_applied_and_saved()
    {
        Start();

        var reply = await Send(Command.SetOptions, Options(grace: 30, hotkey: " Ctrl+Shift+F9 ", capKBps: 16));

        Assert.True(reply.Ok);
        Assert.Equal(Options(grace: 30, hotkey: "Ctrl+Shift+F9", capKBps: 16), StatusOf(reply).Settings.Options);
        Assert.Equal("Ctrl+Shift+F9", new SettingsStore(SettingsPath).Load().Hotkey);
    }

    [Fact]
    public async Task Missing_arguments_get_a_short_error()
    {
        Start();

        var reply = await Send(Command.AddBlock);

        Assert.False(reply.Ok);
        Assert.Equal("Missing arguments for AddBlock.", reply.Error);
    }

    [Fact]
    public async Task A_manual_on_resets_to_auto_at_startup()
    {
        var store = new SettingsStore(SettingsPath);
        store.Save(new AppSettings { Mode = ModeSetting.On, DataSaver = ModeSetting.On, GraceMinutes = 7 });

        var (settings, problem) = ServiceStartup.LoadSettings(store, _log);
        Start(settings);

        Assert.Null(problem);
        Assert.Equal((ModeSetting.Auto, ModeSetting.Auto, 7), (settings.Mode, settings.DataSaver, settings.GraceMinutes));
        var saved = new SettingsStore(SettingsPath).Load();
        Assert.Equal((ModeSetting.Auto, ModeSetting.Auto), (saved.Mode, saved.DataSaver));
        var status = await CurrentStatus();
        Assert.Equal((ModeSetting.Auto, false), (status.GameMode, status.GameActive));
    }

    [Fact]
    public void Off_survives_a_restart()
    {
        var settings = new AppSettings { Mode = ModeSetting.Off, DataSaver = ModeSetting.Off };

        Assert.False(ServiceStartup.ResetManualOn(settings));
        Assert.Equal((ModeSetting.Off, ModeSetting.Off), (settings.Mode, settings.DataSaver));
    }

    [Fact]
    public async Task A_failed_save_still_applies_the_change_and_says_so()
    {
        var notAFolder = _tree.Touch("file");
        Start(settingsPath: Path.Combine(notAFolder, "settings.json")); // creating its folder fails

        var reply = await Send(Command.SetGameMode, new ModeArgs(ModeSetting.On));

        Assert.True(reply.Ok);
        var status = StatusOf(reply);
        Assert.True(status.GameActive);
        Assert.Contains(ServiceStartup.SaveProblem, status.Problems);
        Assert.NotEmpty(_wfp.Filters);
    }

    [Fact]
    public async Task Data_saver_and_game_mode_rules_go_in_one_reconcile()
    {
        var allowed = _tree.Touch(@"Apps\chat.exe");
        var blockAll = new FilterRule(RuleKind.All, "", RuleAction.Block, RuleWeight.SaverBlockAll, "everything");
        var permit = new FilterRule(RuleKind.App, allowed, RuleAction.Permit, RuleWeight.SaverPermit, "chat");
        var planner = new FakePlanner(blockAll, permit);
        _processes.Running.Add(new RunningProcess(42, allowed));
        var engine = Start(new AppSettings { Mode = ModeSetting.On, DataSaver = ModeSetting.On }, planner);

        await engine.TickAsync();

        Assert.Equal(1, _wfp.Transactions);
        Assert.Contains(blockAll, _wfp.Rules);
        Assert.Contains(permit, _wfp.Rules);
        Assert.Contains(GameBlock(RuleKind.App, _updater), _wfp.Rules);
        Assert.True(planner.Last!.SaverActive);
        Assert.Contains(planner.Last.Running, p => p.Pid == 42);
        var status = await CurrentStatus();
        Assert.Equal((true, "On (manual)", 4), (status.SaverActive, status.SaverReason, status.BlockedCount)); // only Game Mode blocks count
    }

    [Fact]
    public async Task Turning_data_saver_off_removes_only_its_rules()
    {
        var blockAll = new FilterRule(RuleKind.All, "", RuleAction.Block, RuleWeight.SaverBlockAll, "everything");
        Start(new AppSettings { DataSaver = ModeSetting.On }, new FakePlanner(blockAll));
        await _engine!.TickAsync();
        Assert.Contains(blockAll, _wfp.Rules);

        var reply = await Send(Command.SetDataSaver, new ModeArgs(ModeSetting.Off));

        Assert.False(StatusOf(reply).SaverActive);
        Assert.Empty(_wfp.Filters);
    }

    [Fact]
    public async Task Auto_data_saver_follows_the_hotspot_list()
    {
        _network.Current = new NetworkSnapshot(NetworkKind.Wifi, Guid.NewGuid(), "Pixel 8", ["192.168.43.1"], NetworkCost.Unrestricted);
        Start();
        Assert.False((await CurrentStatus()).SaverActive);

        var reply = await Send(Command.SetHotspot, new HotspotArgs("Pixel 8", IsHotspot: true));

        var status = StatusOf(reply);
        Assert.Equal((true, "Your hotspot"), (status.SaverActive, status.SaverReason));
        Assert.Equal(["Pixel 8"], status.Settings.HotspotNetworks);
        Assert.True(status.Network.IsHotspot);
        Assert.False(StatusOf(await Send(Command.SetHotspot, new HotspotArgs("Pixel 8", IsHotspot: false))).SaverActive);
    }

    [Fact]
    public async Task A_failed_reconcile_waits_a_minute_before_trying_again()
    {
        _wfp.FailCommit = true;
        var engine = Start(new AppSettings { Mode = ModeSetting.On });
        await engine.TickAsync();
        Assert.Equal(1, _wfp.Transactions);
        Assert.Contains((await CurrentStatus()).Problems, p => p.StartsWith("Could not update the network filters", StringComparison.Ordinal));

        _time.Advance(ServiceEngine.RetryAfter - TimeSpan.FromSeconds(1));
        await engine.TickAsync();
        Assert.Equal(1, _wfp.Transactions);

        _wfp.FailCommit = false;
        _time.Advance(TimeSpan.FromSeconds(1));
        await engine.TickAsync();

        Assert.Equal(2, _wfp.Transactions);
        Assert.Contains(GameBlock(RuleKind.App, _updater), _wfp.Rules);
        Assert.DoesNotContain((await CurrentStatus()).Problems, p => p.StartsWith("Could not update", StringComparison.Ordinal));
        Assert.Single(_log.Lines, l => l.StartsWith("ERROR Could not update the network filters", StringComparison.Ordinal)); // once
    }

    [Fact]
    public async Task A_command_retries_at_once_despite_the_back_off()
    {
        _wfp.FailCommit = true;
        var engine = Start(new AppSettings { Mode = ModeSetting.On });
        await engine.TickAsync();
        _wfp.FailCommit = false;

        await Send(Command.ToggleCatalog, new IdArgs("nvidia-app"));

        Assert.Equal(2, _wfp.Transactions);
        Assert.NotEmpty(_wfp.Filters);
    }

    [Fact]
    public async Task The_version_goes_up_only_when_something_changed()
    {
        var engine = Start();
        await engine.TickAsync();
        var first = engine.Status!.Version;

        _time.Advance(ServiceEngine.TickEvery);
        await engine.TickAsync();
        _time.Advance(ServiceEngine.TickEvery);
        await engine.TickAsync();
        Assert.Equal(first, engine.Status!.Version);
        lock (_published) Assert.Single(_published);

        var reply = await Send(Command.SetGameMode, new ModeArgs(ModeSetting.Off));

        Assert.Equal(first + 1, StatusOf(reply).Version);
        Assert.Equal(first + 1, LastPublished.Version);
    }

    [Fact]
    public async Task A_game_turns_game_mode_on_by_itself()
    {
        var engine = Start();
        _processes.Running.Add(new RunningProcess(10, _game));

        await engine.TickAsync();

        var status = engine.Status!;
        Assert.True(status.GameActive);
        Assert.Equal("eldenring.exe", status.GameName);
        Assert.Equal([_game], status.RunningGames);
        Assert.Contains(GameBlock(RuleKind.App, _updater), _wfp.Rules);
    }

    [Fact]
    public async Task Disposing_removes_every_filter_and_closes_the_session()
    {
        var engine = Start(new AppSettings { Mode = ModeSetting.On });
        await engine.TickAsync();
        Assert.NotEmpty(_wfp.Filters);

        await engine.DisposeAsync();

        Assert.Empty(_wfp.Filters);
        Assert.True(_wfp.Disposed);
        Assert.False((await engine.HandleAsync(Client(), Request(Command.GetStatus), Ct)).Ok); // stopped
    }

    [Fact]
    public async Task Restore_runs_on_request_and_at_stop()
    {
        var hook = new FakeRestorable("Windows download caps") { Problem = "A cap was changed by someone else." };
        var engine = Start(restore: new RestoreHooks([hook], _log));

        var refused = await Send(Command.RestoreEverything, mayChange: false);
        var reply = await Send(Command.RestoreEverything);

        Assert.False(refused.Ok);
        Assert.True(reply.Ok);
        Assert.Contains("A cap was changed by someone else.", StatusOf(reply).Problems);
        Assert.Equal(1, hook.Calls);
        await engine.DisposeAsync();
        Assert.Equal(2, hook.Calls);
    }

    [Theory]
    [InlineData(Command.SetMetered)]
    [InlineData(Command.GetUsage)]
    [InlineData(Command.GetReports)]
    [InlineData(Command.DismissReport)]
    [InlineData(Command.ReportFullscreenGame)]
    [InlineData(Command.SetUploadCap)]
    [InlineData(Command.RemoveUploadCap)]
    public async Task Commands_whose_part_is_not_wired_say_so(Command command)
    {
        Start();

        var reply = await Send(command);

        Assert.Equal((false, "Not available yet."), (reply.Ok, reply.Error));
    }

    [Fact]
    public async Task A_full_list_takes_nothing_more()
    {
        var settings = new AppSettings();
        for (var i = 0; i < IpcLimits.MaxListEntries; i++) settings.AllowApps.Add($@"C:\Apps\app{i}.exe");
        Start(settings);

        var reply = await Send(Command.AllowApp, new PathArgs(_tree.Touch(@"Apps\one-more.exe")));

        Assert.False(reply.Ok);
        Assert.Contains("full", reply.Error);
        Assert.Equal(IpcLimits.MaxListEntries, (await CurrentStatus()).AllowedCount);
    }

    [Fact]
    public async Task A_list_change_that_would_make_the_status_too_large_is_refused()
    {
        // Three lists of long non-ASCII paths: about 1.8 MB of status, past the 1.5 MiB a command may lead to.
        var settings = new AppSettings();
        var folder = @"C:\" + new string('游', 1000);
        for (var i = 0; i < 199; i++)
        {
            settings.ExtraBlocks.Add(new UserBlock($@"{folder}\b{i}.exe", false));
            settings.AlwaysGames.Add($@"{folder}\g{i}.exe");
            settings.NeverGames.Add($@"{folder}\n{i}.exe");
        }
        Start(settings, configure: p => p with { UploadCaps = new FakeUploadCaps() });
        var tool = _tree.Touch(@"Tools\tool.exe");

        Response[] refused =
        [
            await Send(Command.AddBlock, new PathArgs(tool)),
            await Send(Command.AddGame, new PathArgs(tool)),
            await Send(Command.MarkNotAGame, new PathArgs(_tree.Touch(@"Tools\other.exe"))),
            await Send(Command.AllowApp, new PathArgs(tool)),
            await Send(Command.SetHotspot, new HotspotArgs("Phone", true)),
            await Send(Command.SetUploadCap, new UploadCapArgs(tool, 100)),
        ];
        var removed = await Send(Command.RemoveGame, new PathArgs($@"{folder}\g0.exe", IsFolder: true));

        Assert.All(refused, r => Assert.Equal((false, "Too many entries: remove some first."), (r.Ok, r.Error)));
        Assert.True(removed.Ok); // removing always works
        Assert.Equal(198, StatusOf(removed).Settings.AlwaysGames.Count);
    }

    [Fact]
    public async Task Games_lists_and_catalog_follow_the_phase_1_semantics()
    {
        var tool = _tree.Touch(@"Tools\tool.exe");
        Start();

        await Send(Command.AddGame, new PathArgs(tool));
        var marked = StatusOf(await Send(Command.MarkNotAGame, new PathArgs(tool))).Settings; // moves it across
        var toggled = StatusOf(await Send(Command.ToggleCatalog, new IdArgs("NVIDIA-APP"))).Settings;
        var unknown = await Send(Command.ToggleCatalog, new IdArgs("nope"));
        var removed = StatusOf(await Send(Command.RemoveGame, new PathArgs(tool.ToUpperInvariant(), IsFolder: false))).Settings;

        Assert.Equal((0, 1), (marked.AlwaysGames.Count, marked.NeverGames.Count));
        Assert.True(toggled.Catalog.Single(c => c.Id == "nvidia-app").On);
        Assert.Equal((false, "Unknown catalog entry."), (unknown.Ok, unknown.Error));
        Assert.Empty(removed.NeverGames);
    }

    // Task 9: the real Data Saver planner and the other parts, wired as the service wires them.

    private static readonly FilterRule SaverBlockAll = new(RuleKind.All, "", RuleAction.Block, RuleWeight.SaverBlockAll, "");

    private static FilterRule Permit(RuleKind kind, string value, RuleWeight weight = RuleWeight.SaverPermit) => new(kind, value, RuleAction.Permit, weight, "");

    private DataSaverPlanner RealPlanner() => new(_log, _ => true);

    [Fact]
    public async Task Set_data_saver_on_adds_the_block_all_and_off_removes_it()
    {
        Start(saver: RealPlanner());
        await _engine!.TickAsync();
        Assert.Empty(_wfp.Filters);

        var on = StatusOf(await Send(Command.SetDataSaver, new ModeArgs(ModeSetting.On)));

        Assert.Equal((true, "On (manual)"), (on.SaverActive, on.SaverReason));
        Assert.Contains(SaverBlockAll, _wfp.Rules);
        Assert.Contains(Permit(RuleKind.Service, "Dnscache", RuleWeight.EssentialPermit), _wfp.Rules);
        Assert.Contains(Permit(RuleKind.RemotePort, "53", RuleWeight.EssentialPermit), _wfp.Rules);

        var off = StatusOf(await Send(Command.SetDataSaver, new ModeArgs(ModeSetting.Off)));

        Assert.False(off.SaverActive);
        Assert.Empty(_wfp.Filters);
    }

    [Fact]
    public async Task With_data_saver_and_game_mode_on_a_game_block_outweighs_a_saver_permit()
    {
        var chat = _tree.Touch(@"Apps\chat.exe");
        _processes.Running.Add(new RunningProcess(10, _game));
        var engine = Start(new AppSettings { Mode = ModeSetting.On, DataSaver = ModeSetting.On, AllowWindowsDownloads = true, AllowApps = [chat] },
            RealPlanner());

        await engine.TickAsync();

        var rules = _wfp.Rules.ToList();
        Assert.Contains(GameBlock(RuleKind.Service, "DoSvc"), rules); // Windows downloads blocked during the game...
        Assert.Contains(Permit(RuleKind.Service, "DoSvc"), rules);    // ...though Data Saver allows them,
        Assert.True(RuleWeight.GameBlock > RuleWeight.SaverPermit);    // and WFP lets the higher weight decide
        Assert.True(RuleWeight.EssentialPermit > RuleWeight.GameBlock);
        Assert.True(RuleWeight.SaverPermit > RuleWeight.SaverBlockAll);
        Assert.Contains(SaverBlockAll, rules);
        Assert.Contains(Permit(RuleKind.App, _game), rules);          // the running game may connect
        Assert.Contains(Permit(RuleKind.App, chat), rules);
        Assert.Contains(GameBlock(RuleKind.App, _updater), rules);
        Assert.Equal(1, _wfp.Transactions);
        var status = engine.Status!;
        Assert.Equal((true, true, 1), (status.GameActive, status.SaverActive, status.AllowedCount));

        var removed = await Send(Command.RemoveAllowedApp, new PathArgs(chat.ToUpperInvariant()));

        Assert.Equal(0, StatusOf(removed).AllowedCount);
        Assert.DoesNotContain(Permit(RuleKind.App, chat), _wfp.Rules);
    }

    [Fact]
    public async Task Allow_app_adds_a_permit_while_data_saver_is_on()
    {
        var chat = _tree.Touch(@"Apps\chat.exe");
        Start(new AppSettings { DataSaver = ModeSetting.On }, RealPlanner());

        var reply = await Send(Command.AllowApp, new PathArgs(chat));

        Assert.True(reply.Ok);
        Assert.Equal([chat], StatusOf(reply).Settings.AllowApps);
        Assert.Contains(Permit(RuleKind.App, chat), _wfp.Rules);
    }

    [Fact]
    public async Task Restore_everything_calls_every_restorable()
    {
        var caps = new FakeRestorable("Windows download caps");
        var metered = new FakeRestorable("metered Wi-Fi networks") { Problem = "Could not make Cafe unmetered again." };
        Start(restore: new RestoreHooks([caps, metered], _log));

        var reply = await Send(Command.RestoreEverything);

        Assert.True(reply.Ok);
        Assert.Equal((1, 1), (caps.Calls, metered.Calls));
        Assert.Contains("Could not make Cafe unmetered again.", StatusOf(reply).Problems);
    }

    [Fact]
    public async Task Set_metered_goes_through_the_toggle_and_the_status_shows_it()
    {
        var wlan = new FakeWlan();
        wlan.Adapters.Add(new WlanInterface(FakeWlan.A, true));
        wlan.Profiles[FakeWlan.A] = ["Home WiFi"];
        var netsh = new List<string>();
        var toggle = new MeteredToggle(wlan, new PolicyJournal(_tree.Root, _log), _log, args =>
        {
            lock (netsh) netsh.Add(args);
            return 0;
        });
        _network.Current = new NetworkSnapshot(NetworkKind.Wifi, FakeWlan.A, "Home WiFi", ["192.168.1.1"], NetworkCost.Unrestricted);
        Start(configure: p => p with { Metered = new MeteredToggleAdapter(toggle) });

        var before = (await CurrentStatus()).Network;
        var refused = await Send(Command.SetMetered, new OnArgs(true), mayChange: false);
        var marked = await Send(Command.SetMetered, new OnArgs(true));
        var unmarked = await Send(Command.SetMetered, new OnArgs(false));

        Assert.Equal(("wifi", true, false), (before.Kind, before.CanMarkMetered, before.MarkedMeteredByUs));
        Assert.False(refused.Ok);
        Assert.True(marked.Ok);
        Assert.True(StatusOf(marked).Network.MarkedMeteredByUs);
        Assert.False(StatusOf(unmarked).Network.MarkedMeteredByUs);
        lock (netsh) Assert.Equal(["wlan set profileparameter name=\"Home WiFi\" cost=Fixed", "wlan set profileparameter name=\"Home WiFi\" cost=Default"], netsh);
    }

    [Fact]
    public async Task Get_usage_returns_the_meters_top_list()
    {
        var meter = new FakeMeter();
        meter.TopList.AddRange([new UsageDto(@"C:\Apps\chat.exe", "chat.exe", 300, 20), new UsageDto("System", "System", 10, 1)]);
        var engine = Start(configure: p => p with { Meter = meter });
        await engine.TickAsync();

        var reply = await Send(Command.GetUsage);

        Assert.True(reply.Ok);
        Assert.Equal(meter.TopList, reply.Data!.Value.Deserialize(IpcJson.Default.IReadOnlyListUsageDto)); // all of it: the UI's picker keeps full paths
        Assert.Equal(ServiceEngine.UsageCount, meter.LastTopCount);
        Assert.True(meter.Enabled); // follows Settings.TrafficMeter
        await engine.DisposeAsync();
        Assert.True(meter.Disposed);
    }

    [Fact]
    public async Task A_finished_game_session_shows_its_report_until_dismissed()
    {
        var meter = new FakeMeter();
        var chat = Path.Combine(_tree.Root, @"Apps\chat.exe");
        var engine = Start(new AppSettings { GraceMinutes = 0 },
            configure: p => p with { Meter = meter, Reports = new ReportSource(meter, new ReportStore(_tree.Root, _log), _log) });
        _processes.Running.Add(new RunningProcess(10, _game));
        await engine.TickAsync();
        meter.Totals[_game] = 900L * 1024 * 1024;
        meter.Totals[chat] = 40L * 1024 * 1024;
        _processes.Running.Clear();
        _time.Advance(TimeSpan.FromMinutes(30));

        await engine.TickAsync();

        var latest = engine.Status!.LatestReport;
        Assert.NotNull(latest);
        Assert.Equal(("eldenring.exe", 1), (latest.Game, latest.AppCount));
        var all = (await Send(Command.GetReports)).Data!.Value.Deserialize(IpcJson.Default.IReadOnlyListReportDto)!;
        Assert.Equal(chat, Assert.Single(Assert.Single(all).Apps).Path);

        var dismissed = await Send(Command.DismissReport, new IdArgs(latest.Id));
        var unknown = await Send(Command.DismissReport, new IdArgs("nope"));

        Assert.Null(StatusOf(dismissed).LatestReport);
        Assert.Equal((false, "There is no such report."), (unknown.Ok, unknown.Error));
    }

    [Fact]
    public async Task A_wifi_reconnect_during_a_game_wakes_the_engine_to_tune_it_again()
    {
        var wlan = new FakeWlan();
        wlan.Adapters.Add(new WlanInterface(FakeWlan.A, true));
        ServiceEngine? engine = null;
        var wifi = new WifiController(new WifiTuner(wlan, _log), () => engine?.Wake());
        engine = Start(new AppSettings { Mode = ModeSetting.On }, configure: p => p with { Wifi = wifi });
        await engine.TickAsync();
        Assert.Contains("scan A off", wlan.Calls);

        wlan.Callback!(FakeWlan.A);    // a WLAN thread in real life: it only wakes the loop
        await Send(Command.GetStatus); // queued after the woken tick; the fake clock never fires the timer

        Assert.Equal(2, wlan.Calls.Count(c => c == "scan A off"));
        await engine.DisposeAsync();
        Assert.Equal("close", wlan.Calls[^1]);
        engine.Wake();                 // after stop: nothing, and no exception
    }


    // Task 18: games outside Steam, reported by the tray and checked against what the service reads itself.

    private readonly Dictionary<int, ProcessFacts> _lookups = new();

    private EngineParts WithLookup(EngineParts parts) => parts with { ProcessLookup = pid => _lookups.GetValueOrDefault(pid) };

    // A running program as both the process list and the lookup see it. Client() is in session 1.
    private string RunningOutsideSteam(string relative, int pid, int session = 1)
    {
        var path = _tree.Touch(relative);
        _processes.Running.Add(new RunningProcess(pid, path));
        _lookups[pid] = new ProcessFacts(path, session);
        return path;
    }

    [Fact]
    public async Task A_reported_game_turns_game_mode_on_and_may_connect_during_data_saver()
    {
        var indie = RunningOutsideSteam(@"Games\Indie\indie.exe", 77);
        Start(new AppSettings { DataSaver = ModeSetting.On }, RealPlanner(), configure: WithLookup);
        await _engine!.TickAsync();
        Assert.False(_engine.Status!.GameActive);

        var reply = await Send(Command.ReportFullscreenGame, new GameArgs(77, indie.ToUpperInvariant()));

        Assert.True(reply.Ok);
        var status = StatusOf(reply);
        Assert.Equal((ModeSetting.Auto, true, "indie.exe"), (status.GameMode, status.GameActive, status.GameName));
        Assert.Equal([indie], status.RunningGames);
        Assert.Contains(GameBlock(RuleKind.App, _updater), _wfp.Rules);
        Assert.Contains(Permit(RuleKind.App, indie), _wfp.Rules);
        Assert.Empty(status.Settings.AlwaysGames); // never saved
        Assert.Empty(new SettingsStore(SettingsPath).Load().AlwaysGames);
    }

    [Theory]
    [InlineData("another session", "another user's session")]
    [InlineData("another program", "different program")]
    [InlineData("windows", "part of Windows")]
    [InlineData("protected", "can't be a game")]
    [InlineData("not a game", "not-a-game list")]
    [InlineData("inactive session", "Only the active user")]
    [InlineData("exited", "not running")]
    [InlineData("network path", "local drive")]
    public async Task A_report_that_does_not_check_out_is_refused(string problem, string error)
    {
        var indie = RunningOutsideSteam(@"Games\Indie\indie.exe", 77);
        var other = _tree.Touch(@"Games\Other\other.exe");
        var discord = RunningOutsideSteam(@"Apps\Discord\Discord.exe", 78);
        var svchost = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "svchost.exe");
        _processes.Running.Add(new RunningProcess(79, svchost));
        _lookups[79] = new ProcessFacts(svchost, 1);
        var settings = new AppSettings { GraceMinutes = 0 };
        var (args, mayChange) = problem switch
        {
            "another session" => (new GameArgs(77, indie), true),
            "another program" => (new GameArgs(77, other), true),
            "windows" => (new GameArgs(79, svchost), true),
            "protected" => (new GameArgs(78, discord), true),
            "not a game" => (new GameArgs(77, indie), true),
            "inactive session" => (new GameArgs(77, indie), false),
            "exited" => (new GameArgs(99, indie), true),
            _ => (new GameArgs(77, @"\\server\games\indie.exe"), true),
        };
        if (problem == "another session") _lookups[77] = new ProcessFacts(indie, 2);
        if (problem == "not a game") settings.NeverGames.Add(indie);
        Start(settings, configure: WithLookup);

        var reply = await Send(Command.ReportFullscreenGame, args, mayChange);

        Assert.False(reply.Ok);
        Assert.Contains(error, reply.Error);
        var status = await CurrentStatus();
        Assert.Equal((false, 0), (status.GameActive, status.RunningGames.Count));
    }

    [Fact]
    public async Task A_release_report_or_the_process_ending_removes_the_game()
    {
        var indie = RunningOutsideSteam(@"Games\Indie\indie.exe", 77);
        var other = RunningOutsideSteam(@"Games\Other\other.exe", 78);
        var engine = Start(new AppSettings { GraceMinutes = 0 }, configure: WithLookup);
        await Send(Command.ReportFullscreenGame, new GameArgs(77, indie));
        Assert.Equal([indie, other], StatusOf(await Send(Command.ReportFullscreenGame, new GameArgs(78, other))).RunningGames);

        var released = StatusOf(await Send(Command.ReportFullscreenGame, new GameArgs(0, indie.ToUpperInvariant())));
        Assert.Equal([other], released.RunningGames); // still running, but no longer a game

        _processes.Running.RemoveAll(p => p.Pid == 78);
        _processes.Running.Add(new RunningProcess(78, indie)); // the pid reused by another program
        await engine.TickAsync();
        Assert.Equal((false, 0), (engine.Status!.GameActive, engine.Status.RunningGames.Count));

        _processes.Running.Add(new RunningProcess(80, other)); // started again: a game only once reported again
        await engine.TickAsync();
        Assert.Empty(engine.Status!.RunningGames);
    }

    [Fact]
    public async Task At_most_four_games_outside_steam_count_and_the_oldest_goes_first()
    {
        var games = Enumerable.Range(0, 5).Select(i => RunningOutsideSteam($@"Games\G{i}\g{i}.exe", 100 + i)).ToList();
        Start(configure: WithLookup);

        StatusDto? status = null;
        for (var i = 0; i < games.Count; i++)
        {
            var reply = await Send(Command.ReportFullscreenGame, new GameArgs(100 + i, games[i]));
            Assert.True(reply.Ok);
            status = StatusOf(reply);
        }

        Assert.Equal(ServiceEngine.MaxTransientGames, games.Count - 1);
        Assert.Equal(games[1..], status!.RunningGames);
    }

    [Fact]
    public async Task A_steam_game_or_a_second_report_adds_nothing()
    {
        _processes.Running.Add(new RunningProcess(10, _game));
        _lookups[10] = new ProcessFacts(_game, 1);
        var indie = RunningOutsideSteam(@"Games\Indie\indie.exe", 77);
        var engine = Start(configure: WithLookup);
        await engine.TickAsync(); // finds the Steam library

        var steam = await Send(Command.ReportFullscreenGame, new GameArgs(10, _game));
        await Send(Command.ReportFullscreenGame, new GameArgs(77, indie));
        var again = await Send(Command.ReportFullscreenGame, new GameArgs(77, indie));

        Assert.True(steam.Ok);
        Assert.Equal([_game, indie], StatusOf(again).RunningGames);
        Assert.Single(_log.Lines, l => l.Contains("Game outside Steam: ", StringComparison.Ordinal)); // only indie, once
    }

    [Fact]
    public async Task Turning_detection_off_drops_the_games_it_found_and_refuses_new_ones()
    {
        var indie = RunningOutsideSteam(@"Games\Indie\indie.exe", 77);
        Start(new AppSettings { GraceMinutes = 0 }, configure: WithLookup);
        Assert.True(StatusOf(await Send(Command.ReportFullscreenGame, new GameArgs(77, indie))).GameActive);

        var off = StatusOf(await Send(Command.SetOptions, Options(grace: 0) with { DetectNonSteamGames = false }));
        var refused = await Send(Command.ReportFullscreenGame, new GameArgs(77, indie));

        Assert.Equal((false, 0), (off.GameActive, off.RunningGames.Count));
        Assert.Equal((false, "Detecting games outside Steam is turned off."), (refused.Ok, refused.Error));
    }

    // Task 19: upload caps, applied at start and on each change, removed at stop. Windows QoS can be slow, so each call
    // runs on the engine's upload worker; UploadCapsSettledAsync waits for it and for its result to reach the status.

    private string Backup => _tree.Touch(@"Apps\backup.exe");

    [Fact]
    public async Task Upload_caps_are_cleared_then_applied_at_start_and_cleared_at_stop()
    {
        var caps = new FakeUploadCaps();
        var engine = Start(new AppSettings { UploadCaps = [new UploadCapArgs(Backup, 100)] }, configure: p => p with { UploadCaps = caps });
        await engine.TickAsync();
        await engine.TickAsync();
        await engine.UploadCapsSettledAsync();

        Assert.Equal(["remove all", "apply 1 on"], caps.Calls); // not on every tick

        await engine.DisposeAsync();
        Assert.Equal("remove all", caps.Calls[^1]);
    }

    [Fact]
    public async Task Upload_caps_go_to_windows_off_the_loop_and_only_the_newest_request_is_applied()
    {
        using var hold = new ManualResetEventSlim();
        var caps = new FakeUploadCaps { Hold = hold };
        var backup = Backup;
        var engine = Start(configure: p => p with { UploadCaps = caps });
        Assert.True(await caps.Applying.WaitAsync(TimeSpan.FromSeconds(5))); // the start-up apply is stuck in Windows QoS

        await engine.TickAsync().WaitAsync(TimeSpan.FromSeconds(5)); // the loop carries on meanwhile
        foreach (var kbps in new[] { 100, 200, 300 })
            Assert.True((await Send(Command.SetUploadCap, new UploadCapArgs(backup, kbps)).WaitAsync(TimeSpan.FromSeconds(5))).Ok);
        hold.Set();
        await engine.UploadCapsSettledAsync();

        Assert.Equal(["remove all", "apply 0 on", "apply 1 on"], caps.Calls); // 100 and 200 were overtaken before they started
        Assert.Equal(300, Assert.Single(caps.LastCaps).KBps);
    }

    [Fact]
    public async Task Set_upload_cap_checks_the_path_and_the_rate_and_replaces_the_cap_for_the_same_program()
    {
        var caps = new FakeUploadCaps();
        var backup = Backup;
        var engine = Start(configure: p => p with { UploadCaps = caps });

        var network = await Send(Command.SetUploadCap, new UploadCapArgs(@"\\server\share\backup.exe", 100));
        var tooSlow = await Send(Command.SetUploadCap, new UploadCapArgs(backup, UploadCapManager.MinKBps - 1));
        var tooFast = await Send(Command.SetUploadCap, new UploadCapArgs(backup, UploadCapManager.MaxKBps + 1));
        var folder = await Send(Command.SetUploadCap, new UploadCapArgs(Path.GetDirectoryName(backup)!, 100));
        var inactive = await Send(Command.SetUploadCap, new UploadCapArgs(backup, 100), mayChange: false);
        await engine.UploadCapsSettledAsync();
        var callsBefore = caps.Calls.Count;
        var set = await Send(Command.SetUploadCap, new UploadCapArgs(backup, UploadCapManager.MinKBps));
        await engine.UploadCapsSettledAsync();
        var replaced = await Send(Command.SetUploadCap, new UploadCapArgs(backup.ToUpperInvariant(), UploadCapManager.MaxKBps));
        await engine.UploadCapsSettledAsync();

        Assert.All(new[] { network, tooSlow, tooFast, folder, inactive }, r => Assert.False(r.Ok));
        Assert.Contains("local drive", network.Error);
        Assert.Equal("An upload cap must be 8 to 1,000,000 KB/s.", tooSlow.Error);
        Assert.Equal(tooSlow.Error, tooFast.Error);
        Assert.Equal("Pick a program, not a folder.", folder.Error);
        Assert.Equal([new UploadCapArgs(backup, UploadCapManager.MinKBps)], StatusOf(set).Settings.UploadCaps);
        var cap = Assert.Single(StatusOf(replaced).Settings.UploadCaps);
        Assert.Equal((backup, UploadCapManager.MaxKBps), (cap.Path, cap.KBps), new PathAndRate());
        Assert.Equal(["apply 1 on", "apply 1 on"], caps.Calls.Skip(callsBefore));
        Assert.Equal([cap], caps.LastCaps);
        Assert.Equal([cap], new SettingsStore(SettingsPath).Load().UploadCaps);
    }

    [Fact]
    public async Task The_upload_cap_list_is_capped_but_a_capped_program_can_still_change()
    {
        var settings = new AppSettings();
        for (var i = 0; i < UploadCapManager.MaxCaps; i++) settings.UploadCaps.Add(new UploadCapArgs($@"C:\Apps\app{i}.exe", 100));
        Start(settings, configure: p => p with { UploadCaps = new FakeUploadCaps() });

        var more = await Send(Command.SetUploadCap, new UploadCapArgs(Backup, 100));
        var change = await Send(Command.SetUploadCap, new UploadCapArgs(@"C:\Apps\app7.exe", 500));

        Assert.False(more.Ok);
        Assert.Contains($"At most {UploadCapManager.MaxCaps} apps", more.Error);
        var list = StatusOf(change).Settings.UploadCaps;
        Assert.Equal(UploadCapManager.MaxCaps, list.Count);
        Assert.Equal(new UploadCapArgs(@"C:\Apps\app7.exe", 500), list[7]);
    }

    [Fact]
    public async Task Removing_a_cap_or_switching_caps_off_applies_again_and_other_options_do_not()
    {
        var caps = new FakeUploadCaps();
        var backup = Backup;
        var engine = Start(new AppSettings { UploadCaps = [new UploadCapArgs(backup, 100)] }, configure: p => p with { UploadCaps = caps });
        await engine.TickAsync();
        await engine.UploadCapsSettledAsync();

        var off = await Send(Command.SetOptions, Options() with { UploadCapsEnabled = false });
        await engine.UploadCapsSettledAsync();
        await Send(Command.SetOptions, Options(grace: 5) with { UploadCapsEnabled = false });
        await engine.UploadCapsSettledAsync();
        var removed = await Send(Command.RemoveUploadCap, new PathArgs(backup.ToUpperInvariant()));
        await engine.UploadCapsSettledAsync();

        Assert.False(StatusOf(off).Settings.Options.UploadCapsEnabled);
        Assert.Empty(StatusOf(removed).Settings.UploadCaps);
        Assert.Equal(["remove all", "apply 1 on", "apply 1 off", "apply 0 off"], caps.Calls);
        Assert.Empty(new SettingsStore(SettingsPath).Load().UploadCaps);
    }

    [Fact]
    public async Task An_upload_cap_problem_shows_until_a_change_works()
    {
        var caps = new FakeUploadCaps { Problem = "Could not apply the upload caps: Windows QoS failed." };
        var engine = Start(configure: p => p with { UploadCaps = caps });
        await engine.UploadCapsSettledAsync();
        Assert.Contains("Could not apply the upload caps: Windows QoS failed.", engine.Status!.Problems);

        caps.Problem = null;
        await Send(Command.SetUploadCap, new UploadCapArgs(Backup, 100));
        await engine.UploadCapsSettledAsync();

        Assert.DoesNotContain(engine.Status!.Problems, p => p.Contains("upload caps", StringComparison.Ordinal));
    }

    // H2: a stuck engine loop must not keep the filters in place.

    [Fact]
    public async Task A_task_stuck_on_the_loop_trips_the_watchdog_and_a_long_one_does_not()
    {
        var clock = new FakeTimeProvider();
        var stalls = new List<string>();
        var caps = new StuckCaps();
        var engine = Start(configure: p => p with
        {
            Caps = caps,
            Watchdog = new EngineWatchdog(TimeSpan.FromSeconds(90), message => { lock (stalls) stalls.Add(message); }, clock),
        });
        await engine.TickAsync();
        clock.Advance(TimeSpan.FromMinutes(5)); // an idle loop is not a stuck one

        caps.Stuck = true;
        var stuck = engine.TickAsync();
        Assert.True(await caps.Entered.WaitAsync(TimeSpan.FromSeconds(5)));
        clock.Advance(TimeSpan.FromSeconds(80));
        lock (stalls) Assert.Empty(stalls);
        clock.Advance(TimeSpan.FromSeconds(20));

        lock (stalls) Assert.Equal(["Connection Manager engine stalled"], stalls);
        Assert.Contains(_log.Lines, l => l.StartsWith("ERROR Connection Manager engine stalled", StringComparison.Ordinal));
        caps.Release.Set();
        await stuck.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class StuckCaps : ICapController
    {
        public ManualResetEventSlim Release { get; } = new();
        public SemaphoreSlim Entered { get; } = new(0);
        public bool Stuck { get; set; }

        public string? Update(TickContext context)
        {
            if (!Stuck) return null;
            Entered.Release();
            Release.Wait();
            return null;
        }
    }

    private sealed class FakeUploadCaps : IUploadCaps
    {
        private readonly object _gate = new();

        public List<string> Calls { get; } = [];
        public List<UploadCapArgs> LastCaps { get; private set; } = [];
        public string? Problem { get; set; }

        /// When set, each Apply waits for it, like a slow Windows QoS.
        public ManualResetEventSlim? Hold { get; init; }
        public SemaphoreSlim Applying { get; } = new(0);

        public string? Apply(AppSettings settings)
        {
            Applying.Release();
            Hold?.Wait();
            lock (_gate)
            {
                Calls.Add($"apply {settings.UploadCaps.Count} {(settings.UploadCapsEnabled ? "on" : "off")}");
                LastCaps = settings.UploadCaps.ToList();
            }
            return Problem;
        }

        public string? RemoveAll()
        {
            lock (_gate) Calls.Add("remove all");
            return null;
        }
    }

    private sealed class PathAndRate : IEqualityComparer<(string Path, int KBps)>
    {
        public bool Equals((string Path, int KBps) x, (string Path, int KBps) y) =>
            string.Equals(x.Path, y.Path, StringComparison.OrdinalIgnoreCase) && x.KBps == y.KBps;

        public int GetHashCode((string Path, int KBps) obj) => obj.KBps;
    }

    private sealed class FakeNetwork : INetworkSource
    {
        public NetworkSnapshot Current { get; set; } = new(NetworkKind.Ethernet, Guid.NewGuid(), "Home", ["192.168.1.1"], NetworkCost.Unrestricted);

        public bool Refresh(DateTimeOffset now) => false;
    }

    private sealed class FakePlanner(params FilterRule[] rules) : IDataSaverPlanner
    {
        public TickContext? Last { get; private set; }

        public IReadOnlySet<FilterRule> Plan(TickContext context)
        {
            Last = context;
            return rules.ToHashSet();
        }
    }

    private sealed class FakeRestorable(string name) : IRestorable
    {
        public string Name { get; } = name;
        public string? Problem { get; init; }
        public int Calls { get; private set; }

        public string? Restore()
        {
            Calls++;
            return Problem;
        }
    }
}
