using ConnectionManager.Core.Engine;
using ConnectionManager.Core.Ipc;
using ConnectionManager.Core.Logging;

namespace ConnectionManager.Core.Traffic;

/// Counts network bytes per program for the usage list, the post-game report and the Data Saver app picker.
/// Never throws: a source that can't start or dies shows as Problem and is retried a minute later.
/// Call Enabled, Tick and Dispose from one thread; Top and Snapshot are safe from any thread.
public sealed class TrafficMeter : ITrafficMeter
{
    public static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(60);

    private const int MaxLoggedErrors = 32;

    private readonly ILog _log;
    private readonly TimeProvider _time;
    private readonly Func<Action<string, long, long>, ITrafficSource> _createSource;
    private readonly UsageAggregator _usage;
    private readonly HashSet<string> _loggedErrors = new(StringComparer.Ordinal);
    private ITrafficSource? _source;
    private DateTimeOffset _retryAt;
    private bool _enabled;
    private bool _disposed;

    /// `createSource` gets the sink to feed; the default is the real ETW session.
    public TrafficMeter(ILog log, TimeProvider? time = null, Func<Action<string, long, long>, ITrafficSource>? createSource = null)
    {
        _log = log;
        _time = time ?? TimeProvider.System;
        _createSource = createSource ?? (sink => new EtwTrafficSource(sink));
        _usage = new UsageAggregator(_time);
    }

    /// Why the meter isn't counting, for the status Problems list; null while it runs or is off.
    public string? Problem { get; private set; }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_disposed || value == _enabled) return;
            _enabled = value;
            if (value)
            {
                TryStart(_time.GetUtcNow()); // turning it on deserves an attempt now, whatever the retry clock says
                return;
            }
            StopSource();
            Problem = null;
        }
    }

    /// Call from the engine loop. Restarts a dead source a minute after it died or failed to start.
    public void Tick(DateTimeOffset now)
    {
        if (_disposed || !_enabled) return;
        try
        {
            if (_source is { } source)
            {
                if (source.FirstHandlerError is { } bad) LogOnce("bad event: " + bad.Message, () => _log.Warn($"Traffic meter skipped an event it could not read: {bad.Message}"));
                if (source.Fault is not { } fault) return;
                Fail($"The traffic meter stopped: {fault.Message}", "Traffic meter stopped", fault);
                StopSource();
                _retryAt = now + RetryAfter;
                return;
            }
            if (now >= _retryAt) TryStart(now);
        }
        catch (Exception ex) // the engine's timer must never see an exception from here
        {
            Fail($"The traffic meter hit a problem: {ex.Message}", "Traffic meter hit a problem", ex);
        }
    }

    /// The n programs that used the most data in the last 24 hours.
    public IReadOnlyList<UsageDto> Top(int n) => _usage.Top(n);

    /// Bytes per program since the meter was created; diff two snapshots for a game session.
    public IReadOnlyDictionary<string, long> Snapshot() => _usage.Snapshot();

    private void TryStart(DateTimeOffset now)
    {
        ITrafficSource? source = null;
        try
        {
            source = _createSource(_usage.Add);
            source.Start();
            _source = source;
            Problem = null;
            _log.Info("Traffic meter started");
        }
        catch (Exception ex)
        {
            DisposeQuietly(source);
            _retryAt = now + RetryAfter;
            Fail(ex is UnauthorizedAccessException
                ? "The traffic meter needs admin rights to start."
                : $"The traffic meter could not start: {ex.Message}", "Traffic meter could not start", ex);
        }
    }

    private void Fail(string problem, string logMessage, Exception ex)
    {
        Problem = problem;
        LogOnce(problem, () => _log.Error(logMessage, ex));
    }

    // Each distinct error is logged once, so a lasting one doesn't fill the log every minute.
    private void LogOnce(string key, Action log)
    {
        if (_loggedErrors.Count < MaxLoggedErrors && _loggedErrors.Add(key)) log();
    }

    private void StopSource()
    {
        var source = _source;
        _source = null;
        DisposeQuietly(source);
    }

    private void DisposeQuietly(ITrafficSource? source)
    {
        try
        {
            source?.Dispose();
        }
        catch (Exception ex)
        {
            LogOnce("dispose: " + ex.Message, () => _log.Error("Traffic meter could not stop its ETW session", ex));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _enabled = false;
        StopSource();
    }
}
