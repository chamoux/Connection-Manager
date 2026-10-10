using System.Net.NetworkInformation;
using ConnectionManager.Core.Network;
using Microsoft.Extensions.Time.Testing;

namespace ConnectionManager.Core.Tests;

public class NetworkWatcherTests
{
    private static readonly Guid WifiId = Guid.NewGuid();
    private static readonly Guid CableId = Guid.NewGuid();

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 9, 20, 0, 0, TimeSpan.Zero));
    private readonly ListLog _log = new();
    private List<AdapterFacts> _adapters = [new(NetworkInterfaceType.Wireless80211, true, WifiId, ["172.20.10.1"], 7)];
    private (string? Name, NetworkCost Cost) _network = ("iPhone", NetworkCost.Unrestricted);
    private Exception? _fail;
    private int _reads;

    private NetworkWatcher Watcher() => new(_time, _log, () =>
    {
        _reads++;
        return _fail is null ? _adapters : throw _fail;
    }, _ => _network, subscribe: false);

    [Fact]
    public void Starts_with_no_network() => Assert.Equal(NetworkSnapshot.None, Watcher().Current);

    [Fact]
    public void The_first_refresh_reads_the_network()
    {
        var watcher = Watcher();

        Assert.True(watcher.Refresh(_time.GetUtcNow()));
        Assert.Equal(new NetworkSnapshot(NetworkKind.Wifi, WifiId, "iPhone", ["172.20.10.1"], NetworkCost.Unrestricted, 7), watcher.Current);
    }

    [Fact]
    public void Reads_again_only_after_15_seconds()
    {
        var watcher = Watcher();
        watcher.Refresh(_time.GetUtcNow());

        _time.Advance(TimeSpan.FromSeconds(14));
        watcher.Refresh(_time.GetUtcNow());
        Assert.Equal(1, _reads);

        _time.Advance(TimeSpan.FromSeconds(1));
        watcher.Refresh(_time.GetUtcNow());
        Assert.Equal(2, _reads);
    }

    [Fact]
    public void The_parameterless_refresh_uses_the_time_provider()
    {
        var watcher = Watcher();
        watcher.Refresh();
        watcher.Refresh();
        Assert.Equal(1, _reads);

        _time.Advance(NetworkWatcher.RereadEvery);
        watcher.Refresh();
        Assert.Equal(2, _reads);
    }

    [Fact]
    public void A_change_event_makes_the_next_refresh_read()
    {
        var watcher = Watcher();
        watcher.Refresh(_time.GetUtcNow());

        watcher.MarkDirty();
        watcher.Refresh(_time.GetUtcNow());
        Assert.Equal(2, _reads);

        watcher.Refresh(_time.GetUtcNow()); // the flag was cleared
        Assert.Equal(2, _reads);
    }

    [Fact]
    public void A_clock_moved_back_reads_again()
    {
        var watcher = Watcher();
        watcher.Refresh(_time.GetUtcNow());

        watcher.Refresh(_time.GetUtcNow() - TimeSpan.FromMinutes(5));

        Assert.Equal(2, _reads);
    }

    [Fact]
    public void An_unchanged_network_is_not_reported_as_a_change()
    {
        var watcher = Watcher();
        watcher.Refresh(_time.GetUtcNow());
        _adapters = [new(NetworkInterfaceType.Wireless80211, true, WifiId, ["172.20.10.1"], 7)]; // new lists, same content

        watcher.MarkDirty();

        Assert.False(watcher.Refresh(_time.GetUtcNow()));
        Assert.Equal(2, _reads);
    }

    [Fact]
    public void A_new_cost_name_or_gateway_is_a_change()
    {
        var watcher = Watcher();
        watcher.Refresh(_time.GetUtcNow());

        _network = ("iPhone", NetworkCost.Fixed);
        watcher.MarkDirty();
        Assert.True(watcher.Refresh(_time.GetUtcNow()));
        Assert.Equal(NetworkCost.Fixed, watcher.Current.Cost);

        _network = ("Home", NetworkCost.Fixed);
        watcher.MarkDirty();
        Assert.True(watcher.Refresh(_time.GetUtcNow()));
        Assert.Equal("Home", watcher.Current.Name);

        _adapters = [new(NetworkInterfaceType.Wireless80211, true, WifiId, ["192.168.1.1"], 7)];
        watcher.MarkDirty();
        Assert.True(watcher.Refresh(_time.GetUtcNow()));
        Assert.Equal(["192.168.1.1"], watcher.Current.Gateways);
    }

    [Fact]
    public void Plugging_in_a_cable_switches_to_ethernet()
    {
        var watcher = Watcher();
        watcher.Refresh(_time.GetUtcNow());

        _adapters = [.. _adapters, new(NetworkInterfaceType.Ethernet, true, CableId, ["192.168.1.1"], 3)];
        _network = ("Network", NetworkCost.Fixed);
        watcher.MarkDirty();

        Assert.True(watcher.Refresh(_time.GetUtcNow()));
        Assert.Equal(NetworkKind.Ethernet, watcher.Current.Kind);
        Assert.Equal(CableId, watcher.Current.AdapterId);
    }

    [Fact]
    public void Losing_every_adapter_goes_back_to_no_network()
    {
        var watcher = Watcher();
        watcher.Refresh(_time.GetUtcNow());

        _adapters = [];
        watcher.MarkDirty();

        Assert.True(watcher.Refresh(_time.GetUtcNow()));
        Assert.Equal(NetworkSnapshot.None, watcher.Current);
    }

    [Fact]
    public void A_failed_read_keeps_the_last_snapshot_and_logs_once()
    {
        var watcher = Watcher();
        watcher.Refresh(_time.GetUtcNow());
        var before = watcher.Current;

        _fail = new NetworkInformationException(5);
        for (var i = 0; i < 3; i++)
        {
            _time.Advance(NetworkWatcher.RereadEvery);
            Assert.False(watcher.Refresh(_time.GetUtcNow()));
        }

        Assert.Same(before, watcher.Current);
        Assert.Equal(4, _reads);
        Assert.Single(_log.Lines, l => l.StartsWith("ERROR", StringComparison.Ordinal));
    }

    [Fact]
    public void A_failure_after_a_recovery_is_logged_again()
    {
        var watcher = Watcher();
        _fail = new NetworkInformationException(5);
        watcher.Refresh(_time.GetUtcNow());

        _fail = null;
        watcher.MarkDirty();
        Assert.True(watcher.Refresh(_time.GetUtcNow()));

        _fail = new NetworkInformationException(5);
        watcher.MarkDirty();
        watcher.Refresh(_time.GetUtcNow());

        Assert.Equal(2, _log.Lines.Count(l => l.StartsWith("ERROR", StringComparison.Ordinal)));
    }

    [Fact]
    public void Snapshots_compare_gateways_by_content()
    {
        var a = new NetworkSnapshot(NetworkKind.Wifi, WifiId, "iPhone", new List<string> { "172.20.10.1" }, NetworkCost.Fixed);
        var b = new NetworkSnapshot(NetworkKind.Wifi, WifiId, "iPhone", new[] { "172.20.10.1" }, NetworkCost.Fixed);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, b with { Gateways = ["172.20.10.1", "fe80::1"] });
        Assert.NotEqual(a, b with { Name = "IPHONE" });
    }

    [Fact]
    public void Network_names_are_capped_and_cleaned()
    {
        Assert.Null(NlmInterop.CleanName(null));
        Assert.Null(NlmInterop.CleanName("  "));
        Assert.Equal("Home", NlmInterop.CleanName("Home"));
        Assert.Equal("a?b?", NlmInterop.CleanName("a\nb\u0007"));
        Assert.Equal(Ipc.IpcLimits.MaxName, NlmInterop.CleanName(new string('x', 1000))!.Length);
        Assert.Equal(new string('x', Ipc.IpcLimits.MaxName - 1), NlmInterop.CleanName(new string('x', Ipc.IpcLimits.MaxName - 1) + "\U0001F4F6")); // no half emoji
    }

    [Theory]
    [InlineData(0u, NetworkCost.Unknown)]
    [InlineData(1u, NetworkCost.Unrestricted)]
    [InlineData(2u, NetworkCost.Fixed)]
    [InlineData(4u, NetworkCost.Variable)]
    [InlineData(0x40004u, NetworkCost.Variable)] // roaming flag in the high bits
    [InlineData(0x10000u, NetworkCost.Unknown)]
    public void Nlm_costs_map_from_the_low_16_bits(uint raw, NetworkCost expected) => Assert.Equal(expected, NlmInterop.FromNlmCost(raw));

    [Theory]
    [InlineData(0, NetworkCost.Unknown)]
    [InlineData(1, NetworkCost.Unrestricted)]
    [InlineData(2, NetworkCost.Fixed)]
    [InlineData(3, NetworkCost.Variable)]
    [InlineData(9, NetworkCost.Unknown)]
    public void Hint_costs_map(int raw, NetworkCost expected) => Assert.Equal(expected, NlmInterop.FromHintCost(raw));

    [Fact]
    public void Nlm_does_not_throw_for_an_unknown_adapter()
    {
        Assert.False(NlmInterop.TryRead(Guid.NewGuid(), out var name, out var cost));
        Assert.Null(name);
        Assert.Equal(NetworkCost.Unknown, cost);
    }

    // Smoke test on this machine, on an MTA thread like the service's workers.
    [Fact]
    public void The_real_watcher_reads_this_pc_without_throwing()
    {
        NetworkSnapshot? read = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var watcher = new NetworkWatcher(_log);
                watcher.Refresh(DateTimeOffset.UtcNow);
                read = watcher.Current;
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join();

        Assert.Null(error);
        Assert.NotNull(read);
        Assert.True(Enum.IsDefined(read.Kind));
        Assert.DoesNotContain(_log.Lines, l => l.StartsWith("ERROR", StringComparison.Ordinal));
    }
}
