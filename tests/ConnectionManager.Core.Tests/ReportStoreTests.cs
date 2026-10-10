using ConnectionManager.Core.Ipc;
using ConnectionManager.Core.Reports;

namespace ConnectionManager.Core.Tests;

public sealed class ReportStoreTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 9, 20, 0, 0, TimeSpan.Zero);

    private readonly TempTree _tree = new();
    private readonly ListLog _log = new();

    private string FilePath => Path.Combine(_tree.Root, "reports.json");
    private ReportStore Open() => new(_tree.Root, _log);

    private static ReportDto Report(string id, params long[] bytes) => new(id, "eldenring.exe", Start, Start.AddHours(1),
        bytes.Select((b, i) => new UsageDto($@"C:\Apps\app{i}.exe", $"app{i}.exe", b, 0)).ToList());

    public void Dispose() => _tree.Dispose();

    [Fact]
    public void A_missing_file_is_an_empty_store()
    {
        var store = Open();

        Assert.Empty(store.All());
        Assert.Null(store.LatestUndismissed());
        Assert.Empty(_log.Lines);
    }

    [Fact]
    public void Keeps_the_newest_twenty_newest_first()
    {
        var store = Open();
        for (var i = 1; i <= 25; i++) store.Add(Report($"r{i}", 10));

        var all = store.All();

        Assert.Equal(ReportStore.MaxReports, all.Count);
        Assert.Equal("r25", all[0].Id);
        Assert.Equal("r6", all[^1].Id);
        Assert.Equal(20, Open().All().Count);
    }

    [Fact]
    public void Saves_and_loads_reports_and_dismissals()
    {
        var store = Open();
        store.Add(Report("old", 7, 3));
        store.Add(Report("new", 9));
        store.Dismiss("new");

        var reloaded = Open();

        var all = reloaded.All();
        Assert.Equal(["new", "old"], all.Select(r => r.Id));
        var expected = Report("old", 7, 3);
        Assert.Equal(expected.Game, all[1].Game);
        Assert.Equal(expected.StartedAt, all[1].StartedAt);
        Assert.Equal(expected.EndedAt, all[1].EndedAt);
        Assert.Equal(expected.Apps, all[1].Apps);
        Assert.Equal("old", reloaded.LatestUndismissed()!.Id);
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public void Dismiss_marks_one_report_and_reports_whether_it_was_found()
    {
        var store = Open();
        store.Add(Report("a", 10));

        Assert.True(store.Dismiss("a"));
        Assert.False(store.Dismiss("missing"));
        Assert.Null(store.LatestUndismissed());
        Assert.Single(store.All()); // GetReports still returns dismissed ones
    }

    [Fact]
    public void LatestUndismissed_skips_dismissed_reports_and_sums_the_bytes()
    {
        var store = Open();
        store.Add(Report("older", 7, 3));
        store.Add(Report("newer", 9));

        Assert.Equal("newer", store.LatestUndismissed()!.Id);
        store.Dismiss("newer");

        var latest = store.LatestUndismissed()!;
        Assert.Equal(new ReportSummaryDto("older", "eldenring.exe", Start.AddHours(1), 10, 2), latest);
    }

    [Fact]
    public void A_corrupt_file_is_renamed_to_bad_and_the_store_starts_empty()
    {
        File.WriteAllText(FilePath, "{ not json");

        var store = Open();

        Assert.Empty(store.All());
        Assert.False(File.Exists(FilePath));
        Assert.Equal("{ not json", File.ReadAllText(FilePath + ".bad"));
        Assert.Contains(_log.Lines, l => l.StartsWith("WARN", StringComparison.Ordinal) && l.Contains(".bad", StringComparison.Ordinal));

        store.Add(Report("fresh", 10));
        Assert.Equal("fresh", Assert.Single(Open().All()).Id);
    }

    [Fact]
    public void An_oversize_file_is_renamed_to_bad_and_the_store_starts_empty()
    {
        File.WriteAllText(FilePath, new string(' ', ReportStore.MaxBytes + 1));

        var store = Open();

        Assert.Empty(store.All());
        Assert.False(File.Exists(FilePath));
        Assert.True(File.Exists(FilePath + ".bad"));
    }

    [Fact]
    public void Invalid_entries_are_dropped_on_load_and_valid_ones_kept()
    {
        var app = """{ "path": "C:\\a.exe", "name": "a.exe", "bytesIn": 10, "bytesOut": 0 }""";
        string Entry(string id, string game = "g.exe", string? apps = null) =>
            $$"""{ "report": { "id": "{{id}}", "game": "{{game}}", "startedAt": "2026-10-09T20:00:00+00:00", "endedAt": "2026-10-09T21:00:00+00:00", "apps": [ {{apps ?? app}} ] }, "dismissed": false }""";
        var entries = new[]
        {
            Entry("good"),
            Entry("long-game", game: new string('g', 257)),
            Entry("long-path", apps: $$"""{ "path": "C:\\{{new string('p', 1030)}}", "name": "a.exe", "bytesIn": 10, "bytesOut": 0 }"""),
            Entry("long-name", apps: $$"""{ "path": "C:\\a.exe", "name": "{{new string('n', 257)}}", "bytesIn": 10, "bytesOut": 0 }"""),
            Entry("negative", apps: """{ "path": "C:\\a.exe", "name": "a.exe", "bytesIn": -1, "bytesOut": 0 }"""),
            Entry("too-many", apps: string.Join(", ", Enumerable.Repeat(app, 21))),
            Entry("empty-path", apps: """{ "path": "", "name": "a.exe", "bytesIn": 10, "bytesOut": 0 }"""),
            Entry(""),
            Entry("good"), // a duplicate id
            "null",
            Entry("also-good"),
        };
        File.WriteAllText(FilePath, $$"""{ "v": 1, "reports": [ {{string.Join(",\n", entries)}} ] }""");

        var store = Open();

        Assert.Equal(["good", "also-good"], store.All().Select(r => r.Id));
        Assert.True(File.Exists(FilePath)); // a file with some bad entries is still used
        Assert.Contains(_log.Lines, l => l.StartsWith("WARN", StringComparison.Ordinal));
    }

    [Fact]
    public void A_failed_save_is_logged_and_the_report_kept_in_memory()
    {
        var store = Open();
        store.Add(Report("first", 10));

        using (new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None)) // held open by another program
        {
            store.Add(Report("second", 10));
        }

        Assert.Equal(["second", "first"], store.All().Select(r => r.Id));
        Assert.Contains(_log.Lines, l => l.StartsWith("ERROR", StringComparison.Ordinal) || l.StartsWith("WARN", StringComparison.Ordinal));
        Assert.False(File.Exists(FilePath + ".tmp"));
        Assert.Equal("first", Assert.Single(Open().All()).Id);
    }

    [Fact]
    public void Adding_an_invalid_report_throws()
    {
        var store = Open();

        Assert.Throws<ArgumentException>(() => store.Add(Report("bad", -5)));
        Assert.Empty(store.All());
    }
}
