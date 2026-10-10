using ConnectionManager.Core.Steam;

namespace ConnectionManager.Core.Tests;

public sealed class TimedFileReaderTests : IDisposable
{
    private const string Vdf = @"C:\Steam\steamapps\libraryfolders.vdf";
    private const string OtherVdf = @"C:\Steam\config\libraryfolders.vdf";
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(100);

    private readonly ListLog _log = new();
    private readonly ManualResetEventSlim _release = new();
    private int _calls;
    private bool _stuck;

    // A user holding an oplock on the file makes the open wait until they let go: here, until _release is set.
    private TimedFileReader Create() => new(path =>
    {
        var call = Interlocked.Increment(ref _calls);
        if (Volatile.Read(ref _stuck)) _release.Wait();
        return $"{Path.GetFileName(Path.GetDirectoryName(path))} {call}";
    }, Timeout, _log);

    [Fact]
    public void A_quick_read_returns_the_text()
    {
        var reader = Create();

        Assert.Equal("steamapps 1", reader.Read(Vdf));
        Assert.Equal("steamapps 2", reader.Read(Vdf)); // read afresh each time
    }

    [Fact]
    public void A_stuck_read_gives_the_last_text_and_no_other_read_starts_until_it_ends()
    {
        var reader = Create();
        Assert.Equal("steamapps 1", reader.Read(Vdf));
        Volatile.Write(ref _stuck, true);

        Assert.Equal("steamapps 1", reader.Read(Vdf)); // gave up after the timeout: the last good text
        Assert.Null(reader.Read(OtherVdf));             // nothing read from it yet, and no new read while one is stuck
        Assert.Equal("steamapps 1", reader.Read(Vdf));
        Assert.Equal(2, Volatile.Read(ref _calls));
        Assert.Single(_log.Lines, l => l.Contains("did not finish", StringComparison.Ordinal));

        Volatile.Write(ref _stuck, false);
        _release.Set();
        var until = DateTime.UtcNow.AddSeconds(5);
        string? text;
        while ((text = reader.Read(OtherVdf)) is null && DateTime.UtcNow < until) Thread.Sleep(20);
        Assert.Equal("config 3", text); // reads again once the stuck one has ended
    }

    public void Dispose()
    {
        _release.Set();
        _release.Dispose();
    }
}
