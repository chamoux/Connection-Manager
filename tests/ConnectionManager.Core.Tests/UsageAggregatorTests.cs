using ConnectionManager.Core.Ipc;
using ConnectionManager.Core.Traffic;
using Microsoft.Extensions.Time.Testing;

namespace ConnectionManager.Core.Tests;

public class UsageAggregatorTests
{
    private const string Chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
    private const string Steam = @"C:\Program Files (x86)\Steam\steam.exe";
    private const string OneDrive = @"C:\Users\me\AppData\Local\Microsoft\OneDrive\OneDrive.exe";

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 9, 20, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Top_sums_bytes_per_path_and_names_the_file()
    {
        var usage = new UsageAggregator(_time);
        usage.Add(Chrome, 100, 10);
        usage.Add(Chrome, 50, 5);

        var top = Assert.Single(usage.Top(10));
        Assert.Equal(new UsageDto(Chrome, "chrome.exe", 150, 15), top);
    }

    [Fact]
    public void Top_orders_by_total_bytes_and_keeps_n()
    {
        var usage = new UsageAggregator(_time);
        usage.Add(Chrome, 100, 0);
        usage.Add(Steam, 0, 300); // upload counts too
        usage.Add(OneDrive, 200, 0);

        Assert.Equal([Steam, OneDrive, Chrome], usage.Top(10).Select(u => u.Path));
        Assert.Equal([Steam, OneDrive], usage.Top(2).Select(u => u.Path));
        Assert.Empty(usage.Top(0));
        Assert.Empty(usage.Top(-1));
    }

    [Fact]
    public void Paths_merge_case_insensitively()
    {
        var usage = new UsageAggregator(_time);
        usage.Add(Chrome, 100, 0);
        usage.Add(Chrome.ToUpperInvariant(), 50, 0);

        Assert.Equal(150, Assert.Single(usage.Top(10)).BytesIn);
        Assert.Equal(150, Assert.Single(usage.Snapshot()).Value);
    }

    [Fact]
    public void Top_covers_only_the_last_24_hours()
    {
        var usage = new UsageAggregator(_time);
        for (var hour = 0; hour < 25; hour++) // 1 byte in hour 0, 2 in hour 1 ... 25 in hour 24
        {
            if (hour > 0) _time.Advance(TimeSpan.FromHours(1));
            usage.Add(Chrome, hour + 1, 0);
        }

        // Hours 1..24 are the last 24: 2 + ... + 25. Hour 0's single byte has rolled off.
        Assert.Equal(Enumerable.Range(2, 24).Sum(), Assert.Single(usage.Top(10)).BytesIn);
        Assert.Equal(Enumerable.Range(1, 25).Sum(), usage.Snapshot()[Chrome]); // the snapshot keeps everything since start
    }

    [Fact]
    public void An_hour_slot_reused_a_day_later_starts_from_zero()
    {
        var usage = new UsageAggregator(_time);
        usage.Add(Chrome, 1000, 0);

        _time.Advance(TimeSpan.FromHours(23) + TimeSpan.FromMinutes(59));
        Assert.Equal(1000, Assert.Single(usage.Top(10)).BytesIn);

        _time.Advance(TimeSpan.FromMinutes(1)); // same slot, next day
        Assert.Empty(usage.Top(10));

        usage.Add(Chrome, 7, 0);
        Assert.Equal(7, Assert.Single(usage.Top(10)).BytesIn);
    }

    [Fact]
    public void Paths_beyond_the_cap_go_to_other()
    {
        var usage = new UsageAggregator(_time);
        for (var i = 0; i < UsageAggregator.PathLimit; i++) usage.Add($@"C:\Apps\app{i}.exe", 1, 0);

        usage.Add(@"C:\Apps\one-too-many.exe", 500, 0);
        usage.Add(@"C:\Apps\and-another.exe", 0, 250);
        usage.Add(@"C:\Apps\app0.exe", 1, 0); // a known path still counts as itself

        var snapshot = usage.Snapshot();
        Assert.Equal(UsageAggregator.PathLimit + 1, snapshot.Count);
        Assert.Equal(750, snapshot[UsageAggregator.OtherPath]);
        Assert.Equal(2, snapshot[@"C:\Apps\app0.exe"]);
        Assert.False(snapshot.ContainsKey(@"C:\Apps\one-too-many.exe"));
        Assert.Equal(new UsageDto("Other", "Other", 500, 250), usage.Top(1)[0]);
    }

    [Fact]
    public void Empty_or_overlong_paths_go_to_other_and_negative_bytes_count_as_zero()
    {
        var usage = new UsageAggregator(_time);
        usage.Add("", 10, 0);
        usage.Add(@"C:\" + new string('a', 2000) + ".exe", 20, 0);
        usage.Add(Chrome, -5, 3);
        usage.Add(Steam, 0, 0); // nothing to count: no entry

        var snapshot = usage.Snapshot();
        Assert.Equal(30, snapshot[UsageAggregator.OtherPath]);
        Assert.Equal(3, snapshot[Chrome]);
        Assert.False(snapshot.ContainsKey(Steam));
    }

    [Fact]
    public void Snapshots_diff_into_what_was_used_in_between()
    {
        var usage = new UsageAggregator(_time);
        usage.Add(Chrome, 100, 0);
        usage.Add(Steam, 40, 0);
        var before = usage.Snapshot();

        _time.Advance(TimeSpan.FromHours(30)); // well past the 24 h window: totals since start still hold
        usage.Add(Chrome, 1000, 24);
        usage.Add(OneDrive, 5, 5);
        var after = usage.Snapshot();

        var used = after.ToDictionary(p => p.Key, p => p.Value - before.GetValueOrDefault(p.Key), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(1024, used[Chrome]);
        Assert.Equal(0, used[Steam]);
        Assert.Equal(10, used[OneDrive]);
        Assert.Equal(100, before[Chrome]); // an earlier snapshot is a copy, not a live view
    }

    [Fact]
    public void Parallel_adds_give_exact_totals()
    {
        var usage = new UsageAggregator(_time);
        string[] paths = [Chrome, Steam, OneDrive, Chrome.ToUpperInvariant()];

        Parallel.For(0, 200_000, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            usage.Add(paths[i % paths.Length], 2, 1);
            if (i % 1000 == 0) _ = usage.Top(3); // readers run alongside the writers
        });

        var snapshot = usage.Snapshot();
        Assert.Equal(300_000, snapshot[Chrome]); // two of the four spellings: 100 000 adds x 3 bytes
        Assert.Equal(150_000, snapshot[Steam]);
        Assert.Equal(150_000, snapshot[OneDrive]);
        var chrome = usage.Top(10).Single(u => u.Path == Chrome);
        Assert.Equal((200_000L, 100_000L), (chrome.BytesIn, chrome.BytesOut));
    }
}
