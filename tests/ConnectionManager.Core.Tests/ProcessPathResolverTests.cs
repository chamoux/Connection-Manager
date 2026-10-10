using ConnectionManager.Core.Ipc;
using ConnectionManager.Core.Traffic;
using Microsoft.Extensions.Time.Testing;

namespace ConnectionManager.Core.Tests;

public class ProcessPathResolverTests
{
    private const string Curl = @"C:\Windows\System32\curl.exe";

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 9, 20, 0, 0, TimeSpan.Zero));
    private readonly Dictionary<int, string> _running = new();
    private readonly List<int> _lookups = [];

    private ProcessPathResolver Create(Func<string, string>? longPath = null) => new(pid =>
    {
        _lookups.Add(pid);
        return _running.GetValueOrDefault(pid);
    }, longPath ?? (p => p), _time);

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void The_kernel_is_system(int pid) => Assert.Equal("System", Create().Resolve(pid));

    [Fact]
    public void The_first_exe_image_is_the_program_and_is_expanded_once()
    {
        var expanded = 0;
        var resolver = Create(p => { expanded++; return p.Replace("SYSTEM~1", "System32"); });
        resolver.OnStarted(10, "curl.exe");
        resolver.OnImage(10, @"C:\Windows\SYSTEM~1\curl.exe");
        resolver.OnImage(10, @"C:\Other\helper.exe"); // a later .exe load is not the program

        Assert.Equal(Curl, resolver.Resolve(10));
        Assert.Equal(Curl, resolver.Resolve(10));
        Assert.Equal(1, expanded);
        Assert.Empty(_lookups);
    }

    [Fact]
    public void A_short_lived_process_keeps_its_image_path_after_exit()
    {
        var resolver = Create();
        resolver.OnStarted(10, "curl.exe");
        resolver.OnImage(10, Curl);
        resolver.OnStopped(10);

        Assert.Equal(Curl, resolver.Resolve(10)); // packets still in the buffer after the stop event
    }

    [Fact]
    public void Without_an_image_event_windows_is_asked_once()
    {
        _running[20] = @"C:\Apps\long-lived.exe";
        var resolver = Create();

        Assert.Equal(@"C:\Apps\long-lived.exe", resolver.Resolve(20));
        Assert.Equal(@"C:\Apps\long-lived.exe", resolver.Resolve(20));
        Assert.Equal([20], _lookups);
    }

    [Fact]
    public void An_unknown_process_falls_back_to_its_image_name_then_unknown()
    {
        var resolver = Create();
        resolver.OnStarted(30, "gone.exe");

        Assert.Equal("gone.exe", resolver.Resolve(30));
        Assert.Equal("Unknown", resolver.Resolve(31));
        Assert.Equal("Unknown", resolver.Resolve(31));
        Assert.Equal([30, 31], _lookups);
    }

    [Fact]
    public void A_reused_process_id_starts_fresh()
    {
        var resolver = Create();
        resolver.OnStarted(40, "first.exe");
        resolver.OnImage(40, @"C:\Apps\first.exe");
        resolver.OnStopped(40);

        resolver.OnStarted(40, "second.exe");
        resolver.OnImage(40, @"C:\Apps\second.exe");

        Assert.Equal(@"C:\Apps\second.exe", resolver.Resolve(40));
    }

    [Fact]
    public void Exited_processes_are_kept_two_minutes()
    {
        var resolver = Create();
        resolver.OnImage(50, @"C:\Apps\a.exe");
        resolver.OnStopped(50);

        resolver.Prune(_time.GetUtcNow() + ProcessPathResolver.KeepAfterExit);
        Assert.Equal(1, resolver.Count);

        _time.Advance(ProcessPathResolver.KeepAfterExit + TimeSpan.FromSeconds(30));
        resolver.OnStopped(51); // stop events prune every 30 s
        Assert.Equal(0, resolver.Count);
    }

    [Fact]
    public void Paths_longer_than_the_ipc_limit_are_not_kept()
    {
        var tooLong = @"C:\" + new string('a', IpcLimits.MaxPath) + ".exe";
        var resolver = Create(p => p + p); // expanding can make a path longer too
        resolver.OnStarted(60, new string('n', IpcLimits.MaxPath + 1));
        resolver.OnImage(60, tooLong);
        resolver.OnImage(61, @"C:\" + new string('b', IpcLimits.MaxPath / 2 + 1) + ".exe");
        _running[62] = tooLong;
        resolver.OnStarted(63, new string('n', IpcLimits.MaxPath + 1));

        Assert.Equal("Unknown", resolver.Resolve(60));
        Assert.Equal("Unknown", resolver.Resolve(61));
        Assert.Equal("Unknown", resolver.Resolve(62));
        Assert.Equal("Unknown", resolver.Resolve(63));
    }

    [Fact]
    public void The_map_is_capped_when_stop_events_are_lost()
    {
        var resolver = Create();
        for (var pid = 8; pid < 8 + ProcessPathResolver.MaxEntries; pid++) resolver.OnStarted(pid, "x.exe");
        Assert.Equal(ProcessPathResolver.MaxEntries, resolver.Count);

        resolver.OnStarted(1_000_000, "new.exe");

        Assert.Equal(1, resolver.Count);
        Assert.Equal("new.exe", resolver.Resolve(1_000_000));
    }
}
