using System.Collections.Frozen;

namespace ConnectionManager.Core.Games;

/// One look at the foreground, taken every 2 s by the tray. Pid is 0 when there is no foreground window (or it is the
/// tray's own); Path is null when its program can't be read. Gpu3dPercent: that process's 3D engine load, 0 to 100,
/// null when unknown. NotificationState: SHQueryUserNotificationState's value. ProcessAlive: the confirmed game
/// (FullscreenHeuristic.Active) is still running; ignored while none is confirmed.
public sealed record FullscreenSample(
    DateTimeOffset At, int Pid, string? Path, bool IsFullscreen, double? Gpu3dPercent, int NotificationState, bool ProcessAlive);

public abstract record FullscreenEvent(string Path);

/// A game outside Steam was found: report it to the service.
public sealed record FullscreenConfirmed(int Pid, string Path) : FullscreenEvent(Path);

/// The confirmed game ended or stayed out of the foreground: report pid 0 with its path.
public sealed record FullscreenReleased(string Path) : FullscreenEvent(Path);

/// Decides from the samples whether the foreground program is a game. A program that fills its monitor and keeps the
/// 3D engine at 20% or more for 10 s is one, and so is one in exclusive full screen at once. Browsers, video players
/// and the like never are. A confirmed game is released when its process ends, or after 30 s out of the foreground,
/// so an alt-tab to voice chat keeps it. One game at a time. Pure; one thread only.
public sealed class FullscreenHeuristic
{
    public const double MinGpuPercent = 20;
    public static readonly TimeSpan ConfirmAfter = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan ReleaseAfter = TimeSpan.FromSeconds(30);

    /// QUNS_NOT_PRESENT: locked, screen saver or another user's session. Such a sample is skipped: it neither builds
    /// up a candidate nor counts as time away, but a confirmed game whose process ended is still released.
    public const int NotPresent = 1;

    /// QUNS_RUNNING_D3D_FULL_SCREEN: a Direct3D program in exclusive full screen, which is always in front.
    public const int RunningD3DFullScreen = 3;

    // Programs that fill the screen and use the GPU without being games, and the shell's own windows.
    private static readonly FrozenSet<string> ExcludedNames = new[]
    {
        "chrome.exe", "msedge.exe", "firefox.exe", "opera.exe", "opera_gx.exe", "brave.exe", "vivaldi.exe",
        "vlc.exe", "mpv.exe", "mpc-hc.exe", "mpc-hc64.exe", "mpc-be.exe", "mpc-be64.exe", "PotPlayerMini64.exe", "PotPlayerMini.exe",
        "wmplayer.exe", "Video.UI.exe", "Microsoft.Media.Player.exe",
        "obs64.exe", "Discord.exe", "Spotify.exe", "ms-teams.exe", "Zoom.exe",
        "steam.exe", "steamwebhelper.exe", "EpicGamesLauncher.exe",
        "explorer.exe", "msedgewebview2.exe", "ApplicationFrameHost.exe", "ShellExperienceHost.exe", "StartMenuExperienceHost.exe",
        "SearchHost.exe", "LockApp.exe",
        "ConnectionManager.exe", "ConnectionManager.Service.exe",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private readonly string[] _excludedFolders; // each ends with '\'

    private int _candidatePid;
    private string? _candidatePath;
    private DateTimeOffset? _busySince;
    private TimeSpan _away;
    private DateTimeOffset? _awayMark; // the last sample counted as away, so the time since it counts too

    /// excludedFolders: programs under these never count (the tray passes the Windows folder, which the service
    /// refuses anyway).
    public FullscreenHeuristic(IEnumerable<string?>? excludedFolders = null) =>
        _excludedFolders = (excludedFolders ?? []).Select(PathRules.FolderPrefix).OfType<string>().ToArray();

    /// The confirmed game, until it is released.
    public FullscreenConfirmed? Active { get; private set; }

    public bool IsExcluded(string path) =>
        ExcludedNames.Contains(Path.GetFileName(path)) || _excludedFolders.Any(f => PathRules.IsUnder(path, f));

    /// Whether this foreground program could become the game, so its GPU load is worth sampling.
    public bool IsCandidate(int pid, string? path, bool isFullscreen, int notificationState) =>
        Active is null && pid > 0 && path is not null && notificationState != NotPresent
        && (isFullscreen || notificationState == RunningD3DFullScreen) && !IsExcluded(path);

    public FullscreenEvent? Update(FullscreenSample sample) => Active is { } game ? Follow(game, sample) : Look(sample);

    /// Forgets the candidate and the confirmed game, without an event (detection was turned off).
    public void Reset()
    {
        Active = null;
        ForgetCandidate();
        (_away, _awayMark) = (TimeSpan.Zero, null);
    }

    private FullscreenEvent? Look(FullscreenSample s)
    {
        if (!IsCandidate(s.Pid, s.Path, s.IsFullscreen, s.NotificationState))
        {
            ForgetCandidate();
            return null;
        }
        var path = s.Path!;
        if (s.Pid != _candidatePid || !string.Equals(path, _candidatePath, StringComparison.OrdinalIgnoreCase))
            (_candidatePid, _candidatePath, _busySince) = (s.Pid, path, null);

        if (s.NotificationState == RunningD3DFullScreen) return Confirm(s.Pid, path);
        if (s.Gpu3dPercent is not >= MinGpuPercent)
        {
            _busySince = null; // quiet, or unknown: the 10 s start again
            return null;
        }
        _busySince ??= s.At;
        return s.At - _busySince >= ConfirmAfter ? Confirm(s.Pid, path) : null;
    }

    private FullscreenEvent? Follow(FullscreenConfirmed game, FullscreenSample s)
    {
        if (!s.ProcessAlive) return Release(game);
        if (s.NotificationState == NotPresent)
        {
            _awayMark = null; // the locked time is not away time
            return null;
        }
        if (s.Pid == game.Pid)
        {
            (_away, _awayMark) = (TimeSpan.Zero, null);
            return null;
        }
        if (_awayMark is { } mark) _away += s.At - mark;
        _awayMark = s.At;
        return _away >= ReleaseAfter ? Release(game) : null;
    }

    private FullscreenConfirmed Confirm(int pid, string path)
    {
        ForgetCandidate();
        (_away, _awayMark) = (TimeSpan.Zero, null);
        return Active = new FullscreenConfirmed(pid, path);
    }

    private FullscreenReleased Release(FullscreenConfirmed game)
    {
        Reset();
        return new FullscreenReleased(game.Path);
    }

    private void ForgetCandidate() => (_candidatePid, _candidatePath, _busySince) = (0, null, null);
}
