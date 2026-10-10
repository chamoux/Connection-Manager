using ConnectionManager.Core.Games;
using Microsoft.Extensions.Time.Testing;

namespace ConnectionManager.Core.Tests;

public class GameSessionTrackerTests
{
    private const string Game = @"C:\Steam\steamapps\common\Elden Ring\Game\eldenring.exe";
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 8, 20, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Starts_idle() => Assert.Equal(GameState.Idle, new GameSessionTracker(_time, TimeSpan.FromMinutes(2)).State);

    [Fact]
    public void A_running_game_makes_it_active_at_once()
    {
        var state = new GameSessionTracker(_time, TimeSpan.FromMinutes(2)).Update([Game]);

        Assert.True(state.Active);
        Assert.Equal("eldenring.exe", state.GameName);
        Assert.Null(state.GraceEndsAt);
    }

    [Fact]
    public void Stays_active_through_the_grace_period_then_goes_idle()
    {
        var tracker = new GameSessionTracker(_time, TimeSpan.FromMinutes(2));
        tracker.Update([Game]);

        var closing = tracker.Update([]);
        Assert.True(closing.Active);
        Assert.Equal(_time.GetUtcNow().AddMinutes(2), closing.GraceEndsAt);

        _time.Advance(TimeSpan.FromSeconds(119));
        Assert.True(tracker.Update([]).Active);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(GameState.Idle, tracker.Update([]));
    }

    [Fact]
    public void Restarting_the_game_during_grace_keeps_it_active()
    {
        var tracker = new GameSessionTracker(_time, TimeSpan.FromMinutes(2));
        tracker.Update([Game]);
        tracker.Update([]);
        _time.Advance(TimeSpan.FromMinutes(1));

        var state = tracker.Update([Game]);

        Assert.True(state.Active);
        Assert.Null(state.GraceEndsAt);
    }

    [Fact]
    public void Zero_grace_goes_idle_as_soon_as_the_game_closes()
    {
        var tracker = new GameSessionTracker(_time, TimeSpan.Zero);
        tracker.Update([Game]);

        Assert.Equal(GameState.Idle, tracker.Update([]));
    }

    [Fact]
    public void Shortening_the_grace_mid_countdown_takes_effect()
    {
        var tracker = new GameSessionTracker(_time, TimeSpan.FromMinutes(2));
        tracker.Update([Game]);
        tracker.Update([]);
        _time.Advance(TimeSpan.FromSeconds(30));

        tracker.Grace = TimeSpan.Zero;

        Assert.Equal(GameState.Idle, tracker.Update([]));
    }
}
