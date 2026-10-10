using System.Net.NetworkInformation;
using ConnectionManager.Core.Network;

namespace ConnectionManager.Core.Tests;

public class AdapterSelectorTests
{
    private static AdapterFacts A(NetworkInterfaceType type, bool up = true, params string[] gateways) => new(type, up, Guid.NewGuid(), gateways);

    private static readonly AdapterFacts Wifi = A(NetworkInterfaceType.Wireless80211, true, "172.20.10.1");
    private static readonly AdapterFacts Cable = A(NetworkInterfaceType.Ethernet, true, "192.168.1.1");

    [Fact]
    public void Ethernet_beats_wifi()
    {
        var (kind, adapter) = AdapterSelector.Select([Wifi, Cable]);

        Assert.Equal(NetworkKind.Ethernet, kind);
        Assert.Same(Cable, adapter);
    }

    [Fact]
    public void Wifi_when_no_ethernet_is_usable()
    {
        var hostOnlySwitch = A(NetworkInterfaceType.Ethernet); // Hyper-V Default Switch on a host: up, no gateway

        var (kind, adapter) = AdapterSelector.Select([hostOnlySwitch, Wifi]);

        Assert.Equal(NetworkKind.Wifi, kind);
        Assert.Same(Wifi, adapter);
    }

    [Fact]
    public void A_down_adapter_is_ignored()
    {
        var unplugged = A(NetworkInterfaceType.Ethernet, false, "192.168.1.1");

        Assert.Same(Wifi, AdapterSelector.Select([unplugged, Wifi]).Adapter);
        Assert.Equal(NetworkKind.None, AdapterSelector.Select([A(NetworkInterfaceType.Wireless80211, false, "172.20.10.1")]).Kind);
    }

    [Fact]
    public void An_adapter_without_a_gateway_is_ignored()
    {
        var (kind, adapter) = AdapterSelector.Select([A(NetworkInterfaceType.Ethernet), A(NetworkInterfaceType.Wireless80211)]);

        Assert.Equal(NetworkKind.None, kind);
        Assert.Null(adapter);
    }

    [Fact]
    public void None_when_there_are_no_adapters() => Assert.Equal((NetworkKind.None, (AdapterFacts?)null), AdapterSelector.Select([]));

    [Fact]
    public void Loopback_tunnels_and_other_types_are_ignored()
    {
        var others = new[]
        {
            A(NetworkInterfaceType.Loopback),
            A(NetworkInterfaceType.Tunnel, true, "fe80::1"),
            A(NetworkInterfaceType.Ppp, true, "10.0.0.1"),
            A(NetworkInterfaceType.Wwanpp, true, "10.0.0.2"),
            A(NetworkInterfaceType.Unknown, true, "10.0.0.3"),
        };

        Assert.Equal(NetworkKind.None, AdapterSelector.Select(others).Kind);
        Assert.Same(Wifi, AdapterSelector.Select([.. others, Wifi]).Adapter);
    }

    [Theory]
    [InlineData(NetworkInterfaceType.Ethernet)]
    [InlineData(NetworkInterfaceType.GigabitEthernet)]
    [InlineData(NetworkInterfaceType.FastEthernetT)]
    [InlineData(NetworkInterfaceType.FastEthernetFx)]
    [InlineData(NetworkInterfaceType.Ethernet3Megabit)]
    public void Ethernet_types_count_as_ethernet(NetworkInterfaceType type) =>
        Assert.Equal(NetworkKind.Ethernet, AdapterSelector.Select([Wifi, A(type, true, "192.168.1.1")]).Kind);

    [Fact]
    public void The_first_usable_adapter_of_a_kind_wins()
    {
        var second = A(NetworkInterfaceType.Ethernet, true, "10.0.0.1");

        Assert.Same(Cable, AdapterSelector.Select([Cable, second]).Adapter);
    }
}
