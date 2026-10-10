using ConnectionManager.Core.Games;
using ConnectionManager.Core.Ipc;
using ConnectionManager.Core.Reports;

namespace ConnectionManager.Core.Tests;

public class GameSessionReporterTests
{
    private const long MB = 1024 * 1024;
    private static readonly DateTimeOffset T0 = new(2026, 10, 9, 20, 0, 0, TimeSpan.Zero);
    private static readonly GameState Playing = new(true, "eldenring.exe", null);
    private static readonly Func<string, bool> Nothing = _ => false;

    private readonly GameSessionReporter _reporter = new();
    private readonly Dictionary<string, long> _meter = new(StringComparer.OrdinalIgnoreCase);
    private int _snapshots;

    private IReadOnlyDictionary<string, long> Snapshot()
    {
        _snapshots++;
        return new Dictionary<string, long>(_meter, StringComparer.OrdinalIgnoreCase);
    }

    private ReportDto? Tick(GameState state, int minute) => _reporter.Update(state, T0.AddMinutes(minute), Snapshot, Nothing);

    [Fact]
    public void A_full_session_gives_exactly_one_report()
    {
        _meter[@"C:\Apps\onedrive.exe"] = 100 * MB;
        Assert.Null(Tick(GameState.Idle, 0));
        Assert.Null(Tick(Playing, 1));
        _meter[@"C:\Apps\onedrive.exe"] = 160 * MB;
        Assert.Null(Tick(Playing, 30));

        var report = Tick(GameState.Idle, 62);

        Assert.NotNull(report);
        Assert.Equal("eldenring.exe", report.Game);
        Assert.Equal(T0.AddMinutes(1), report.StartedAt);
        Assert.Equal(T0.AddMinutes(62), report.EndedAt);
        Assert.Equal(60 * MB, Assert.Single(report.Apps).BytesIn);
        Assert.Null(Tick(GameState.Idle, 63));
        Assert.Null(Tick(GameState.Idle, 64));
    }

    [Fact]
    public void No_session_gives_no_report_and_takes_no_snapshot()
    {
        _meter[@"C:\Apps\onedrive.exe"] = 500 * MB;

        for (var minute = 0; minute < 5; minute++) Assert.Null(Tick(GameState.Idle, minute));

        Assert.Equal(0, _snapshots);
    }

    [Fact]
    public void The_grace_period_does_not_split_the_session()
    {
        Tick(Playing, 0);
        _meter[@"C:\Apps\a.exe"] = 10 * MB;
        Assert.Null(Tick(Playing with { GraceEndsAt = T0.AddMinutes(3) }, 1)); // game closed, grace running
        Assert.Null(Tick(Playing, 2));                                        // game back before grace ended
        _meter[@"C:\Apps\a.exe"] = 20 * MB;
        Assert.Null(Tick(Playing with { GraceEndsAt = T0.AddMinutes(8) }, 6));

        var report = Tick(GameState.Idle, 8);

        Assert.Equal(T0, report!.StartedAt);
        Assert.Equal(20 * MB, Assert.Single(report.Apps).BytesIn);
        Assert.Equal(2, _snapshots); // one at the start, one at the end
    }

    [Fact]
    public void A_meter_that_is_off_gives_no_report()
    {
        Tick(Playing, 0);

        Assert.Null(Tick(GameState.Idle, 60));
    }

    [Fact]
    public void A_second_session_gets_its_own_report()
    {
        Tick(Playing, 0);
        _meter[@"C:\Apps\a.exe"] = 10 * MB;
        var first = Tick(GameState.Idle, 10);

        Tick(Playing with { GameName = "cs2.exe" }, 20);
        _meter[@"C:\Apps\a.exe"] = 16 * MB;
        var second = Tick(GameState.Idle, 30);

        Assert.Equal(10 * MB, first!.Apps[0].BytesIn);
        Assert.Equal("cs2.exe", second!.Game);
        Assert.Equal(6 * MB, second.Apps[0].BytesIn);
        Assert.NotEqual(first.Id, second.Id);
    }
}
