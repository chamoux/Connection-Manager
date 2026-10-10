using ConnectionManager.Core.Ipc;

namespace ConnectionManager.Core.Traffic;

/// Network bytes per program: hourly totals for the last 24 hours, and totals since start for game-session diffs.
/// Thread-safe: the ETW thread adds while the engine reads.
public sealed class UsageAggregator
{
    public const int PathLimit = 5000;
    public const string OtherPath = "Other";

    private const int Hours = 24;

    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<string, Usage> _byPath = new(StringComparer.OrdinalIgnoreCase);

    public UsageAggregator(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    public void Add(string path, long bytesIn, long bytesOut)
    {
        bytesIn = Math.Max(0, bytesIn);
        bytesOut = Math.Max(0, bytesOut);
        if (bytesIn == 0 && bytesOut == 0) return;
        if (string.IsNullOrEmpty(path) || path.Length > IpcLimits.MaxPath) path = OtherPath; // every path ends up on the pipe

        var hour = CurrentHour();
        lock (_gate)
        {
            var usage = UsageOf(path);
            ref var bucket = ref usage.Buckets[hour % Hours];
            if (bucket.Hour != hour) bucket = new Bucket { Hour = hour }; // the slot still holds an hour from a day or more ago
            bucket.In += bytesIn;
            bucket.Out += bytesOut;
            usage.Total += bytesIn + bytesOut;
        }
    }

    /// The n programs that used the most data in the last 24 hours, most first.
    public IReadOnlyList<UsageDto> Top(int n)
    {
        if (n <= 0) return [];
        var hour = CurrentHour();
        var rows = new List<UsageDto>();
        lock (_gate)
        {
            foreach (var (path, usage) in _byPath)
            {
                long bytesIn = 0, bytesOut = 0;
                foreach (var bucket in usage.Buckets)
                {
                    if (hour - bucket.Hour is < 0 or >= Hours) continue;
                    bytesIn += bucket.In;
                    bytesOut += bucket.Out;
                }
                if (bytesIn + bytesOut > 0) rows.Add(new UsageDto(path, Path.GetFileName(path), bytesIn, bytesOut));
            }
        }
        rows.Sort(static (a, b) =>
        {
            var byBytes = (b.BytesIn + b.BytesOut).CompareTo(a.BytesIn + a.BytesOut);
            return byBytes != 0 ? byBytes : StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path);
        });
        if (rows.Count > n) rows.RemoveRange(n, rows.Count - n);
        return rows;
    }

    /// Bytes in + out per path since start. Diff two snapshots to get what was used in between.
    public IReadOnlyDictionary<string, long> Snapshot()
    {
        lock (_gate)
        {
            var totals = new Dictionary<string, long>(_byPath.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var (path, usage) in _byPath) totals[path] = usage.Total;
            return totals;
        }
    }

    private long CurrentHour() => _time.GetUtcNow().UtcTicks / TimeSpan.TicksPerHour;

    // A new path past the cap is counted as Other, so a flood of one-off programs can't grow memory without end.
    private Usage UsageOf(string path)
    {
        if (_byPath.TryGetValue(path, out var usage)) return usage;
        if (_byPath.Count >= PathLimit) path = OtherPath;
        return _byPath.TryGetValue(path, out usage) ? usage : _byPath[path] = new Usage();
    }

    private struct Bucket
    {
        public long Hour;
        public long In;
        public long Out;
    }

    private sealed class Usage
    {
        public readonly Bucket[] Buckets = new Bucket[Hours];
        public long Total;
    }
}
