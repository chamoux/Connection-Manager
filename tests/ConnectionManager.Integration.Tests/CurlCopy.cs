using System.Diagnostics;

namespace ConnectionManager.Integration.Tests;

/// A private copy of Windows' curl, so blocking it never touches the real one.
internal sealed class CurlCopy : IDisposable
{
    // HTTPS on purpose: an antivirus may route plain HTTP through a local proxy, which a block would not stop.
    // (msftconnecttest.com is no good here: its HTTPS certificate is for another host name, so curl exits 60.)
    public const string InternetUrl = "https://www.microsoft.com/";

    private readonly string _dir = Directory.CreateTempSubdirectory("cm-curl-").FullName;

    public CurlCopy()
    {
        ExePath = Path.Combine(_dir, "cm test curl.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "curl.exe"), ExePath);
    }

    public string ExePath { get; }

    /// curl's exit code: 0 = fetched, 7 = could not connect (what a WFP block looks like), 28 = timed out.
    public int Fetch(string url = InternetUrl)
    {
        var start = new ProcessStartInfo(ExePath) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-s", "-o", "NUL", "--max-time", "10", url }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        if (process.WaitForExit(20_000)) return process.ExitCode;
        process.Kill(); // a hung curl would keep holding the copied exe
        return -1;
    }

    /// Retries for a while: the engine may need a moment to drop a dead session's filters.
    public bool FetchesWithin(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (true)
        {
            if (Fetch() == 0) return true;
            if (DateTime.UtcNow > until) return false;
            Thread.Sleep(1000);
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A curl process still exiting can hold the file for a moment; the temp folder is cleaned later.
        }
    }
}
