using ConnectionManager.Core.Ipc;
using ConnectionManager.Core.Modes;

namespace ConnectionManager.Core.Tests;

public class TrayViewTests
{
    private static readonly OptionsDto Options = new(2, true, "Ctrl+Alt+G", true, false, 200, true, true, true, true);

    private static readonly StatusDto Idle = new(1, ModeSetting.Auto, false, null, null, [], 0,
        ModeSetting.Auto, false, "Not on a hotspot", 0,
        new NetworkDto("ethernet", null, false, false, null, false, false), [],
        new SettingsDto(Options, [], [], [], [], [], [], []), null);

    private static readonly StatusDto Playing = Idle with { GameActive = true, GameName = "Elden Ring", BlockedCount = 12 };
    private static readonly StatusDto Saving = Idle with { SaverActive = true, SaverReason = "iPhone hotspot", AllowedCount = 3 };

    private static StatusDto Quiet(StatusDto status) =>
        status with { Settings = status.Settings with { Options = status.Settings.Options with { Notifications = false } } };

    private static StatusDto WithReport(StatusDto status, string id, int minutes, long bytes = 300L * 1024 * 1024, int apps = 4) =>
        status with { LatestReport = new ReportSummaryDto(id, "Elden Ring", DateTimeOffset.UnixEpoch.AddMinutes(minutes), bytes, apps) };

    private static StatusDto OnWifi(StatusDto status, string? name = "Phone", bool canMarkMetered = true) =>
        status with { Network = new NetworkDto("wifi", name, false, false, null, canMarkMetered, false) };

    // Icon

    [Fact]
    public void Disconnected_is_grey_whatever_the_last_status_said()
    {
        Assert.Equal(TrayIconKind.Disconnected, TrayView.Icon(null, connected: false));
        Assert.Equal(TrayIconKind.Disconnected, TrayView.Icon(null, connected: true));
        Assert.Equal(TrayIconKind.Disconnected, TrayView.Icon(Playing with { Problems = ["x"] }, connected: false));
    }

    [Fact]
    public void Each_status_maps_to_its_icon()
    {
        Assert.Equal(TrayIconKind.Waiting, TrayView.Icon(Idle, true));
        Assert.Equal(TrayIconKind.GameActive, TrayView.Icon(Playing, true));
        Assert.Equal(TrayIconKind.SaverActive, TrayView.Icon(Saving, true));
        Assert.Equal(TrayIconKind.Both, TrayView.Icon(Playing with { SaverActive = true }, true));
        Assert.Equal(TrayIconKind.Off, TrayView.Icon(Idle with { GameMode = ModeSetting.Off, DataSaver = ModeSetting.Off }, true));
    }

    [Fact]
    public void One_mode_off_while_the_other_waits_is_still_waiting() =>
        Assert.Equal(TrayIconKind.Waiting, TrayView.Icon(Idle with { GameMode = ModeSetting.Off }, true));

    [Fact]
    public void An_active_mode_beats_off() =>
        Assert.Equal(TrayIconKind.SaverActive, TrayView.Icon(Saving with { GameMode = ModeSetting.Off, DataSaver = ModeSetting.On }, true));

    [Fact]
    public void A_problem_beats_every_other_state() =>
        Assert.Equal(TrayIconKind.Problem, TrayView.Icon(Playing with { SaverActive = true, Problems = ["Could not block"] }, true));

    // Tooltip

    [Fact]
    public void Disconnected_tooltip_says_the_service_is_not_running()
    {
        Assert.Equal("Connection Manager service is not running", TrayView.Tooltip(null, false));
        Assert.Equal("Connection Manager service is not running", TrayView.Tooltip(Playing, false));
    }

    [Fact]
    public void Tooltip_shows_both_modes()
    {
        Assert.Equal("Game Mode: waiting\nData Saver: waiting", TrayView.Tooltip(Idle, true));
        Assert.Equal("Game Mode: on for Elden Ring\nData Saver: waiting", TrayView.Tooltip(Playing, true));
        Assert.Equal("Game Mode: waiting\nData Saver: on", TrayView.Tooltip(Saving, true));
        Assert.Equal("Game Mode: off\nData Saver: off", TrayView.Tooltip(Idle with { GameMode = ModeSetting.Off, DataSaver = ModeSetting.Off }, true));
        Assert.Equal("Game Mode: ending soon\nData Saver: waiting", TrayView.Tooltip(Playing with { GraceEndsAt = DateTimeOffset.UnixEpoch }, true));
        Assert.Equal("Game Mode: on\nData Saver: starting",
            TrayView.Tooltip(Playing with { GameMode = ModeSetting.On, GameName = null, DataSaver = ModeSetting.On }, true));
    }

    [Fact]
    public void Tooltip_shows_the_first_problem() =>
        Assert.Equal("Problem: Could not block updaters", TrayView.Tooltip(Idle with { Problems = ["Could not block updaters", "Second"] }, true));

    [Fact]
    public void A_long_game_name_is_shortened_and_the_data_saver_line_survives()
    {
        var tooltip = TrayView.Tooltip(Playing with { GameName = new string('x', 200), DataSaver = ModeSetting.On }, true);
        Assert.True(tooltip.Length <= TrayView.MaxTooltip, $"{tooltip.Length} characters");
        Assert.StartsWith("Game Mode: on for xxx", tooltip);
        Assert.EndsWith("…\nData Saver: starting", tooltip);
    }

    [Fact]
    public void A_long_problem_is_shortened()
    {
        var tooltip = TrayView.Tooltip(Idle with { Problems = [new string('p', 300)] }, true);
        Assert.Equal(TrayView.MaxTooltip, tooltip.Length);
        Assert.EndsWith("p…", tooltip);
    }

    [Fact]
    public void Shortening_never_splits_a_surrogate_pair()
    {
        var name = new string('x', 22) + "😀😀😀😀😀😀😀😀😀😀"; // the cut falls inside an emoji
        var tooltip = TrayView.Tooltip(Playing with { GameName = name, DataSaver = ModeSetting.On }, true);
        Assert.True(tooltip.Length <= TrayView.MaxTooltip);
        for (var i = 0; i < tooltip.Length; i++)
        {
            if (char.IsHighSurrogate(tooltip[i])) Assert.True(i + 1 < tooltip.Length && char.IsLowSurrogate(tooltip[i + 1]), $"lone high surrogate at {i}");
            if (char.IsLowSurrogate(tooltip[i])) Assert.True(i > 0 && char.IsHighSurrogate(tooltip[i - 1]), $"lone low surrogate at {i}");
        }
    }

    [Fact]
    public void Control_characters_in_names_cannot_break_the_layout() =>
        Assert.Equal("Game Mode: on for Bad Name\nData Saver: waiting", TrayView.Tooltip(Playing with { GameName = "Bad\nName" }, true));

    // Menu lines

    [Fact]
    public void Menu_lines_give_the_details()
    {
        Assert.Equal("Game Mode on for Elden Ring: 12 blocked", TrayView.GameLine(Playing));
        Assert.Equal("Game Mode on (manual): 12 blocked", TrayView.GameLine(Playing with { GameMode = ModeSetting.On, GameName = null }));
        Assert.Equal("Game Mode ending soon: 12 blocked", TrayView.GameLine(Playing with { GraceEndsAt = DateTimeOffset.UnixEpoch }));
        Assert.Equal("Game Mode waiting for a game", TrayView.GameLine(Idle));
        Assert.Equal("Game Mode off", TrayView.GameLine(Idle with { GameMode = ModeSetting.Off }));
        Assert.Equal("Data Saver on: 3 apps allowed", TrayView.SaverLine(Saving));
        Assert.Equal("Data Saver on: 1 app allowed", TrayView.SaverLine(Saving with { AllowedCount = 1 }));
        Assert.Equal("Data Saver waiting for a hotspot", TrayView.SaverLine(Idle));
        Assert.Equal("Data Saver off", TrayView.SaverLine(Idle with { DataSaver = ModeSetting.Off }));
    }

    [Fact]
    public void Wifi_toggles_show_only_on_wifi()
    {
        Assert.False(TrayView.ShowsHotspotToggle(Idle));
        Assert.False(TrayView.ShowsMeteredToggle(Idle with { Network = Idle.Network with { CanMarkMetered = true } }));
        Assert.True(TrayView.ShowsHotspotToggle(OnWifi(Idle)));
        Assert.False(TrayView.ShowsHotspotToggle(OnWifi(Idle, name: null)));
        Assert.True(TrayView.ShowsMeteredToggle(OnWifi(Idle)));
        Assert.False(TrayView.ShowsMeteredToggle(OnWifi(Idle, canMarkMetered: false)));
    }

    [Fact]
    public void The_hotspot_checkbox_follows_the_saved_list_exactly()
    {
        var wifi = OnWifi(Idle);
        Assert.False(TrayView.IsMarkedHotspot(wifi));
        Assert.True(TrayView.IsMarkedHotspot(wifi with { Settings = wifi.Settings with { HotspotNetworks = ["Phone"] } }));
        Assert.False(TrayView.IsMarkedHotspot(wifi with { Settings = wifi.Settings with { HotspotNetworks = ["phone"] } })); // names are case-sensitive
    }

    // Balloons

    [Fact]
    public void No_change_no_balloon()
    {
        Assert.Empty(TrayView.Balloons(Idle, Idle with { Version = 2 }));
        Assert.Empty(TrayView.Balloons(Playing, Playing));
    }

    [Fact]
    public void Game_mode_on_and_off()
    {
        var on = Assert.Single(TrayView.Balloons(Idle, Playing));
        Assert.Equal("Game Mode on", on.Title);
        Assert.Contains("12 updaters", on.Text);
        Assert.False(on.IsWarning);

        var off = Assert.Single(TrayView.Balloons(Playing, Idle));
        Assert.Equal("Game Mode off", off.Title);
    }

    [Fact]
    public void Data_saver_on_and_off_give_the_reason()
    {
        var on = Assert.Single(TrayView.Balloons(Idle, Saving));
        Assert.Equal("Data Saver on", on.Title);
        Assert.Contains("iPhone hotspot", on.Text);

        var off = Assert.Single(TrayView.Balloons(Saving, Idle));
        Assert.Equal("Data Saver off", off.Title);
        Assert.Contains("Not on a hotspot", off.Text);
    }

    [Fact]
    public void A_new_problem_is_a_warning_and_an_old_one_is_not_repeated()
    {
        var problem = Assert.Single(TrayView.Balloons(Idle, Idle with { Problems = ["Could not block updaters"] }));
        Assert.True(problem.IsWarning);
        Assert.Contains("Could not block updaters", problem.Text);

        Assert.Empty(TrayView.Balloons(Idle with { Problems = ["A"] }, Idle with { Problems = ["A"] }));
        var second = Assert.Single(TrayView.Balloons(Idle with { Problems = ["A"] }, Idle with { Problems = ["A", "B"] }));
        Assert.Contains("B", second.Text);
        Assert.Empty(TrayView.Balloons(Idle with { Problems = ["A", "B"] }, Idle with { Problems = ["A"] }));
    }

    [Fact]
    public void A_new_report_says_how_much_was_used()
    {
        var report = Assert.Single(TrayView.Balloons(Idle, WithReport(Idle, "r1", 10)));
        Assert.Equal("4 apps used 300 MB during your game. Open Settings to review.", report.Text);
        Assert.True(report.OpensReports); // a click opens the settings window on the reports page

        var one = Assert.Single(TrayView.Balloons(Idle, WithReport(Idle, "r1", 10, bytes: 6L * 1024 * 1024 + 300 * 1024, apps: 1)));
        Assert.Equal("1 app used 6.3 MB during your game. Open Settings to review.", one.Text);
    }

    [Fact]
    public void A_report_fires_once_per_id()
    {
        var first = WithReport(Idle, "r1", 10);
        Assert.Single(TrayView.Balloons(Idle, first));
        Assert.Empty(TrayView.Balloons(first, first with { Version = 2 }));

        var newer = WithReport(Idle, "r2", 20);
        Assert.Single(TrayView.Balloons(first, newer));
        // Dismissing r2 brings back r1, which was already announced.
        Assert.Empty(TrayView.Balloons(newer, first));
        // All dismissed, then a new game.
        Assert.Single(TrayView.Balloons(Idle, WithReport(Idle, "r3", 30)));
    }

    [Fact]
    public void The_first_status_announces_what_is_on_but_not_an_old_report()
    {
        Assert.Empty(TrayView.Balloons(null, WithReport(Idle, "r1", 10)));
        Assert.Empty(TrayView.Balloons(null, Idle));
        Assert.Equal("Game Mode on", Assert.Single(TrayView.Balloons(null, Playing)).Title);
        Assert.True(Assert.Single(TrayView.Balloons(null, Idle with { Problems = ["A"] })).IsWarning);
    }

    [Fact]
    public void Notifications_off_suppresses_only_the_optional_balloons()
    {
        Assert.Empty(TrayView.Balloons(Quiet(Idle), Quiet(Playing)));
        Assert.Empty(TrayView.Balloons(Quiet(Playing), Quiet(Idle)));
        Assert.Empty(TrayView.Balloons(Quiet(Idle), Quiet(Saving)));
        Assert.Empty(TrayView.Balloons(Quiet(Idle), Quiet(WithReport(Idle, "r1", 10))));

        var problem = Assert.Single(TrayView.Balloons(Quiet(Idle), Quiet(Playing with { Problems = ["A"] })));
        Assert.True(problem.IsWarning);
        Assert.Equal(TrayIconKind.Problem, TrayView.Icon(Quiet(Playing with { Problems = ["A"] }), true));
    }

    [Fact]
    public void Several_changes_come_in_order_problem_first()
    {
        var next = WithReport(Saving with { Problems = ["A"] }, "r1", 10);
        var balloons = TrayView.Balloons(Playing, next);
        Assert.Equal(new[] { "Connection Manager problem", "Game Mode off", "Data Saver on", "Post-game report" }, balloons.Select(b => b.Title));
    }

    [Fact]
    public void Balloon_text_fits_the_notification_limits()
    {
        var balloon = Assert.Single(TrayView.Balloons(Idle, Idle with { Problems = [new string('p', 1000)] }));
        Assert.True(balloon.Text.Length <= TrayView.MaxBalloonText);
        var error = TrayView.CommandError(new string('e', 1000));
        Assert.True(error.Text.Length <= TrayView.MaxBalloonText);
        Assert.True(error.IsWarning);
    }
}
