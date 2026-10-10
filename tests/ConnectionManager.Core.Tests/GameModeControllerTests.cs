using ConnectionManager.Core.Modes;
using ConnectionManager.Core.Processes;
using ConnectionManager.Core.Settings;
using ConnectionManager.Core.Wfp;
using Microsoft.Extensions.Time.Testing;

namespace ConnectionManager.Core.Tests;

public sealed class GameModeControllerTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 8, 20, 0, 0, TimeSpan.Zero));
    private readonly FakeProcessSource _processes = new();
    private readonly FakeWfpEngine _engine = new();
    private readonly ListLog _log = new();
    private readonly string _game;
    private readonly string _updater;
    private readonly GameModeController _controller;

    public GameModeControllerTests()
    {
        _game = _tree.Touch(@"Steam\steamapps\common\Elden Ring\eldenring.exe");
        _updater = _tree.Touch(@"x86\Google\GoogleUpdater\1.0\updater.exe");
        _controller = NewController(_engine);
    }

    private GameModeController NewController(IWfpEngine? engine)
    {
        var steam = Path.Combine(_tree.Root, "Steam");
        // Only %ProgramFiles(x86)% points somewhere; every other catalog variable stays unset and matches nothing.
        var controller = new GameModeController(_processes, engine, _time, _log, () => steam, _ => null, Path.Combine(_tree.Root, "App"),
            text => text.Replace("%ProgramFiles(x86)%", Path.Combine(_tree.Root, "x86")));
        controller.ApplySettings(new AppSettings());
        return controller;
    }

    private IEnumerable<FilterRule> Blocked => _engine.Rules;

    private static FilterRule GameBlock(RuleKind kind, string value) => new(kind, value, RuleAction.Block, RuleWeight.GameBlock, "");

    private void Run(params string[] paths)
    {
        _processes.Running.Clear();
        _processes.Running.AddRange(paths.Select((p, i) => new RunningProcess(100 + i, p)));
    }

    [Fact]
    public void Nothing_is_blocked_without_a_game()
    {
        var status = _controller.Tick();

        Assert.False(status.Active);
        Assert.Empty(_engine.Filters);
    }

    [Fact]
    public void Starting_a_game_blocks_the_updaters_and_windows_update()
    {
        Run(_game);

        var status = _controller.Tick();

        Assert.True(status.Active);
        Assert.Equal("eldenring.exe", status.Game.GameName);
        Assert.Contains(GameBlock(RuleKind.App, _updater), Blocked);
        Assert.Contains(GameBlock(RuleKind.Service, "DoSvc"), Blocked);
        Assert.Equal(4, status.BlockedCount); // updater.exe + wuauserv, BITS, DoSvc
    }

    [Fact]
    public void Blocks_end_after_the_grace_period()
    {
        Run(_game);
        _controller.Tick();
        Run();
        Assert.True(_controller.Tick().Active);

        _time.Advance(TimeSpan.FromMinutes(2));
        var status = _controller.Tick();

        Assert.False(status.Active);
        Assert.Empty(_engine.Filters);
    }

    [Fact]
    public void Manual_on_and_off_override_detection()
    {
        _controller.ApplySettings(new AppSettings { Mode = ModeSetting.On });
        Assert.True(_controller.Tick().Active);

        Run(_game);
        _controller.ApplySettings(new AppSettings { Mode = ModeSetting.Off });

        Assert.False(_controller.Tick().Active);
        Assert.Empty(_engine.Filters);
    }

    [Fact]
    public void Turning_a_catalog_entry_off_mid_game_unblocks_it()
    {
        Run(_game);
        _controller.Tick();

        _controller.ApplySettings(new AppSettings { Catalog = { ["google-updater"] = false } });
        _controller.Tick();

        Assert.DoesNotContain(GameBlock(RuleKind.App, _updater), Blocked);
    }

    [Fact]
    public void A_new_updater_version_is_picked_up_within_a_minute()
    {
        Run(_game);
        _controller.Tick();
        var newer = _tree.Touch(@"x86\Google\GoogleUpdater\2.0\updater.exe");

        _time.Advance(TimeSpan.FromSeconds(30));
        _controller.Tick();
        Assert.DoesNotContain(GameBlock(RuleKind.App, newer), Blocked);

        _time.Advance(TimeSpan.FromSeconds(30));
        _controller.Tick();
        Assert.Contains(GameBlock(RuleKind.App, newer), Blocked);
    }

    [Fact]
    public void Your_blocks_never_include_the_game_itself()
    {
        Run(_game);
        _controller.ApplySettings(new AppSettings { ExtraBlocks = { new UserBlock(_game, false) } });

        _controller.Tick();

        Assert.DoesNotContain(GameBlock(RuleKind.App, _game), Blocked);
    }

    [Fact]
    public void A_target_that_fails_is_logged_once_and_the_rest_still_apply()
    {
        _engine.FailFor.Add("BITS");
        Run(_game);
        _controller.Tick();
        _time.Advance(GameModeController.RefreshWhileActive);
        _controller.Tick();

        Assert.Single(_log.Lines, l => l.Contains("Could not block BITS"));
        Assert.Contains(GameBlock(RuleKind.App, _updater), Blocked);
    }

    [Fact]
    public void A_lasting_failure_is_retried_once_a_minute_and_logged_once()
    {
        Run(_game);
        _engine.FailCommit = true;

        Assert.NotNull(_controller.Tick().Error);
        var attempts = _engine.Transactions;

        _time.Advance(TimeSpan.FromSeconds(2));
        _controller.Tick();
        Assert.Equal(attempts, _engine.Transactions); // no retry yet

        _time.Advance(GameModeController.RefreshWhileActive);
        _controller.Tick();
        Assert.Equal(attempts + 1, _engine.Transactions);
        Assert.Single(_log.Lines, l => l.StartsWith("ERROR"));

        _engine.FailCommit = false;
        _time.Advance(GameModeController.RefreshWhileActive);
        var status = _controller.Tick();
        Assert.Null(status.Error);
        Assert.Equal(4, status.BlockedCount);
    }

    [Fact]
    public void A_settings_change_retries_at_once_after_a_failure()
    {
        Run(_game);
        _engine.FailCommit = true;
        _controller.Tick();
        _engine.FailCommit = false;

        _controller.ApplySettings(new AppSettings());
        var status = _controller.Tick();

        Assert.Null(status.Error);
        Assert.Equal(4, status.BlockedCount);
    }

    [Fact]
    public void A_failing_process_list_is_reported_without_crashing()
    {
        Run(_game);
        _controller.Tick();
        _processes.Fail = true;

        var status = _controller.Tick();

        Assert.NotNull(status.Error);
        Assert.True(status.Active); // the last known state stays until detection works again
        Assert.Equal(4, status.BlockedCount);
    }

    [Fact]
    public void Your_folder_blocks_skip_steam()
    {
        var steamExe = _tree.Touch(@"Steam\steam.exe");
        _controller.ApplySettings(new AppSettings { Mode = ModeSetting.On, ExtraBlocks = { new UserBlock(Path.Combine(_tree.Root, "Steam"), true) } });

        _controller.Tick();

        Assert.DoesNotContain(GameBlock(RuleKind.App, steamExe), Blocked);
        Assert.DoesNotContain(GameBlock(RuleKind.App, _game), Blocked);
    }

    [Fact]
    public void Picks_that_would_never_be_blocked_are_protected()
    {
        _controller.ApplySettings(new AppSettings { AlwaysGames = { @"C:\Games\x.exe" } });
        _controller.Tick(); // finds the Steam libraries

        Assert.True(_controller.IsProtected(new UserBlock(_game, false)));
        Assert.True(_controller.IsProtected(new UserBlock(@"C:\Games\x.exe", false)));
        Assert.True(_controller.IsProtected(new UserBlock(@"D:\Anywhere\Discord.exe", false)));
        Assert.True(_controller.IsProtected(new UserBlock(Path.Combine(_tree.Root, "Steam"), true)));
        Assert.True(_controller.IsProtected(new UserBlock(Path.Combine(_tree.Root, @"Steam\steamapps\common\Elden Ring"), true)));
        Assert.True(_controller.IsProtected(new UserBlock(Path.Combine(_tree.Root, @"App\"), true)));
        Assert.True(_controller.IsProtected(new UserBlock(Environment.GetFolderPath(Environment.SpecialFolder.Windows), true)));
    }

    [Fact]
    public void Ordinary_picks_are_not_protected()
    {
        _controller.Tick();

        Assert.False(_controller.IsProtected(new UserBlock(_updater, false)));
        Assert.False(_controller.IsProtected(new UserBlock(Path.Combine(_tree.Root, "x86"), true)));
        Assert.False(_controller.IsProtected(new UserBlock(_tree.Root, true))); // holds Steam, but its other programs are still blocked
        Assert.False(_controller.IsProtected(new UserBlock("relative.exe", false)));
    }

    [Fact]
    public void Dispose_removes_the_blocks_and_closes_the_engine()
    {
        Run(_game);
        _controller.Tick();

        _controller.Dispose();

        Assert.Empty(_engine.Filters);
        Assert.True(_engine.Disposed);
    }

    [Fact]
    public void Wanted_rules_are_the_game_blocks_while_game_mode_is_on()
    {
        Run(_game);
        _controller.Tick();

        Assert.True(_controller.WantedRules.SetEquals(_engine.Rules));
        Assert.Contains(GameBlock(RuleKind.Service, "BITS"), _controller.WantedRules);
        Assert.All(_controller.WantedRules, r => Assert.Equal((RuleAction.Block, RuleWeight.GameBlock), (r.Action, r.Weight)));

        Run();
        _controller.Tick();
        _time.Advance(TimeSpan.FromMinutes(2));
        _controller.Tick();

        Assert.Empty(_controller.WantedRules);
    }

    [Fact]
    public void Planning_works_while_game_mode_is_off()
    {
        _controller.Tick();

        var plan = _controller.PlanGameBlocks();

        Assert.Contains(GameBlock(RuleKind.App, _updater), plan);
        Assert.Empty(_controller.WantedRules);
        Assert.Empty(_engine.Filters);
    }

    [Fact]
    public void Without_an_engine_it_only_plans()
    {
        using var planner = NewController(engine: null);
        Run(_game);

        var status = planner.Tick();

        Assert.True(status.Active);
        Assert.Equal(4, status.BlockedCount);
        Assert.Contains(GameBlock(RuleKind.App, _updater), planner.WantedRules);
        Assert.Empty(_engine.Filters);
    }

    [Fact]
    public void A_rejected_path_is_logged_once()
    {
        var settings = new AppSettings { Mode = ModeSetting.On };
        settings.AddBlock(new UserBlock(@"\\server\share\tool.exe", false));
        _controller.ApplySettings(settings);

        _controller.Tick();
        _time.Advance(GameModeController.RefreshWhileActive);
        _controller.Tick(); // resolves again

        Assert.Single(_log.Lines, l => l.StartsWith("WARN Not blocked", StringComparison.Ordinal) && l.Contains(@"\\server\share\tool.exe"));
    }

    [Fact]
    public void Exposes_the_steam_folders_it_found()
    {
        _controller.Tick();

        Assert.Equal(Path.Combine(_tree.Root, "Steam"), _controller.SteamPath);
        Assert.Equal([Path.Combine(_tree.Root, @"Steam\steamapps\common")], _controller.SteamLibraryFolders);
    }

    public void Dispose() => _tree.Dispose();
}
