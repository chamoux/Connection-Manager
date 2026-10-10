using ConnectionManager.Core.Games;
using ConnectionManager.Core.Ipc;

namespace ConnectionManager.Core.Reports;

/// Watches the game state each Engine tick: a meter snapshot when a session starts, another when it ends (after the
/// grace period, which GameState already covers), and the report built from the two. Call from one thread.
public sealed class GameSessionReporter
{
    private Session? _session;

    public bool InSession => _session is not null;

    /// The report on the tick a session ends with at least one app worth reporting, otherwise null.
    /// The snapshot is taken before any state changes, so if it throws the next tick tries the same edge again.
    public ReportDto? Update(GameState state, DateTimeOffset now, Func<IReadOnlyDictionary<string, long>> snapshot,
        Func<string, bool> isExcluded)
    {
        if (state.Active)
        {
            if (_session is null) _session = new Session(state.GameName ?? "Game", now, snapshot());
            return null;
        }
        if (_session is not { } session) return null;

        var after = snapshot();
        _session = null;
        return GameReportBuilder.Build(session.Game, session.Started, now, session.Before, after, isExcluded);
    }

    private sealed record Session(string Game, DateTimeOffset Started, IReadOnlyDictionary<string, long> Before);
}
