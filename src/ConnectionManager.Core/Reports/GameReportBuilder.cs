using ConnectionManager.Core.Ipc;

namespace ConnectionManager.Core.Reports;

/// Turns two traffic-meter snapshots, taken when a game session started and ended, into the post-game report.
public static class GameReportBuilder
{
    public const int MaxApps = 20;
    public const long DefaultThresholdBytes = 5 * 1024 * 1024;

    /// Null when no app used at least `thresholdBytes` in the session. A snapshot holds only in + out totals per path,
    /// so each app's whole total goes in BytesIn and BytesOut is 0.
    public static ReportDto? Build(string game, DateTimeOffset started, DateTimeOffset ended,
        IReadOnlyDictionary<string, long> before, IReadOnlyDictionary<string, long> after,
        Func<string, bool> isExcluded, long thresholdBytes = DefaultThresholdBytes)
    {
        var minimum = Math.Max(thresholdBytes, 1);
        var apps = new List<UsageDto>();
        foreach (var (path, total) in after)
        {
            // A meter restarted mid-game counts from zero again, so its total can be below the "before" one.
            var used = Math.Max(0, total - before.GetValueOrDefault(path));
            if (used < minimum || path.Length is 0 or > IpcLimits.MaxPath) continue;
            var name = Path.GetFileName(path);
            if (name.Length > IpcLimits.MaxName || isExcluded(path)) continue;
            apps.Add(new UsageDto(path, name, used, 0));
        }
        if (apps.Count == 0) return null;

        apps.Sort((a, b) =>
        {
            var byBytes = b.BytesIn.CompareTo(a.BytesIn);
            return byBytes != 0 ? byBytes : StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path);
        });
        if (apps.Count > MaxApps) apps.RemoveRange(MaxApps, apps.Count - MaxApps);
        if (game.Length > IpcLimits.MaxName) game = game[..IpcLimits.MaxName];
        return new ReportDto(Guid.NewGuid().ToString("N"), game, started, ended, apps);
    }
}
