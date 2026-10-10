using ConnectionManager.Core.Logging;
using Microsoft.Extensions.Time.Testing;

namespace ConnectionManager.Core.Tests;

public sealed class FileLogTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 8, 21, 30, 0, TimeSpan.Zero));
    private string Folder => Path.Combine(_tree.Root, "logs");

    [Fact]
    public void Writes_one_file_per_day_with_time_and_level()
    {
        var log = new FileLog(Folder, _time);
        log.Info("Game Mode on");
        log.Error("Oops", new InvalidOperationException("bad"));

        var text = File.ReadAllText(Path.Combine(Folder, "app-20261008.log"));
        Assert.Contains("2026-10-08 21:30:00 INFO Game Mode on", text);
        Assert.Contains("ERROR Oops: System.InvalidOperationException: bad", text);
    }

    [Fact]
    public void Deletes_logs_older_than_a_week()
    {
        Directory.CreateDirectory(Folder);
        var old = Path.Combine(Folder, "app-20260901.log");
        var recent = Path.Combine(Folder, "app-20261007.log");
        File.WriteAllText(old, "old");
        File.WriteAllText(recent, "recent");
        File.SetLastWriteTimeUtc(old, _time.GetUtcNow().UtcDateTime.AddDays(-8));
        File.SetLastWriteTimeUtc(recent, _time.GetUtcNow().UtcDateTime.AddDays(-1));

        _ = new FileLog(Folder, _time);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public void A_new_day_starts_a_new_file()
    {
        _time.SetUtcNow(new DateTimeOffset(2026, 10, 8, 23, 59, 0, TimeSpan.Zero));
        var log = new FileLog(Folder, _time);
        log.Info("before midnight");
        _time.Advance(TimeSpan.FromMinutes(2));
        log.Info("after midnight");

        Assert.Contains("before midnight", File.ReadAllText(Path.Combine(Folder, "app-20261008.log")));
        Assert.Contains("after midnight", File.ReadAllText(Path.Combine(Folder, "app-20261009.log")));
    }

    [Fact]
    public void A_locked_log_file_does_not_throw()
    {
        var log = new FileLog(Folder, _time);
        using var locked = new FileStream(Path.Combine(Folder, "app-20261008.log"), FileMode.Create, FileAccess.Write, FileShare.None);

        log.Info("dropped, not thrown");
    }

    [Fact]
    public void Lines_and_file_names_use_local_time()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time")); // UTC+9, no daylight saving
        var log = new FileLog(Folder, _time);

        log.Info("hello");

        Assert.Contains("2026-10-09 06:30:00 INFO hello", File.ReadAllText(Path.Combine(Folder, "app-20261009.log")));
    }

    [Fact]
    public void Line_breaks_and_control_characters_cannot_forge_a_line()
    {
        var log = new FileLog(Folder, _time);

        log.Info("Pipe client: C:\\a\r\n2026-10-08 21:30:00 INFO Game Mode off\u0085x\u2028y\tz\0.exe");
        log.Error("Oops", new InvalidOperationException("line one\nline two"));

        var lines = File.ReadAllLines(Path.Combine(Folder, "app-20261008.log"));
        Assert.Equal(2, lines.Length);
        Assert.Equal(@"2026-10-08 21:30:00 INFO Pipe client: C:\a 2026-10-08 21:30:00 INFO Game Mode off x y?z?.exe", lines[0]);
        Assert.StartsWith("2026-10-08 21:30:00 ERROR Oops: System.InvalidOperationException: line one line two", lines[1]);
    }

    [Fact]
    public void After_the_daily_limit_one_line_says_so_and_the_rest_of_the_day_is_dropped()
    {
        var log = new FileLog(Folder, _time, maxBytesPerDay: 300);
        for (var i = 0; i < 20; i++) log.Info($"line {i} {new string('x', 20)}");

        var file = Path.Combine(Folder, "app-20261008.log");
        var lines = File.ReadAllLines(file);
        Assert.Contains("Log limit reached for today", lines[^1]);
        Assert.Single(lines, l => l.Contains("Log limit", StringComparison.Ordinal));
        Assert.InRange(new FileInfo(file).Length, 250, 300 + 200);

        _time.Advance(TimeSpan.FromDays(1));
        log.Info("a new day");
        Assert.Contains("a new day", File.ReadAllText(Path.Combine(Folder, "app-20261009.log")));
    }

    [Fact]
    public void The_limit_counts_what_an_earlier_run_wrote_today()
    {
        Directory.CreateDirectory(Folder);
        var file = Path.Combine(Folder, "app-20261008.log");
        File.WriteAllText(file, new string('x', 290) + Environment.NewLine);

        new FileLog(Folder, _time, maxBytesPerDay: 300).Info("one line too many");

        var lines = File.ReadAllLines(file);
        Assert.Equal(2, lines.Length);
        Assert.Contains("Log limit reached for today", lines[1]);
    }

    public void Dispose() => _tree.Dispose();
}
