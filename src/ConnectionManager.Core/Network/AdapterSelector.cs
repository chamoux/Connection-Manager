using System.Net.NetworkInformation;

namespace ConnectionManager.Core.Network;

/// What the watcher needs to know about one network adapter. Gateways are its default gateways, as IP strings.
public sealed record AdapterFacts(NetworkInterfaceType Type, bool Up, Guid Id, IReadOnlyList<string> Gateways, int IfIndex = 0);

/// Picks the adapter the PC is using: an up Ethernet adapter with a gateway, else an up Wi-Fi adapter with a gateway.
public static class AdapterSelector
{
    public static (NetworkKind Kind, AdapterFacts? Adapter) Select(IEnumerable<AdapterFacts> adapters)
    {
        AdapterFacts? wifi = null;
        foreach (var adapter in adapters)
        {
            if (!adapter.Up || adapter.Gateways.Count == 0) continue; // drops host-only virtual switches too
            var kind = KindOf(adapter.Type);
            if (kind == NetworkKind.Ethernet) return (kind, adapter);
            if (kind == NetworkKind.Wifi) wifi ??= adapter;
        }
        return wifi is null ? (NetworkKind.None, null) : (NetworkKind.Wifi, wifi);
    }

    /// Hyper-V vEthernet adapters and USB-tethered phones report as Ethernet and count as Ethernet. Loopback, tunnels,
    /// PPP and mobile broadband count as neither.
    public static NetworkKind KindOf(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
            or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit => NetworkKind.Ethernet,
        NetworkInterfaceType.Wireless80211 => NetworkKind.Wifi,
        _ => NetworkKind.None,
    };
}
