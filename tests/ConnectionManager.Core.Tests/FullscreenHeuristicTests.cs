using ConnectionManager.Core.Games;

namespace ConnectionManager.Core.Tests;

public sealed class FullscreenHeuristicTests
{
    private const string Game = @"D:\Games\Indie\indie.exe";
    private const int Pid = 4242;
    private static readonly DateTimeOffset T0 = new(2026, 10, 9, 20, 0, 0, TimeSpan.Zero);

    private readonly FullscreenHeuristic _heuristic = new([@"C:\Windows"]);

    private static FullscreenSample At(double seconds, double? gpu = 50, int pid = Pid, string? path = Game, bool fullscreen = true,
        int state = 5, bool alive = true) =>
        new(T0.AddSeconds(seconds), pid, path, fullscreen, gpu, state, alive);

    // Samples every 2 s from `from` to `to`, inclusive; the events that came out.
    private List<FullscreenEvent> Run(double from, double to, Func<double, FullscreenSample> sample)
    {
        var events = new List<FullscreenEvent>();
        for (var t = from; t <= to; t += 2)
            if (_heuristic.Update(sample(t)) is { } found) events.Add(found);
        return events;
    }

    private void Confirm()
    {
        Assert.Equal([new FullscreenConfirmed(Pid, Game)], Run(0, 10, t => At(t)));
        Assert.Equal(new FullscreenConfirmed(Pid, Game), _heuristic.Active);
    }

    [Fact]
    public void Ten_seconds_at_20_percent_or_more_confirms()
    {
        Assert.Empty(Run(0, 8, t => At(t, gpu: 20)));

        Assert.Equal(new FullscreenConfirmed(Pid, Game), _heuristic.Update(At(10, gpu: 20)));
        Assert.Null(_heuristic.Update(At(12))); // once
    }

    [Fact]
    public void Nineteen_percent_never_confirms()
    {
        Assert.Empty(Run(0, 120, t => At(t, gpu: 19)));
        Assert.Null(_heuristic.Active);
    }

    [Fact]
    public void A_quiet_sample_starts_the_ten_seconds_again()
    {
        Assert.Empty(Run(0, 8, t => At(t)));
        Assert.Null(_heuristic.Update(At(10, gpu: 5)));
        Assert.Empty(Run(12, 20, t => At(t)));

        Assert.NotNull(_heuristic.Update(At(22)));
    }

    [Fact]
    public void Unknown_gpu_load_confirms_only_on_exclusive_full_screen()
    {
        Assert.Empty(Run(0, 60, t => At(t, gpu: null)));

        Assert.Equal(new FullscreenConfirmed(Pid, Game), _heuristic.Update(At(62, gpu: null, state: FullscreenHeuristic.RunningD3DFullScreen)));
    }

    [Fact]
    public void Exclusive_full_screen_confirms_at_once()
    {
        Assert.Equal(new FullscreenConfirmed(Pid, Game), _heuristic.Update(At(0, gpu: 0, state: FullscreenHeuristic.RunningD3DFullScreen)));
    }

    [Theory]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe")]
    [InlineData(@"C:\Program Files (x86)\Microsoft\Edge\Application\MSEDGE.EXE")]
    [InlineData(@"C:\Program Files\VideoLAN\VLC\vlc.exe")]
    [InlineData(@"C:\Program Files\obs-studio\bin\64bit\obs64.exe")]
    [InlineData(@"C:\Users\Player\AppData\Local\Discord\app-1.0.9200\Discord.exe")]
    [InlineData(@"C:\Program Files (x86)\Steam\bin\cef\cef.win7x64\steamwebhelper.exe")]
    [InlineData(@"C:\Program Files\ConnectionManager\ConnectionManager.exe")]
    [InlineData(@"C:\Windows\System32\mstsc.exe")] // under an excluded folder
    public void An_excluded_app_never_confirms(string path)
    {
        Assert.Empty(Run(0, 60, t => At(t, gpu: 95, path: path)));
        Assert.Null(_heuristic.Update(At(62, path: path, state: FullscreenHeuristic.RunningD3DFullScreen)));
        Assert.True(_heuristic.IsExcluded(path));
    }

    [Fact]
    public void A_window_that_does_not_fill_its_monitor_never_confirms()
    {
        Assert.Empty(Run(0, 60, t => At(t, gpu: 95, fullscreen: false)));
        Assert.False(_heuristic.IsCandidate(Pid, Game, isFullscreen: false, notificationState: 5));
        Assert.True(_heuristic.IsCandidate(Pid, Game, isFullscreen: true, notificationState: 5));
    }

    [Fact]
    public void Another_program_in_front_starts_the_ten_seconds_again()
    {
        const string other = @"D:\Games\Other\other.exe";
        Assert.Empty(Run(0, 8, t => At(t)));
        Assert.Null(_heuristic.Update(At(10, pid: 7, path: other)));

        Assert.Empty(Run(12, 18, t => At(t)));
    }

    [Fact]
    public void Alt_tab_for_less_than_30_seconds_keeps_the_game_and_31_releases_it()
    {
        Confirm();

        Assert.Null(_heuristic.Update(At(20, pid: 7, path: @"C:\Program Files\Discord\Discord.exe", fullscreen: false)));
        Assert.Null(_heuristic.Update(At(49, pid: 0, path: null, fullscreen: false))); // 29 s away
        Assert.Null(_heuristic.Update(At(51)));                                         // back in front
        Assert.Equal(new FullscreenConfirmed(Pid, Game), _heuristic.Active);

        Assert.Null(_heuristic.Update(At(60, pid: 7, fullscreen: false)));
        Assert.Equal(new FullscreenReleased(Game), _heuristic.Update(At(91, pid: 7, fullscreen: false))); // 31 s away
        Assert.Null(_heuristic.Active);
    }

    [Fact]
    public void While_a_game_is_confirmed_another_one_waits()
    {
        Confirm();

        Assert.Empty(Run(12, 40, t => At(t, pid: 9, path: @"D:\Games\Other\other.exe")));
    }

    [Fact]
    public void The_process_exiting_releases_the_game()
    {
        Confirm();

        Assert.Equal(new FullscreenReleased(Game), _heuristic.Update(At(12, alive: false)));
        Assert.Null(_heuristic.Active);
        Assert.Empty(Run(14, 22, t => At(t))); // found again from scratch: 10 s more
        Assert.NotNull(_heuristic.Update(At(24)));
    }

    [Fact]
    public void A_locked_screen_is_skipped()
    {
        // Locked: no confirm, however busy.
        Assert.Empty(Run(0, 60, t => At(t, gpu: 99, state: FullscreenHeuristic.NotPresent)));

        Confirm();
        // Ten minutes locked, after 20 s away: neither counts as away time...
        Assert.Null(_heuristic.Update(At(20, pid: 0, path: null, fullscreen: false)));
        Assert.Null(_heuristic.Update(At(40, pid: 0, path: null, fullscreen: false)));
        Assert.Empty(Run(42, 640, t => At(t, pid: 0, path: null, state: FullscreenHeuristic.NotPresent)));
        Assert.Null(_heuristic.Update(At(642, pid: 0, path: null, fullscreen: false)));
        Assert.Null(_heuristic.Update(At(650, pid: 0, path: null, fullscreen: false)));
        Assert.Equal(new FullscreenConfirmed(Pid, Game), _heuristic.Active);
        // ...but the 20 s before it still do, and the game exiting while locked still releases it.
        Assert.Equal(new FullscreenReleased(Game), _heuristic.Update(At(652, pid: 0, path: null, state: FullscreenHeuristic.NotPresent, alive: false)));
    }

    [Fact]
    public void Reset_forgets_everything()
    {
        Confirm();

        _heuristic.Reset();

        Assert.Null(_heuristic.Active);
        Assert.Empty(Run(20, 28, t => At(t)));
    }
}
