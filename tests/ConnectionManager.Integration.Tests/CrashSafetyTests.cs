using System.Diagnostics;

namespace ConnectionManager.Integration.Tests;

/// The promise the whole design rests on: if the app dies, Windows drops its blocks.
public sealed class CrashSafetyTests : IDisposable
{
    private readonly CurlCopy _curl = new();

    [ElevatedFact]
    public void Killing_the_owning_process_removes_its_filters()
    {
        var start = new ProcessStartInfo(ProbePath.Find()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--app");
        start.ArgumentList.Add(_curl.ExePath);
        using var probe = Process.Start(start)!;
        try
        {
            var line = probe.StandardOutput.ReadLine();
            // Each admin run costs the user a UAC prompt, so a probe failure must say why.
            Assert.True(line == "READY", $"Probe said '{line}'. Its error output: {(probe.HasExited ? probe.StandardError.ReadToEnd() : "(still running)")}");
            Assert.Equal(7, _curl.Fetch());
        }
        finally
        {
            probe.Kill();
            probe.WaitForExit(10_000);
        }

        Assert.True(_curl.FetchesWithin(TimeSpan.FromSeconds(15)));
    }

    public void Dispose() => _curl.Dispose();
}
