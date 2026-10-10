using ConnectionManager.Core.Ipc;
using ConnectionManager.Core.Reports;

namespace ConnectionManager.Core.Tests;

public class GameReportBuilderTests
{
    private const long MB = 1024 * 1024;
    private static readonly DateTimeOffset Start = new(2026, 10, 9, 20, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = Start.AddHours(2);

    private static Dictionary<string, long> Snap(params (string Path, long Bytes)[] rows) =>
        rows.ToDictionary(r => r.Path, r => r.Bytes, StringComparer.OrdinalIgnoreCase);

    private static ReportDto? Build(Dictionary<string, long> before, Dictionary<string, long> after, Func<string, bool>? excluded = null) =>
        GameReportBuilder.Build("eldenring.exe", Start, End, before, after, excluded ?? (_ => false));

    [Fact]
    public void Exactly_the_threshold_is_reported_and_one_byte_less_is_not()
    {
        var report = Build(Snap(), Snap((@"C:\Apps\a.exe", 5 * MB), (@"C:\Apps\b.exe", 5 * MB - 1)));

        var app = Assert.Single(report!.Apps);
        Assert.Equal(@"C:\Apps\a.exe", app.Path);
        Assert.Equal("a.exe", app.Name);
        Assert.Equal(5 * MB, app.BytesIn);
        Assert.Equal(0, app.BytesOut);
    }

    [Fact]
    public void Reports_what_was_used_between_the_snapshots()
    {
        var report = Build(Snap((@"C:\Apps\a.exe", 100 * MB)), Snap((@"C:\Apps\a.exe", 112 * MB)));

        Assert.Equal(12 * MB, Assert.Single(report!.Apps).BytesIn);
        Assert.Equal("eldenring.exe", report.Game);
        Assert.Equal(Start, report.StartedAt);
        Assert.Equal(End, report.EndedAt);
        Assert.Equal(32, report.Id.Length);
        Assert.True(Guid.TryParseExact(report.Id, "N", out _));
    }

    [Fact]
    public void Excluded_apps_are_left_out()
    {
        var report = Build(Snap(), Snap((@"C:\Apps\a.exe", 50 * MB), (@"C:\Steam\steam.exe", 90 * MB)),
            p => p.StartsWith(@"C:\Steam\", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(@"C:\Apps\a.exe", Assert.Single(report!.Apps).Path);
    }

    [Fact]
    public void Apps_are_sorted_by_bytes_descending()
    {
        var report = Build(Snap(), Snap((@"C:\a.exe", 6 * MB), (@"C:\b.exe", 60 * MB), (@"C:\c.exe", 20 * MB)));

        Assert.Equal([@"C:\b.exe", @"C:\c.exe", @"C:\a.exe"], report!.Apps.Select(a => a.Path));
    }

    [Fact]
    public void Keeps_at_most_twenty_apps_the_biggest_ones()
    {
        var after = Snap(Enumerable.Range(1, 30).Select(i => ($@"C:\Apps\app{i}.exe", i * 10 * MB)).ToArray());

        var report = Build(Snap(), after);

        Assert.Equal(GameReportBuilder.MaxApps, report!.Apps.Count);
        Assert.Equal(@"C:\Apps\app30.exe", report.Apps[0].Path);
        Assert.Equal(@"C:\Apps\app11.exe", report.Apps[^1].Path);
    }

    [Fact]
    public void A_meter_restart_gives_zero_not_a_negative_number()
    {
        var report = Build(Snap((@"C:\a.exe", 500 * MB), (@"C:\b.exe", 1 * MB)), Snap((@"C:\a.exe", 3 * MB), (@"C:\b.exe", 9 * MB)));

        Assert.Equal(@"C:\b.exe", Assert.Single(report!.Apps).Path);
    }

    [Fact]
    public void Nothing_over_the_threshold_gives_no_report()
    {
        Assert.Null(Build(Snap(), Snap()));
        Assert.Null(Build(Snap((@"C:\a.exe", 10 * MB)), Snap((@"C:\a.exe", 11 * MB))));
        Assert.Null(Build(Snap(), Snap((@"C:\a.exe", 50 * MB)), _ => true));
    }

    [Fact]
    public void A_custom_threshold_is_used()
    {
        var report = GameReportBuilder.Build("g.exe", Start, End, Snap(), Snap((@"C:\a.exe", 2 * MB)), _ => false, thresholdBytes: MB);

        Assert.Single(report!.Apps);
    }

    [Fact]
    public void Paths_too_long_for_the_pipe_are_skipped_and_a_long_game_name_is_cut()
    {
        var longPath = @"C:\" + new string('x', 1100) + ".exe";

        var report = GameReportBuilder.Build(new string('g', 300), Start, End, Snap(), Snap((longPath, 50 * MB), (@"C:\a.exe", 50 * MB)), _ => false);

        Assert.Equal(@"C:\a.exe", Assert.Single(report!.Apps).Path);
        Assert.Equal(256, report.Game.Length);
    }
}
