using ConnectionManager.Core.Caps;
using ConnectionManager.Core.Engine;
using ConnectionManager.Core.Games;
using ConnectionManager.Core.Modes;
using ConnectionManager.Core.Network;
using ConnectionManager.Core.Reports;
using ConnectionManager.Core.Settings;
using ConnectionManager.Core.Wifi;
using Microsoft.Win32;
using static ConnectionManager.Core.Tests.FakeWlan;

namespace ConnectionManager.Core.Tests;

public sealed class EngineAdapterTests : IDisposable
{
    private const long MB = 1024 * 1024;
    private const string Key = DoCapManager.PolicyKey;
    private const string Background = "DOMaxBackgroundDownloadBandwidth";
    private const string Foreground = "DOMaxForegroundDownloadBandwidth";
    private const string Home = "Home WiFi";
    private static readonly DateTimeOffset T0 = new(2026, 10, 9, 20, 0, 0, TimeSpan.Zero);

    private readonly TempTree _tree = new();
    private readonly ListLog _log = new();
    private readonly FakeRegistryStore _registry = new();
    private readonly FakeWlan _wlan = new();
    private readonly List<string> _netsh = [];
    private int _refreshes;

    public void Dispose() => _tree.Dispose();

    private static TickContext Context(DateTimeOffset now, AppSettings? settings = null, bool saverActive = false, ControllerStatus? game = null,
        string? steam = null, IReadOnlyList<string>? libraries = null, Func<string, bool>? isProtected = null, string? serviceExe = null) =>
        new(now, settings ?? new AppSettings(), NetworkSnapshot.None, [], game ?? ControllerStatus.Starting, saverActive, steam, libraries ?? [],
            isProtected ?? (_ => false), serviceExe);

    private static ControllerStatus Playing(params string[] games) =>
        new(true, ModeSetting.Auto, new GameState(true, Path.GetFileName(games.FirstOrDefault()) ?? "game", null), 0, games, null);

    private static ControllerStatus Idle => ControllerStatus.Starting;

    private static RegistryValue Dword(uint value) => new(RegistryValueKind.DWord, value);

    // Windows download caps

    private DoCapManager NewCaps() => new(_registry, new PolicyJournal(_tree.Root, _log), _log, () => _refreshes++);

    private static AppSettings Downloads(bool allowed = true, int kbps = 200) => new() { AllowWindowsDownloads = allowed, WindowsDownloadCapKBps = kbps };

    [Fact]
    public void Caps_are_applied_once_on_the_rising_edge_and_restored_once_on_the_falling_edge()
    {
        var controller = new DoCapController(NewCaps());

        Assert.Null(controller.Update(Context(T0, Downloads(), saverActive: false)));
        Assert.Equal(0, _refreshes);

        Assert.Null(controller.Update(Context(T0.AddSeconds(2), Downloads(), saverActive: true)));
        Assert.Null(controller.Update(Context(T0.AddSeconds(4), Downloads(), saverActive: true)));
        Assert.Equal((Dword(200), Dword(200), 1), (_registry.Read(Key, Background), _registry.Read(Key, Foreground), _refreshes));

        Assert.Null(controller.Update(Context(T0.AddSeconds(6), Downloads(), saverActive: false)));
        Assert.Null(controller.Update(Context(T0.AddSeconds(8), Downloads(), saverActive: false)));
        Assert.False(_registry.KeyExists(Key));
        Assert.Equal(2, _refreshes);
    }

    [Fact]
    public void Caps_are_wanted_only_while_windows_downloads_are_allowed()
    {
        var controller = new DoCapController(NewCaps());

        controller.Update(Context(T0, Downloads(allowed: false), saverActive: true));
        Assert.False(_registry.KeyExists(Key));

        controller.Update(Context(T0.AddSeconds(2), Downloads(allowed: true), saverActive: true));
        Assert.Equal(Dword(200), _registry.Read(Key, Background));

        controller.Update(Context(T0.AddSeconds(4), Downloads(allowed: false), saverActive: true));
        Assert.False(_registry.KeyExists(Key));
    }

    [Fact]
    public void A_new_cap_while_active_restores_then_applies_it()
    {
        var controller = new DoCapController(NewCaps());
        controller.Update(Context(T0, Downloads(kbps: 200), saverActive: true));

        controller.Update(Context(T0.AddSeconds(2), Downloads(kbps: 500), saverActive: true));

        Assert.Equal((Dword(500), Dword(500)), (_registry.Read(Key, Background), _registry.Read(Key, Foreground)));
        Assert.Equal(3, _refreshes); // apply, restore, apply
        Assert.All(new PolicyJournal(_tree.Root, _log).Load().Entries.OfType<RegistryEntry>(), e => Assert.Equal(500u, e.WrittenData));
    }

    [Fact]
    public void Caps_put_back_by_restore_everything_are_applied_again_while_still_wanted()
    {
        var caps = NewCaps();
        var controller = new DoCapController(caps);
        controller.Update(Context(T0, Downloads(), saverActive: true));

        caps.Restore(); // the "Windows download caps" restore hook
        Assert.False(_registry.KeyExists(Key));
        controller.Update(Context(T0.AddSeconds(2), Downloads(), saverActive: true));

        Assert.Equal(Dword(200), _registry.Read(Key, Background));
    }

    [Fact]
    public void Caps_a_policy_already_sets_are_not_retried_every_tick()
    {
        _registry.Set(Key, Background, Dword(50));
        _registry.Set(Key, Foreground, Dword(50));
        var controller = new DoCapController(NewCaps());

        controller.Update(Context(T0, Downloads(), saverActive: true));
        controller.Update(Context(T0.AddSeconds(2), Downloads(), saverActive: true));

        Assert.Single(_log.Lines, l => l.Contains(Background, StringComparison.Ordinal));
        Assert.Equal(Dword(50), _registry.Read(Key, Background));
        Assert.Equal(0, _refreshes);
    }

    [Fact]
    public void A_failed_apply_is_reported_and_tried_again_a_minute_later()
    {
        _registry.FailWritesAfter = 0;
        var controller = new DoCapController(NewCaps());

        var problem = controller.Update(Context(T0, Downloads(), saverActive: true));
        _registry.FailWritesAfter = int.MaxValue;
        var meanwhile = controller.Update(Context(T0.AddSeconds(2), Downloads(), saverActive: true));

        Assert.Contains("Could not limit Windows downloads", problem);
        Assert.Equal(problem, meanwhile);
        Assert.Null(_registry.Read(Key, Background));

        Assert.Null(controller.Update(Context(T0 + DoCapController.RetryAfter, Downloads(), saverActive: true)));
        Assert.Equal(Dword(200), _registry.Read(Key, Background));
    }

    // Metered toggle

    private MeteredToggleAdapter NewMetered()
    {
        _wlan.Adapters.Add(new WlanInterface(A, true));
        _wlan.Profiles[A] = [Home];
        return new MeteredToggleAdapter(new MeteredToggle(_wlan, new PolicyJournal(_tree.Root, _log), _log, args =>
        {
            _netsh.Add(args);
            return 0;
        }));
    }

    private static NetworkSnapshot Wifi(string name = Home) => new(NetworkKind.Wifi, A, name, ["192.168.1.1"], NetworkCost.Unrestricted);

    private static NetworkSnapshot Ethernet => new(NetworkKind.Ethernet, Guid.NewGuid(), "Home", ["192.168.1.1"], NetworkCost.Unrestricted);

    [Fact]
    public void The_metered_toggle_marks_and_unmarks_the_current_wifi()
    {
        var metered = NewMetered();
        Assert.Equal((false, false), metered.Describe(Ethernet));
        Assert.Equal((true, false), metered.Describe(Wifi()));

        Assert.Null(metered.Set(Wifi(), on: true));
        Assert.Equal((true, true), metered.Describe(Wifi()));

        Assert.Null(metered.Set(Wifi(), on: false));
        Assert.Equal((true, false), metered.Describe(Wifi()));
        Assert.Equal([$"wlan set profileparameter name=\"{Home}\" cost=Fixed", $"wlan set profileparameter name=\"{Home}\" cost=Default"], _netsh);
    }

    [Fact]
    public void The_metered_toggle_explains_what_it_cannot_do()
    {
        var metered = NewMetered();

        Assert.Equal("Only a Wi-Fi network can be marked as metered.", metered.Set(Ethernet, on: true));
        Assert.Contains("did not mark", metered.Set(Wifi(), on: false));
        Assert.Contains("could not find the Wi-Fi profile", metered.Set(Wifi("Cafe"), on: false));
        Assert.Empty(_netsh);
    }

    // Wi-Fi scan pause

    [Fact]
    public void Wifi_tuning_follows_game_mode_and_the_setting()
    {
        _wlan.Adapters.Add(new WlanInterface(A, true));
        using var wifi = new WifiController(new WifiTuner(_wlan, _log), wake: null);

        wifi.Update(Context(T0, game: Idle));
        Assert.Empty(_wlan.Calls);

        wifi.Update(Context(T0, game: Playing(@"C:\Games\game.exe")));
        Assert.Equal(["open", "register", "interfaces", "scan A off", "streaming A on"], _wlan.Calls);

        _wlan.Calls.Clear();
        wifi.Update(Context(T0, new AppSettings { WifiTuning = false }, game: Playing(@"C:\Games\game.exe")));
        Assert.Equal(["unregister", "scan A on", "streaming A off", "close"], _wlan.Calls);

        _wlan.Calls.Clear();
        wifi.Update(Context(T0, game: Playing(@"C:\Games\game.exe")));
        wifi.Update(Context(T0, game: Idle));
        Assert.Equal(["open", "register", "interfaces", "scan A off", "streaming A on", "unregister", "scan A on", "streaming A off", "close"], _wlan.Calls);
    }

    [Fact]
    public void A_reconnect_during_a_game_wakes_the_engine_and_the_next_update_tunes_it_again()
    {
        _wlan.Adapters.Add(new WlanInterface(A, true));
        var wakes = 0;
        using var wifi = new WifiController(new WifiTuner(_wlan, _log), () => wakes++);
        wifi.Update(Context(T0, game: Playing(@"C:\Games\game.exe")));
        _wlan.Calls.Clear();

        _wlan.Callback!(A); // on a WLAN thread in real life: it only queues and wakes

        Assert.Equal(1, wakes);
        Assert.Empty(_wlan.Calls);
        wifi.Update(Context(T0, game: Playing(@"C:\Games\game.exe")));
        Assert.Equal(["scan A off", "streaming A on"], _wlan.Calls);
    }

    [Fact]
    public void Wifi_tuning_reports_an_adapter_that_cannot_pause_and_dispose_puts_things_back()
    {
        _wlan.Adapters.Add(new WlanInterface(A, true));
        _wlan.ScanCode = WlanNative.ERROR_NOT_SUPPORTED;
        var wifi = new WifiController(new WifiTuner(_wlan, _log), wake: null);

        Assert.Equal(WifiTuner.NotSupportedProblem, wifi.Update(Context(T0, game: Playing(@"C:\Games\game.exe"))));

        wifi.Dispose();
        Assert.Equal("close", _wlan.Calls[^1]);
        Assert.Null(wifi.Update(Context(T0, game: Playing(@"C:\Games\game.exe")))); // disposed: nothing reopens
        Assert.Equal(1, _wlan.Opens);
    }

    // Post-game reports

    [Fact]
    public void A_session_ends_with_a_report_that_leaves_out_every_game_exe_seen_during_it()
    {
        var meter = new FakeMeter();
        var store = new ReportStore(_tree.Root, _log);
        var reports = new ReportSource(meter, store, _log);
        var steam = Path.Combine(_tree.Root, "Steam");
        var library = Path.Combine(_tree.Root, @"Library\steamapps\common");
        var launcher = Path.Combine(library, @"Elden Ring\start_protected_game.exe");
        var game = Path.Combine(library, @"Elden Ring\eldenring.exe");
        var mod = Path.Combine(_tree.Root, @"Mods\seamless.exe"); // a game the user added, seen only mid-session
        var chat = Path.Combine(_tree.Root, @"Apps\chat.exe");
        var voice = Path.Combine(_tree.Root, @"Apps\voice.exe");
        var service = Path.Combine(_tree.Root, @"App\ConnectionManager.Service.exe");
        TickContext At(int minutes, ControllerStatus status) =>
            Context(T0.AddMinutes(minutes), game: status, steam: steam, libraries: [library], isProtected: p => p == voice, serviceExe: service);

        Assert.Null(reports.Update(At(0, Playing(launcher))));
        Assert.Null(reports.Update(At(5, Playing(launcher, mod))));
        meter.Totals[mod] = 300 * MB;
        meter.Totals[game] = 900 * MB;                                // under the library
        meter.Totals[Path.Combine(steam, "steam.exe")] = 50 * MB;     // under Steam
        meter.Totals[voice] = 20 * MB;                                // protected
        meter.Totals[service] = 10 * MB;
        meter.Totals["System"] = 40 * MB;                             // not a path
        meter.Totals[chat] = 60 * MB;
        Assert.Null(reports.Update(At(90, Playing(launcher))));        // the mod has exited
        Assert.Null(reports.Latest);
        Assert.Null(reports.Update(At(120, Idle)));                   // the session ends (after the grace period)

        var report = Assert.Single(reports.All());
        Assert.Equal(chat, Assert.Single(report.Apps).Path);
        Assert.Equal(("start_protected_game.exe", T0, T0.AddMinutes(120)), (report.Game, report.StartedAt, report.EndedAt));
        Assert.Equal((report.Id, 60 * MB, 1), (reports.Latest!.Id, reports.Latest.TotalBytes, reports.Latest.AppCount));
        Assert.Single(new ReportStore(_tree.Root, _log).All()); // saved

        Assert.True(reports.Dismiss(report.Id));
        Assert.Null(reports.Latest);
        Assert.False(reports.Dismiss("no-such-report"));
    }

    [Fact]
    public void The_next_session_starts_with_no_games_remembered()
    {
        var meter = new FakeMeter();
        var reports = new ReportSource(meter, new ReportStore(_tree.Root, _log), _log);
        var tool = Path.Combine(_tree.Root, @"Apps\tool.exe");

        reports.Update(Context(T0, game: Playing(tool)));     // tool counted as a game this time
        reports.Update(Context(T0.AddMinutes(30), game: Idle));
        Assert.Empty(reports.All());                          // nothing was used

        reports.Update(Context(T0.AddMinutes(40), game: Playing(Path.Combine(_tree.Root, @"Games\other.exe"))));
        meter.Totals[tool] = 30 * MB;
        reports.Update(Context(T0.AddMinutes(80), game: Idle));

        Assert.Equal(tool, Assert.Single(Assert.Single(reports.All()).Apps).Path);
    }
}
