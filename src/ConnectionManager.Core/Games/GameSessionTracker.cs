namespace ConnectionManager.Core.Games;

/// Game state after the grace period: Active stays true until GraceEndsAt passes with no game running.
public sealed record GameState(bool Active, string? GameName, DateTimeOffset? GraceEndsAt)
{
    public static readonly GameState Idle = new(false, null, null);
}

/// Turns "which games are running right now" into a steady on/off state, so Game Mode doesn't flicker when a game restarts.
public sealed class GameSessionTracker
{
    private readonly TimeProvider _time;
    private DateTimeOffset _lastSeen;

    public GameSessionTracker(TimeProvider time, TimeSpan grace)
    {
        _time = time;
        Grace = grace;
    }

    /// Read on every update, so changing it also shortens or extends a countdown already running.
    public TimeSpan Grace { get; set; }
    public GameState State { get; private set; } = GameState.Idle;

    public GameState Update(IReadOnlyList<string> runningGamePaths)
    {
        var now = _time.GetUtcNow();
        if (runningGamePaths.Count > 0)
        {
            _lastSeen = now;
            return State = new GameState(true, Path.GetFileName(runningGamePaths[0]), null);
        }
        if (!State.Active) return State;

        var graceEnds = _lastSeen + Grace;
        return State = now >= graceEnds ? GameState.Idle : State with { GraceEndsAt = graceEnds };
    }
}
