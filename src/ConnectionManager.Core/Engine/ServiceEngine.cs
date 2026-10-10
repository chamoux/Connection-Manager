using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using ConnectionManager.Core.Blocking;
using ConnectionManager.Core.Ipc;
using ConnectionManager.Core.Logging;
using ConnectionManager.Core.Modes;
using ConnectionManager.Core.Network;
using ConnectionManager.Core.Processes;
using ConnectionManager.Core.Settings;
using ConnectionManager.Core.Wfp;

namespace ConnectionManager.Core.Engine;

/// What the engine is built from. SteamPath and ReadFile must already apply SafePaths (SteamLibraries.ReadSteamPathForUser,
/// ReadUntrustedFile), and ReadFile must not wait long on a file a user holds open (TimedFileReader); ExpandEnv names the
/// signed-in user's folders (ActiveUser.ExpandUserEnv). Parts left null are not wired yet, and their commands answer
/// "Not available yet."
public sealed record EngineParts(
    IWfpEngine Wfp,
    IProcessSource Processes,
    INetworkSource Network,
    Func<string?> SteamPath,
    Func<string, string?> ReadFile,
    Func<string, string> ExpandEnv,
    string AppFolder)
{
    public IDataSaverPlanner Saver { get; init; } = NoDataSaverRules.Instance;
    public ICapController? Caps { get; init; }
    public IWifiController? Wifi { get; init; }
    public ITrafficMeter? Meter { get; init; }
    public IReportSource? Reports { get; init; }
    public IMeteredToggle? Metered { get; init; }
    public IUploadCaps? UploadCaps { get; init; }

    /// A process as the service itself sees it (SystemProcessSource.Describe), to check a game the tray reports.
    public Func<int, ProcessFacts?>? ProcessLookup { get; init; }
    public RestoreHooks? Restore { get; init; }
    public string? ServiceExe { get; init; } = Environment.ProcessPath;

    /// A problem from loading settings at start (a failed save), shown until a save works.
    public string? SettingsProblem { get; init; }

    public EngineWatchdog Watchdog { get; init; } = EngineWatchdog.Default;
}

/// What happens when one piece of engine work runs longer than StallAfter: OnStall, which in the service ends the
/// process (Environment.FailFast). The WFP session is dynamic, so every filter goes with the process (fail open), and the
/// service manager starts it again. Time is the watchdog's own clock, never the engine's.
public sealed record EngineWatchdog(TimeSpan StallAfter, Action<string> OnStall, TimeProvider Time)
{
    public static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(10);

    public static readonly EngineWatchdog Default = new(TimeSpan.FromSeconds(90), Environment.FailFast, TimeProvider.System);
}

/// The service's single-threaded core. One loop runs every piece of work in order: a tick every 2 s, and each pipe
/// command. A tick detects games, reads the network, decides Game Mode and Data Saver, applies the union of their rules
/// to the one WFP reconciler, drives the other parts, and publishes the status when anything visible changed.
/// Owns the WFP engine: disposing removes every filter and closes the session. Nothing slow runs on the loop (upload
/// caps go to a worker, folder scans and Steam's files are bounded), and a watchdog ends the process if a piece of work
/// is stuck anyway, since a stuck loop would keep its filters in place.
public sealed partial class ServiceEngine : IAsyncDisposable
{
    public static readonly TimeSpan TickEvery = TimeSpan.FromSeconds(2);

    /// Phase 1's back-off after a failed filter update, and how often the rules are re-applied while any are wanted
    /// (which retries a rule that failed on its own, such as a program that was being updated).
    public static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(60);

    private const int QueueLength = 64;          // 7 pipe connections at most, each one request at a time, plus ticks
    private const int MaxProblems = 20;
    private const int MaxProblemLength = 512;
    private const int MaxRunningGamesShown = 50;
    private const int MaxLoggedErrors = 256;
    private const string UploadCapsPart = "Upload caps";
    private const string Stalled = "Connection Manager engine stalled";
    private const long Idle = long.MinValue;

    /// How long stopping waits for the upload worker to remove the caps. The ActiveStore is memory only, so caps a stuck
    /// QoS keeps go at the next start or reboot.
    private static readonly TimeSpan UploadStopWait = TimeSpan.FromSeconds(20);

    private static readonly IReadOnlySet<FilterRule> NoRules = FrozenSet<FilterRule>.Empty;

    private readonly EngineParts _parts;
    private readonly SettingsStore _store;
    private readonly Action<StatusDto> _publish;
    private readonly TimeProvider _time;
    private readonly ILog _log;
    private readonly TickProcesses _processes;
    private readonly GameModeController _controller;
    private readonly FilterReconciler _reconciler;
    private readonly RestoreHooks _restore;
    private readonly Channel<Func<Task>> _work = Channel.CreateBounded<Func<Task>>(
        new BoundedChannelOptions(QueueLength) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Func<Task> _tickWork;
    private readonly HashSet<string> _loggedErrors = new(StringComparer.Ordinal);
    private readonly HashSet<string> _loggedRuleFailures = new(StringComparer.OrdinalIgnoreCase);
    private readonly LatestWorker _uploads = new();

    private ITimer? _timer;
    private ITimer? _watchdog;
    private long _workStartedAt = Idle; // the watchdog clock's timestamp when the running work began
    private int _stalled;
    private Task _loop = Task.CompletedTask;
    private int _started;
    private int _tickQueued;
    private int _disposed;
    private StatusDto? _status;

    // Engine thread only.
    private AppSettings _settings;
    private IReadOnlySet<string> _hotspots = FrozenSet<string>.Empty;
    private ControllerStatus _game = ControllerStatus.Starting;
    private NetworkSnapshot _network = NetworkSnapshot.None;
    private bool _saverActive;
    private string _saverReason = "";
    private IReadOnlySet<FilterRule> _lastWanted = NoRules;
    private DateTimeOffset _appliedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _retryAt = DateTimeOffset.MinValue;
    private string? _filterError;
    private string? _settingsProblem;
    private string? _lastSaveError;
    private string? _saverProblem, _capProblem, _wifiProblem, _meterProblem, _reportProblem, _uploadProblem;
    private byte[] _statusJson = [];
    private long _version;

    /// `settings` are the loaded ones (ServiceStartup.LoadSettings); every change is saved to `store`. `publish` gets
    /// each new status, on the engine thread (PipeServerHost.Publish).
    public ServiceEngine(EngineParts parts, AppSettings settings, SettingsStore store, Action<StatusDto> publish, TimeProvider time, ILog log)
    {
        _parts = parts;
        _settings = settings;
        _store = store;
        _publish = publish;
        _time = time;
        _log = log;
        _processes = new TickProcesses(parts.Processes);
        // Planning only: the engine applies Game Mode's rules together with Data Saver's, in one reconciler.
        _controller = new GameModeController(_processes, engine: null, time, log, parts.SteamPath, parts.ReadFile, parts.AppFolder, parts.ExpandEnv);
        _reconciler = new FilterReconciler(parts.Wfp);
        _restore = parts.Restore ?? new RestoreHooks([], log);
        _settingsProblem = parts.SettingsProblem;
        _tickWork = () =>
        {
            Volatile.Write(ref _tickQueued, 0);
            Tick();
            return Task.CompletedTask;
        };
        ApplySettings();
    }

    /// The latest published status, or null before the first tick. Readable from any thread.
    public StatusDto? Status => Volatile.Read(ref _status);

    /// The loop. It ends only after DisposeAsync; ending sooner means the engine is broken.
    public Task Completion => _loop;

    /// Starts the loop and the 2 s tick, the first one at once. Once.
    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("The engine is already running.");
        _loop = Task.Run(LoopAsync);
        _watchdog = _parts.Watchdog.Time.CreateTimer(_ => CheckStall(), null, EngineWatchdog.CheckEvery, EngineWatchdog.CheckEvery);
        // First in the queue, before any tick or command: the QoS policies an earlier run left go, the saved caps come back.
        if (_parts.UploadCaps is not null) _work.Writer.TryWrite(() =>
        {
            QueueUploadCaps(clearFirst: true);
            return Task.CompletedTask;
        });
        _timer = _time.CreateTimer(_ => QueueTick(), null, TimeSpan.Zero, TickEvery);
    }

    /// Asks for a tick soon instead of at the next 2 s period, for a part that saw a change on another thread (the
    /// Wi-Fi tuner's reconnect notification). Safe from any thread; never blocks or throws; does nothing once stopped.
    public void Wake()
    {
        if (Volatile.Read(ref _disposed) == 0) QueueTick();
    }

    /// Runs a tick on the loop and waits for it, after anything already queued.
    public Task TickAsync(CancellationToken ct = default) => RunOnLoopAsync(() =>
    {
        Tick();
        return true;
    }, ct);

    /// The pipe's request handler: the work runs on the loop, in order with ticks and other commands.
    public async Task<Response> HandleAsync(ClientIdentity client, Request request, CancellationToken ct)
    {
        try
        {
            return await RunOnLoopAsync(() => Handle(client, request), ct);
        }
        catch (ChannelClosedException)
        {
            return Refuse(request, "The service is stopping.");
        }
    }

    /// Stops the loop after the work already queued, then fails open: every filter goes first, then the WFP session,
    /// then the other parts, and last the journaled changes are put back.
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_timer is not null) await _timer.DisposeAsync();
        _work.Writer.TryComplete();
        await _loop;
        if (_watchdog is not null) await _watchdog.DisposeAsync();

        try
        {
            _reconciler.Clear();
        }
        catch (Exception ex)
        {
            _log.Error("Could not remove the filters on stop; closing the WFP session removes them anyway", ex);
        }
        finally
        {
            Quietly("closing the WFP session", _parts.Wfp.Dispose);
        }
        Quietly("undoing the Wi-Fi scan pause", () => _parts.Wifi?.Dispose());
        Quietly("stopping the traffic meter", () => _parts.Meter?.Dispose());
        Quietly("stopping the network watcher", () => (_parts.Network as IDisposable)?.Dispose());
        await RemoveUploadCapsAsync();
        _controller.Dispose();
        _restore.RunAll("at stop", includeUserChoices: false);
    }

    private void QueueTick()
    {
        // The timer callback: one waiting tick is enough, and nothing here can throw.
        if (Interlocked.Exchange(ref _tickQueued, 1) != 0) return;
        if (!_work.Writer.TryWrite(_tickWork)) Volatile.Write(ref _tickQueued, 0); // full or closed: the next period tries again
    }

    private async Task LoopAsync()
    {
        await foreach (var work in _work.Reader.ReadAllAsync())
        {
            Interlocked.Exchange(ref _workStartedAt, _parts.Watchdog.Time.GetTimestamp());
            try
            {
                await work();
            }
            catch (Exception ex) // each item handles its own errors; this keeps the loop alive whatever happens
            {
                LogOnce("loop " + ex.GetType().Name + ex.Message, "An engine task failed", ex);
            }
            finally
            {
                Interlocked.Exchange(ref _workStartedAt, Idle);
            }
        }
    }

    // The watchdog's timer, off the loop. Nothing on the loop should take long (the slowest, a folder scan of the most
    // entries allowed, takes seconds), so work still running after StallAfter is stuck: a filter call that never
    // returns, say. The loop would then keep its filters in place forever, so the process ends instead (fail open).
    private void CheckStall()
    {
        var watchdog = _parts.Watchdog;
        var started = Interlocked.Read(ref _workStartedAt);
        if (started == Idle || watchdog.Time.GetElapsedTime(started) < watchdog.StallAfter) return;
        if (Interlocked.Exchange(ref _stalled, 1) != 0) return;
        try
        {
            // Best effort, and on another thread: the stuck work may hold the log's lock.
            Task.Run(() => _log.Error(Invariant($"{Stalled}: one task has run for over {watchdog.StallAfter.TotalSeconds:0} s. Ending the process, which removes every filter; the service manager starts it again.")))
                .Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception) // the log is not worth staying stuck for
        {
        }
        watchdog.OnStall(Stalled);
    }

    private async Task<T> RunOnLoopAsync<T>(Func<T> work, CancellationToken ct)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _work.Writer.WriteAsync(() =>
        {
            try
            {
                done.TrySetResult(work());
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
            return Task.CompletedTask;
        }, ct);
        return await done.Task.WaitAsync(ct);
    }

    private void ApplySettings()
    {
        _controller.ApplySettings(_settings);
        DropTransientGamesIfOff();
        _hotspots = _settings.HotspotNetworks.ToFrozenSet(StringComparer.Ordinal); // network names are case-sensitive
        _retryAt = DateTimeOffset.MinValue; // a change deserves an immediate attempt
    }

    private void Tick()
    {
        try
        {
            TickCore();
        }
        catch (Exception ex) // the loop boundary: the next tick starts afresh
        {
            LogOnce("tick " + ex.GetType().Name + ex.Message, "An engine tick failed", ex);
        }
    }

    private void TickCore()
    {
        var now = _time.GetUtcNow();
        _processes.Clear();
        PruneTransientGames();
        _game = _controller.Tick();
        ReadNetwork(now);

        var (saverActive, saverReason) = DecideSaver(_settings.DataSaver, _network, _hotspots, _settings.AutoOnMeteredWifi);
        if (saverActive != _saverActive) _log.Info($"Data Saver {(saverActive ? "on" : "off")}: {saverReason}");
        (_saverActive, _saverReason) = (saverActive, saverReason);

        var context = new TickContext(now, _settings, _network, RunningOrNone(), _game, saverActive, _controller.SteamPath,
            _controller.SteamLibraryFolders, path => _controller.IsProtected(new UserBlock(path, false)), _parts.ServiceExe);
        var wanted = new HashSet<FilterRule>(_controller.WantedRules);
        wanted.UnionWith(saverActive ? PlanSaver(context) : NoRules);
        if (!saverActive) _saverProblem = null;
        Reconcile(wanted, now);

        _capProblem = _parts.Caps is { } caps ? Call("Windows download caps", () => caps.Update(context)) : null;
        _wifiProblem = _parts.Wifi is { } wifi ? Call("Wi-Fi tuning", () => wifi.Update(context)) : null;
        _meterProblem = _parts.Meter is { } meter ? Call("The traffic meter", () =>
        {
            meter.Enabled = _settings.TrafficMeter;
            meter.Tick(now);
            return meter.Problem;
        }) : null;
        _reportProblem = _parts.Reports is { } reports ? Call("Post-game reports", () => reports.Update(context)) : null;

        PublishIfChanged();
    }

    /// Data Saver's state: On and Off as set; Auto follows the hotspot classifier.
    internal static (bool Active, string Reason) DecideSaver(ModeSetting mode, NetworkSnapshot network, IReadOnlySet<string> hotspots, bool autoOnMeteredWifi) =>
        mode switch
        {
            ModeSetting.On => (true, "On (manual)"),
            ModeSetting.Off => (false, "Off"),
            _ => HotspotClassifier.Classify(network, hotspots, autoOnMeteredWifi),
        };

    private void ReadNetwork(DateTimeOffset now)
    {
        try
        {
            _parts.Network.Refresh(now);
            _network = _parts.Network.Current;
        }
        catch (Exception ex) // NetworkWatcher never throws; keep the last snapshot if another source does
        {
            LogOnce("network " + ex.Message, "Could not read the network", ex);
        }
    }

    private IReadOnlyList<RunningProcess> RunningOrNone()
    {
        try
        {
            return _processes.GetRunning();
        }
        catch (Exception) // game detection already reported it
        {
            return [];
        }
    }

    private IReadOnlySet<FilterRule> PlanSaver(TickContext context)
    {
        try
        {
            var rules = _parts.Saver.Plan(context);
            _saverProblem = null;
            return rules;
        }
        catch (Exception ex)
        {
            LogOnce("saver " + ex.Message, "Data Saver could not plan its rules", ex);
            _saverProblem = $"Data Saver hit a problem: {ex.Message}";
            return NoRules;
        }
    }

    // Applies the union when it changed, and again every minute while any rule is wanted. A failure leaves the old filters
    // in place (the reconciler rolls back) and waits a minute before the next attempt, unless a command changes something.
    private void Reconcile(IReadOnlySet<FilterRule> wanted, DateTimeOffset now)
    {
        var due = !wanted.SetEquals(_lastWanted) || (wanted.Count > 0 && now - _appliedAt >= RetryAfter);
        if (!due || now < _retryAt) return;
        try
        {
            var result = _reconciler.Apply(wanted);
            _lastWanted = wanted;
            _appliedAt = now;
            _filterError = null;
            if (result.Added > 0 || result.Removed > 0)
                _log.Info($"Filters updated: +{result.Added} -{result.Removed}, {_reconciler.Active.Count} in place");
            foreach (var (rule, message) in result.Failures)
                if (_loggedRuleFailures.Count < MaxLoggedErrors && _loggedRuleFailures.Add($"{rule.Action} {rule.Kind} {rule.Value}"))
                    _log.Warn($"Could not {(rule.Action == RuleAction.Block ? "block" : "allow")} {rule.Value}: {message}");
            if (wanted.Count == 0) _loggedRuleFailures.Clear();
        }
        catch (Exception ex)
        {
            _retryAt = now + RetryAfter; // no hammering WFP every 2 s
            if (ex.Message != _filterError) _log.Error("Could not update the network filters", ex);
            _filterError = ex.Message;
        }
    }

    // At start and after a change to the caps or their switch only. Each call goes to Windows QoS, which can take many
    // seconds, so it runs on the upload worker with a copy of the caps; a newer request overtakes one not yet started.
    // The result comes back to the loop for the status.
    private void QueueUploadCaps(bool clearFirst = false)
    {
        if (_parts.UploadCaps is not { } caps) return;
        var wanted = new AppSettings { UploadCaps = _settings.UploadCaps.ToList(), UploadCapsEnabled = _settings.UploadCapsEnabled };
        _uploads.Post(() =>
        {
            var removed = clearFirst ? Attempt(caps.RemoveAll) : default;
            var applied = Attempt(() => caps.Apply(wanted));
            _work.Writer.TryWrite(() => // closed once stopping: the result no longer matters
            {
                var (cleared, set) = (UploadProblem(removed), UploadProblem(applied));
                _uploadProblem = cleared is null || cleared == set ? set : set is null ? cleared : $"{cleared} {set}";
                PublishIfChanged();
                return Task.CompletedTask;
            });
        });
    }

    private static (string? Problem, Exception? Error) Attempt(Func<string?> work)
    {
        try
        {
            return (work(), null);
        }
        catch (Exception ex)
        {
            return (null, ex);
        }
    }

    private string? UploadProblem((string? Problem, Exception? Error) outcome)
    {
        if (outcome.Error is not { } ex) return outcome.Problem;
        LogOnce(UploadCapsPart + ex.Message, $"{UploadCapsPart} hit a problem", ex);
        return $"{UploadCapsPart} hit a problem: {ex.Message}";
    }

    // The last job: it overtakes any apply not yet started. UploadCapManager logs its own problems.
    private async Task RemoveUploadCapsAsync()
    {
        if (_parts.UploadCaps is not { } caps) return;
        _uploads.Post(() =>
        {
            if (Attempt(caps.RemoveAll).Error is { } ex) _log.Error("Failed removing the upload caps", ex);
        });
        try
        {
            await _uploads.WhenIdleAsync().WaitAsync(UploadStopWait);
        }
        catch (TimeoutException)
        {
            _log.Warn(Invariant($"Windows QoS did not remove the upload caps within {UploadStopWait.TotalSeconds:0} s; the next start or a restart of Windows removes them"));
        }
    }

    /// For tests: waits until the upload worker is idle and its last result has reached the status.
    internal async Task UploadCapsSettledAsync()
    {
        await _uploads.WhenIdleAsync();
        await RunOnLoopAsync(() => true, CancellationToken.None);
    }

    private string? Call(string part, Func<string?> work)
    {
        try
        {
            return work();
        }
        catch (Exception ex)
        {
            LogOnce(part + ex.Message, $"{part} hit a problem", ex);
            return $"{part} hit a problem: {ex.Message}";
        }
    }

    // Version goes up only when something a client would see changed, so a quiet tick sends nothing.
    private void PublishIfChanged()
    {
        StatusDto status;
        byte[] json;
        try
        {
            status = BuildStatus();
            json = JsonSerializer.SerializeToUtf8Bytes(status, IpcJson.Wire.StatusDto); // as large as it goes out
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            LogOnce("status " + ex.Message, "Could not build the status", ex);
            return;
        }
        if (_status is not null && json.AsSpan().SequenceEqual(_statusJson)) return;
        _statusJson = json;
        var published = status with { Version = ++_version };
        Volatile.Write(ref _status, published);
        try
        {
            _publish(published);
        }
        catch (Exception ex)
        {
            LogOnce("publish " + ex.Message, "Could not publish the status", ex);
        }
    }

    private StatusDto BuildStatus()
    {
        var s = _settings;
        var (canMark, marked) = Guarded(() => _parts.Metered?.Describe(_network)) ?? (false, false);
        var blocked = _reconciler.Active.Count(r => r is { Action: RuleAction.Block, Weight: RuleWeight.GameBlock });
        // AllowedCount is the user's allowed apps (Settings.AllowApps), whether or not Data Saver is on; the automatic
        // allows (Steam, games, anti-cheat, security software, essentials) are not counted.
        return new StatusDto(0,
            s.Mode, _game.Active, _game.Game.GameName, _game.Game.GraceEndsAt, _game.RunningGames.Take(MaxRunningGamesShown).ToArray(), blocked,
            s.DataSaver, _saverActive, _saverReason, s.AllowApps.Count,
            _network.ToDto(_hotspots, s.AutoOnMeteredWifi, canMark, marked),
            Problems(),
            new SettingsDto(Options(s),
                BuiltInCatalog.Entries.Select(e => new CatalogItemDto(e.Id, e.Name, BuiltInCatalog.IsOn(e, s.Catalog))).ToArray(),
                s.ExtraBlocks.ToArray(), s.AlwaysGames.ToArray(), s.NeverGames.ToArray(), s.AllowApps.ToArray(), s.HotspotNetworks.ToArray(),
                s.UploadCaps.ToArray()),
            Guarded(() => _parts.Reports?.Latest));
    }

    internal static OptionsDto Options(AppSettings s) => new(s.GraceMinutes, s.Notifications, s.Hotkey, s.AutoOnMeteredWifi,
        s.AllowWindowsDownloads, s.WindowsDownloadCapKBps, s.WifiTuning, s.TrafficMeter, s.DetectNonSteamGames, s.UploadCapsEnabled);

    private string[] Problems()
    {
        List<string?> problems = [_settingsProblem, .. _restore.Problems];
        if (_game.Error is { } detection) problems.Add($"Game Mode hit a problem: {detection}");
        if (_filterError is { } filters) problems.Add($"Could not update the network filters: {filters}");
        problems.AddRange([_saverProblem, _capProblem, _wifiProblem, _meterProblem, _reportProblem, _uploadProblem]);
        return problems.OfType<string>().Take(MaxProblems).Select(p => p.Length > MaxProblemLength ? p[..MaxProblemLength] + "…" : p).ToArray();
    }

    private T? Guarded<T>(Func<T?> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            LogOnce("read " + ex.Message, "A status value could not be read", ex);
            return default;
        }
    }

    private void Quietly(string what, Action work)
    {
        try
        {
            work();
        }
        catch (Exception ex)
        {
            _log.Error($"Failed {what}", ex);
        }
    }

    // Each distinct problem is logged once, and only so many: a lasting one must not fill the log every 2 s.
    private void LogOnce(string key, string message, Exception ex)
    {
        if (_loggedErrors.Count < MaxLoggedErrors && _loggedErrors.Add(key)) _log.Error(message, ex);
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    /// The process list read once per tick: game detection and the Data Saver planner share the snapshot.
    private sealed class TickProcesses(IProcessSource source) : IProcessSource
    {
        private IReadOnlyList<RunningProcess>? _running;

        public void Clear() => _running = null;

        public IReadOnlyList<RunningProcess> GetRunning() => _running ??= source.GetRunning();
    }
}
