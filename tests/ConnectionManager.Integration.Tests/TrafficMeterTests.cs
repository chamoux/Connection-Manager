using ConnectionManager.Core.Ipc;
using ConnectionManager.Core.Logging;
using ConnectionManager.Core.Native;
using ConnectionManager.Core.Traffic;

namespace ConnectionManager.Integration.Tests;

/// The real ETW session counting a private copy of curl. Needs admin rights and a working internet connection.
public sealed class TrafficMeterTests : IDisposable
{
    private readonly CurlCopy _curl = new();

    [ElevatedFact]
    public void Counts_what_a_program_downloads()
    {
        var log = new LinesLog();
        // Its own session name, so the test never takes over the session of an installed service.
        using var meter = new TrafficMeter(log, TimeProvider.System, sink => new EtwTrafficSource(sink, "ConnectionManager-Net-Test"));
        meter.Enabled = true;
        Assert.True(meter.Problem is null, meter.Problem);
        Thread.Sleep(1000); // let the session start delivering before curl runs

        Assert.Equal(0, _curl.Fetch());

        var path = NativeProcess.ToLongPath(_curl.ExePath); // ETW reports long names; %TEMP% may hold 8.3 ones
        UsageDto? usage = null;
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < until)
        {
            usage = meter.Top(50).FirstOrDefault(u => string.Equals(u.Path, path, StringComparison.OrdinalIgnoreCase));
            if (usage?.BytesIn > 10_000) break;
            Thread.Sleep(250);
        }

        Assert.True(usage?.BytesIn > 10_000,
            $"curl: {usage}; top: {string.Join("; ", meter.Top(10).Select(u => $"{u.Path} {u.BytesIn}"))}; log: {string.Join("; ", log.Lines)}");
        Assert.True(meter.Problem is null, meter.Problem);
    }

    public void Dispose() => _curl.Dispose();

    private sealed class LinesLog : ILog
    {
        public List<string> Lines { get; } = [];
        public void Info(string message) => Lines.Add(message);
        public void Warn(string message) => Lines.Add(message);
        public void Error(string message, Exception? ex = null) => Lines.Add($"{message}: {ex?.Message}");
    }
}
