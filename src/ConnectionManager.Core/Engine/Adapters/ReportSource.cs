using ConnectionManager.Core.Ipc;
using ConnectionManager.Core.Logging;
using ConnectionManager.Core.Reports;

namespace ConnectionManager.Core.Engine;

/// Post-game reports from the tick: GameSessionReporter takes meter snapshots at the session's start and end, and the
/// report goes into ReportStore. A session can run several game exes (a launcher, then the game), so every one seen
/// while it lasted is left out of its report, not just those running at the end. Engine thread only.
public sealed class ReportSource : IReportSource
{
    /// Game exes remembered per session; more than this many is not a real session.
    public const int MaxSessionGames = 64;

    private readonly ITrafficMeter _meter;
    private readonly ReportStore _store;
    private readonly ILog _log;
    private readonly GameSessionReporter _reporter = new();
    private readonly HashSet<string> _sessionGames = new(StringComparer.OrdinalIgnoreCase);

    public ReportSource(ITrafficMeter meter, ReportStore store, ILog log)
    {
        _meter = meter;
        _store = store;
        _log = log;
    }

    public ReportSummaryDto? Latest => _store.LatestUndismissed();

    public IReadOnlyList<ReportDto> All() => _store.All();

    public bool Dismiss(string id) => _store.Dismiss(id);

    public string? Update(TickContext context)
    {
        var state = context.Game.Game;
        if (state.Active)
            foreach (var game in context.Game.RunningGames)
                if (_sessionGames.Count < MaxSessionGames) _sessionGames.Add(game);

        // Built on the first app the report asks about, which happens only when a session ends.
        Func<string, bool>? excluded = null;
        var report = _reporter.Update(state, context.Now, _meter.Snapshot, path => (excluded ??= ReportExclusions.Create(
            _sessionGames.ToArray(), [context.SteamPath, .. context.SteamLibraries], context.IsProtected, context.ServiceExe))(path));
        if (!_reporter.InSession) _sessionGames.Clear();
        if (report is null) return null;
        try
        {
            _store.Add(report);
            _log.Info($"Post-game report: {report.Apps.Count} app(s) used the internet during {report.Game}");
        }
        catch (ArgumentException ex) // a report that would not load back: drop it, the next session makes a new one
        {
            _log.Warn($"Post-game report dropped: {ex.Message}");
        }
        return null;
    }
}
