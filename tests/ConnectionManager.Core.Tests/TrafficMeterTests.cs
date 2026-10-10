using ConnectionManager.Core.Traffic;
using Microsoft.Extensions.Time.Testing;

namespace ConnectionManager.Core.Tests;

public class TrafficMeterTests
{
    private const string Chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 9, 20, 0, 0, TimeSpan.Zero));
    private readonly ListLog _log = new();
    private readonly List<FakeTrafficSource> _sources = [];
    private Exception? _failStart;
    private bool _failFactory;

    private TrafficMeter Create() => new(_log, _time, sink =>
    {
        if (_failFactory) throw new InvalidOperationException("factory broke");
        var source = new FakeTrafficSource(sink) { FailStart = _failStart };
        _sources.Add(source);
        return source;
    });

    private int Errors => _log.Lines.Count(l => l.StartsWith("ERROR", StringComparison.Ordinal) || l.StartsWith("WARN", StringComparison.Ordinal));

    [Fact]
    public void Starts_off_and_enabling_starts_a_source()
    {
        using var meter = Create();
        Assert.False(meter.Enabled);
        Assert.Empty(_sources);

        meter.Enabled = true;
        meter.Enabled = true; // no second source

        var source = Assert.Single(_sources);
        Assert.True(source.Started);
        Assert.Null(meter.Problem);
    }

    [Fact]
    public void Disabling_stops_the_source_and_keeps_the_numbers()
    {
        using var meter = Create();
        meter.Enabled = true;
        _sources[0].Sink(Chrome, 2000, 100);

        meter.Enabled = false;

        Assert.True(_sources[0].Disposed);
        Assert.Equal(2100, meter.Snapshot()[Chrome]);
        Assert.Equal(2000, Assert.Single(meter.Top(10)).BytesIn);
    }

    [Fact]
    public void Tick_does_nothing_while_disabled()
    {
        using var meter = Create();
        _time.Advance(TimeSpan.FromMinutes(5));
        meter.Tick(_time.GetUtcNow());
        Assert.Empty(_sources);
    }

    [Fact]
    public void A_faulted_source_is_restarted_after_60_seconds_and_logged_once()
    {
        using var meter = Create();
        meter.Enabled = true;
        _sources[0].Fault = new InvalidOperationException("session died");

        meter.Tick(_time.GetUtcNow());
        Assert.True(_sources[0].Disposed);
        Assert.Contains("session died", meter.Problem);

        _time.Advance(TimeSpan.FromSeconds(59));
        meter.Tick(_time.GetUtcNow());
        Assert.Single(_sources);

        _time.Advance(TimeSpan.FromSeconds(1));
        meter.Tick(_time.GetUtcNow());
        Assert.Equal(2, _sources.Count);
        Assert.True(_sources[1].Started);
        Assert.Null(meter.Problem);

        _sources[1].Fault = new InvalidOperationException("session died"); // the same problem again
        meter.Tick(_time.GetUtcNow());
        _time.Advance(TimeSpan.FromSeconds(60));
        meter.Tick(_time.GetUtcNow());

        Assert.Equal(3, _sources.Count);
        Assert.Equal(1, Errors);
    }

    [Fact]
    public void Each_distinct_problem_is_logged()
    {
        using var meter = Create();
        meter.Enabled = true;
        _sources[0].Fault = new InvalidOperationException("first");
        meter.Tick(_time.GetUtcNow());
        _time.Advance(TimeSpan.FromSeconds(60));
        meter.Tick(_time.GetUtcNow());
        _sources[1].Fault = new InvalidOperationException("second");
        meter.Tick(_time.GetUtcNow());

        Assert.Equal(2, Errors);
    }

    [Fact]
    public void Without_admin_rights_start_fails_into_a_problem_and_retries_a_minute_later()
    {
        _failStart = new UnauthorizedAccessException("Access is denied.");
        using var meter = Create();

        meter.Enabled = true; // never throws

        Assert.True(meter.Enabled);
        Assert.Contains("admin", meter.Problem);
        Assert.True(_sources[0].Disposed);

        meter.Tick(_time.GetUtcNow());
        Assert.Single(_sources);

        _time.Advance(TimeSpan.FromSeconds(60));
        meter.Tick(_time.GetUtcNow());
        Assert.Equal(2, _sources.Count);
        Assert.Equal(1, Errors); // the same failure is not logged every minute

        _failStart = null;
        _time.Advance(TimeSpan.FromSeconds(60));
        meter.Tick(_time.GetUtcNow());
        Assert.True(_sources[2].Started);
        Assert.Null(meter.Problem);
    }

    [Fact]
    public void Turning_it_off_clears_the_problem_and_on_again_retries_at_once()
    {
        _failStart = new InvalidOperationException("no resources");
        using var meter = Create();
        meter.Enabled = true;
        Assert.NotNull(meter.Problem);

        meter.Enabled = false;
        Assert.Null(meter.Problem);

        _failStart = null;
        meter.Enabled = true;
        Assert.Equal(2, _sources.Count);
        Assert.Null(meter.Problem);
    }

    [Fact]
    public void A_throwing_factory_or_dispose_never_reaches_the_caller()
    {
        _failFactory = true;
        using (var meter = Create())
        {
            meter.Enabled = true;
            Assert.Contains("factory broke", meter.Problem);
        }

        _failFactory = false;
        using (var meter = Create())
        {
            meter.Enabled = true;
            _sources[0].FailDispose = true;
            _sources[0].Fault = new InvalidOperationException("died");
            meter.Tick(_time.GetUtcNow());
            meter.Enabled = false;
        }
    }

    [Fact]
    public void A_bad_event_is_logged_once()
    {
        using var meter = Create();
        meter.Enabled = true;
        _sources[0].FirstHandlerError = new FormatException("bad payload");

        meter.Tick(_time.GetUtcNow());
        meter.Tick(_time.GetUtcNow());

        Assert.Equal(1, Errors);
        Assert.Null(meter.Problem); // skipped events don't stop the meter
    }

    [Fact]
    public void Dispose_stops_the_source_and_later_calls_are_ignored()
    {
        var meter = Create();
        meter.Enabled = true;

        meter.Dispose();
        meter.Enabled = true;
        meter.Tick(_time.GetUtcNow() + TimeSpan.FromMinutes(5));

        Assert.True(Assert.Single(_sources).Disposed);
    }

    private sealed class FakeTrafficSource(Action<string, long, long> sink) : ITrafficSource
    {
        public Action<string, long, long> Sink { get; } = sink;
        public Exception? FailStart { get; init; }
        public bool FailDispose { get; set; }
        public bool Started { get; private set; }
        public bool Disposed { get; private set; }
        public Exception? Fault { get; set; }
        public Exception? FirstHandlerError { get; set; }

        public void Start()
        {
            if (FailStart is not null) throw FailStart;
            Started = true;
        }

        public void Dispose()
        {
            Disposed = true;
            if (FailDispose) throw new InvalidOperationException("dispose broke");
        }
    }
}
