using System.Collections.Frozen;
using ConnectionManager.Core.Blocking;
using ConnectionManager.Core.Games;
using ConnectionManager.Core.Logging;
using ConnectionManager.Core.Modes;
using ConnectionManager.Core.Processes;
using ConnectionManager.Core.Settings;
using ConnectionManager.Core.Steam;
using ConnectionManager.Core.Wfp;

namespace ConnectionManager.Core;

/// What the tray shows after each tick.
public sealed record ControllerStatus(
    bool Active, ModeSetting Setting, GameState Game, int BlockedCount, IReadOnlyList<string> RunningGames, string? Error)
{
    public static readonly ControllerStatus Starting = new(false, ModeSetting.Auto, GameState.Idle, 0, [], null);
}

/// The Game Mode loop. Each tick finds running games, decides whether Game Mode is on, and keeps its block rules in step.
/// With an engine it applies them itself. Without one it only plans them (WantedRules), for a caller that combines them
/// with other rules in its own reconciler. Not thread-safe: call everything from one thread.
public sealed class GameModeController : IDisposable
{
    public static readonly TimeSpan RefreshWhileActive = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan LibraryRefresh = TimeSpan.FromMinutes(5);

    private const int MaxLoggedRejections = 256;

    private static readonly IReadOnlySet<FilterRule> NoRules = FrozenSet<FilterRule>.Empty;

    private readonly IProcessSource _processes;
    private readonly IWfpEngine? _engine;
    private readonly FilterReconciler? _reconciler;
    private readonly TimeProvider _time;
    private readonly ILog _log;
    private readonly Func<string?> _steamPath;
    private readonly Func<string, string?> _readFile;
    private readonly string _appFolder;
    private readonly Func<string, string>? _expandEnv;
    private readonly GameSessionTracker _tracker;
    private readonly HashSet<string> _loggedFailures = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _loggedRejections = new(StringComparer.OrdinalIgnoreCase);

    private AppSettings _settings = new();
    private string? _steam;
    private IReadOnlyList<string> _libraries = [];
    private GameClassifier _classifier = new([], [], []);
    private IReadOnlyList<string> _transientGames = [];
    private DateTimeOffset _librariesReadAt = DateTimeOffset.MinValue;
    private DateTimeOffset _resolvedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _retryAt = DateTimeOffset.MinValue;
    private string? _lastError;
    private bool _active;

    public GameModeController(IProcessSource processes, IWfpEngine? engine, TimeProvider time, ILog log,
        Func<string?> steamPath, Func<string, string?> readFile, string appFolder, Func<string, string>? expandEnv = null)
    {
        _processes = processes;
        _engine = engine;
        _reconciler = engine is null ? null : new FilterReconciler(engine);
        _time = time;
        _log = log;
        _steamPath = steamPath;
        _readFile = readFile;
        _appFolder = appFolder;
        _expandEnv = expandEnv;
        _tracker = new GameSessionTracker(time, TimeSpan.FromMinutes(_settings.GraceMinutes));
    }

    public ControllerStatus Status { get; private set; } = ControllerStatus.Starting;

    /// The Game Mode blocks as of the last tick: refreshed every minute while Game Mode is on, empty while it is off.
    public IReadOnlySet<FilterRule> WantedRules { get; private set; } = NoRules;

    /// Steam's folder and its libraries' steamapps\common folders, as found by the last library read (every 5 minutes).
    public string? SteamPath => _steam;
    public IReadOnlyList<string> SteamLibraryFolders => _libraries;

    private int BlockedCount => _reconciler?.Active.Count ?? WantedRules.Count;

    /// Call after every settings change. Blocks are recomputed on the next tick.
    public void ApplySettings(AppSettings settings)
    {
        _settings = settings;
        _tracker.Grace = TimeSpan.FromMinutes(settings.GraceMinutes);
        Reclassify();
    }

    /// Games found outside Steam and checked by the caller: they count like your always list until the next call, and
    /// are never saved. Blocks are recomputed on the next tick, so a new game is never blocked.
    public void SetTransientGames(IReadOnlyList<string> paths)
    {
        _transientGames = paths.ToArray();
        Reclassify();
    }

    private void Reclassify()
    {
        _classifier = NewClassifier();
        _resolvedAt = DateTimeOffset.MinValue;
        _retryAt = DateTimeOffset.MinValue; // a change deserves an immediate attempt
    }

    private GameClassifier NewClassifier() => new(_libraries, [.. _settings.AlwaysGames, .. _transientGames], _settings.NeverGames);

    public ControllerStatus Tick()
    {
        var now = _time.GetUtcNow();
        List<string> games;
        try
        {
            if (now - _librariesReadAt >= LibraryRefresh) ReadLibraries(now);
            games = _processes.GetRunning()
                .Select(p => p.Path)
                .Where(_classifier.IsGame)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) // detection failed (registry, process list): keep the last state and try again next tick
        {
            Remember(ex);
            return Status = Status with { Error = _lastError };
        }
        var game = _tracker.Update(games);
        var active = ModeLogic.IsGameModeActive(_settings.Mode, game);

        var due = active ? !_active || now - _resolvedAt >= RefreshWhileActive : _active;
        if (due && now >= _retryAt)
        {
            try
            {
                if (active) Block(now);
                else Unblock();
                _active = active;
                _lastError = null;
            }
            catch (Exception ex) // the loop boundary: anything that escapes here would skip the back-off below
            {
                // Wait a minute before trying again: no hammering WFP or rescanning folders every tick during a game.
                _retryAt = now + RefreshWhileActive;
                Remember(ex);
            }
        }
        else if (!active && !_active) _lastError = null; // nothing left to retry: don't keep showing an old problem

        return Status = new ControllerStatus(active, _settings.Mode, game, BlockedCount, games, _lastError);
    }

    // Each distinct problem is logged once, so a lasting one doesn't fill the log every tick.
    private void Remember(Exception ex)
    {
        if (ex.Message != _lastError) _log.Error("Game Mode hit a problem", ex);
        _lastError = ex.Message;
    }

    private void ReadLibraries(DateTimeOffset now)
    {
        _steam = _steamPath();
        _libraries = SteamLibraries.FindCommonFoldersSafely(_steam, _readFile); // a normal user can edit Steam's files
        _librariesReadAt = now;
        _classifier = NewClassifier();
    }

    /// True when this program counts as a game: in a Steam library (as of the last tick), on your always list, or found
    /// outside Steam.
    public bool IsGame(string path) => _classifier.IsGame(path);

    /// True when a full path is under Windows, this app, Steam or its libraries, or is voice chat or anti-cheat. Unlike
    /// IsProtected, games don't count.
    public bool IsProtectedPath(string fullPath) => ProtectedPaths.ForThisPc(_steam, _libraries, _appFolder).IsProtected(fullPath);

    /// True when Game Mode would never block this program, or anything in this folder. Uses the Steam libraries
    /// found by the last tick.
    public bool IsProtected(UserBlock block)
    {
        // A folder is checked as "C:\Folder\", which falls under itself, so picking a protected folder exactly counts too.
        var path = block.IsFolder ? PathRules.FolderPrefix(block.Path) : PathRules.TryFull(block.Path, out var full) ? full : null;
        return path is not null && ProtectedCheck()(path);
    }

    private Func<string, bool> ProtectedCheck()
    {
        var protectedPaths = ProtectedPaths.ForThisPc(_steam, _libraries, _appFolder);
        return p => protectedPaths.IsProtected(p) || _classifier.IsGame(p);
    }

    /// The Game Mode blocks for the current settings, whether or not Game Mode is on: the enabled catalog entries and
    /// your blocks, minus anything protected. Uses the Steam libraries found by the last tick, and scans folders.
    public IReadOnlySet<FilterRule> PlanGameBlocks()
    {
        var resolver = new TargetResolver(ProtectedCheck(), _expandEnv);
        var targets = resolver.Resolve(BuiltInCatalog.Enabled(_settings.Catalog), _settings.ExtraBlocks);
        foreach (var folder in resolver.CappedFolders)
            if (_loggedFailures.Add("cap:" + folder))
                _log.Warn($"Not every program under {folder} is blocked: a folder scan stops at {TargetResolver.FolderLimit} programs or {PathPatterns.MaxEntries} files and folders, and all of them together at {TargetResolver.TotalEntryLimit}");
        foreach (var path in resolver.Rejected) // once each for the life of the process, and only so many
            if (_loggedRejections.Count < MaxLoggedRejections && _loggedRejections.Add(path))
                _log.Warn($"Not blocked: {path} is not a plain path on a local drive, or it goes through a link");
        return targets.Select(t => new FilterRule(t.Kind == TargetKind.App ? RuleKind.App : RuleKind.Service, t.Value,
            RuleAction.Block, RuleWeight.GameBlock, t.Label)).ToHashSet();
    }

    private void Block(DateTimeOffset now)
    {
        WantedRules = PlanGameBlocks();
        var result = _reconciler?.Apply(WantedRules);
        _resolvedAt = now;

        if (!_active) _log.Info($"Game Mode on: blocking {BlockedCount} targets");
        else if (result is { Added: > 0 } or { Removed: > 0 })
            _log.Info($"Game Mode blocks updated: +{result.Added} -{result.Removed}, {BlockedCount} in total");
        foreach (var (rule, message) in result?.Failures ?? [])
            if (_loggedFailures.Add(rule.Value)) _log.Warn($"Could not block {rule.Value}: {message}");
    }

    private void Unblock()
    {
        WantedRules = NoRules;
        _reconciler?.Clear();
        _loggedFailures.Clear();
        _log.Info("Game Mode off: all blocks removed");
    }

    public void Dispose()
    {
        try
        {
            _reconciler?.Clear();
        }
        catch (Exception ex)
        {
            _log.Error("Could not remove blocks on exit; closing the session removes them anyway", ex);
        }
        finally
        {
            _engine?.Dispose();
        }
    }
}
