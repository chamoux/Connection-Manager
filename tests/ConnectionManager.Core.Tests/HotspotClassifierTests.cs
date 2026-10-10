using ConnectionManager.Core.Network;

namespace ConnectionManager.Core.Tests;

public class HotspotClassifierTests
{
    private static readonly IReadOnlySet<string> Mine = new HashSet<string>(StringComparer.Ordinal) { "Charbel's Phone" };

    private static NetworkSnapshot Net(NetworkKind kind, string? name, string gateways, NetworkCost cost) =>
        new(kind, kind == NetworkKind.None ? null : Guid.NewGuid(), name, gateways.Split(',', StringSplitOptions.RemoveEmptyEntries), cost);

    [Theory]
    [InlineData(NetworkKind.None, null, "", NetworkCost.Unknown, true, false, "No network")]
    [InlineData(NetworkKind.Ethernet, "Office", "192.168.1.1", NetworkCost.Unrestricted, true, false, "Ethernet")]
    [InlineData(NetworkKind.Ethernet, "Network", "192.168.1.1", NetworkCost.Fixed, true, false, "Ethernet")]                // this VM: metered Ethernet
    [InlineData(NetworkKind.Ethernet, "Charbel's Phone", "172.20.10.1", NetworkCost.Fixed, true, false, "Ethernet")]        // USB-tethered iPhone
    [InlineData(NetworkKind.Wifi, "Charbel's Phone", "192.168.43.1", NetworkCost.Unrestricted, false, true, "Your hotspot")]
    [InlineData(NetworkKind.Wifi, "Charbel's Phone", "172.20.10.1", NetworkCost.Fixed, true, true, "Your hotspot")]         // the list wins
    [InlineData(NetworkKind.Wifi, "charbel's phone", "192.168.43.1", NetworkCost.Unrestricted, true, false, "Not on a hotspot")] // case-sensitive
    [InlineData(NetworkKind.Wifi, "iPhone", "172.20.10.1", NetworkCost.Unrestricted, false, true, "iPhone hotspot")]
    [InlineData(NetworkKind.Wifi, "iPhone", "fe80::1%12,172.20.10.1", NetworkCost.Fixed, true, true, "iPhone hotspot")]
    [InlineData(NetworkKind.Wifi, null, "172.20.10.1", NetworkCost.Unknown, false, true, "iPhone hotspot")]                 // NLM gave no name
    [InlineData(NetworkKind.Wifi, "AndroidAP", "192.168.43.1", NetworkCost.Fixed, true, true, "Metered Wi-Fi")]
    [InlineData(NetworkKind.Wifi, "AndroidAP", "192.168.43.1", NetworkCost.Variable, true, true, "Metered Wi-Fi")]
    [InlineData(NetworkKind.Wifi, "AndroidAP", "192.168.43.1", NetworkCost.Fixed, false, false, "Not on a hotspot")]
    [InlineData(NetworkKind.Wifi, "Home", "192.168.1.1", NetworkCost.Unrestricted, true, false, "Not on a hotspot")]
    [InlineData(NetworkKind.Wifi, "Home", "192.168.1.1", NetworkCost.Unknown, true, false, "Not on a hotspot")]
    [InlineData(NetworkKind.Wifi, null, "192.168.1.1", NetworkCost.Unrestricted, true, false, "Not on a hotspot")]
    [InlineData(NetworkKind.Wifi, "Home", "", NetworkCost.Unrestricted, true, false, "Not on a hotspot")]
    public void Classifies(NetworkKind kind, string? name, string gateways, NetworkCost cost, bool autoOnMeteredWifi,
        bool expectedHotspot, string expectedReason)
    {
        var (isHotspot, reason) = HotspotClassifier.Classify(Net(kind, name, gateways, cost), Mine, autoOnMeteredWifi);

        Assert.Equal(expectedHotspot, isHotspot);
        Assert.Equal(expectedReason, reason);
    }

    [Fact]
    public void Names_match_case_sensitively_whatever_the_set_comparer()
    {
        var ignoreCase = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Home" };

        Assert.False(HotspotClassifier.Classify(Net(NetworkKind.Wifi, "HOME", "192.168.1.1", NetworkCost.Unrestricted), ignoreCase, false).IsHotspot);
        Assert.True(HotspotClassifier.Classify(Net(NetworkKind.Wifi, "Home", "192.168.1.1", NetworkCost.Unrestricted), ignoreCase, false).IsHotspot);
    }

    [Fact]
    public void Dto_for_a_metered_wifi_hotspot()
    {
        var dto = Net(NetworkKind.Wifi, "AndroidAP", "192.168.43.1", NetworkCost.Variable).ToDto(Mine, true, true, false);

        Assert.Equal(new Ipc.NetworkDto("wifi", "AndroidAP", true, true, "Metered Wi-Fi", true, false), dto);
    }

    [Fact]
    public void Dto_leaves_the_reason_empty_when_not_a_hotspot()
    {
        var dto = Net(NetworkKind.Wifi, "Home", "192.168.1.1", NetworkCost.Unrestricted).ToDto(Mine, true, true, true);

        Assert.Equal(new Ipc.NetworkDto("wifi", "Home", false, false, null, true, true), dto);
    }

    [Fact]
    public void Dto_for_metered_ethernet_is_metered_but_not_a_hotspot()
    {
        var dto = Net(NetworkKind.Ethernet, "Network", "192.168.1.1", NetworkCost.Fixed).ToDto(Mine, true, false, false);

        Assert.Equal(new Ipc.NetworkDto("ethernet", "Network", true, false, null, false, false), dto);
    }

    [Fact]
    public void Dto_for_no_network() =>
        Assert.Equal(new Ipc.NetworkDto("none", null, false, false, null, false, false), NetworkSnapshot.None.ToDto(Mine, true, false, false));
}
