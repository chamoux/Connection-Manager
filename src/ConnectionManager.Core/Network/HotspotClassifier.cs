namespace ConnectionManager.Core.Network;

/// Decides whether the current network is a phone hotspot, for Data Saver's Auto mode.
public static class HotspotClassifier
{
    /// Every iPhone hotspot hands out this gateway.
    public const string IPhoneGateway = "172.20.10.1";

    public static (bool IsHotspot, string Reason) Classify(NetworkSnapshot snapshot, IReadOnlySet<string> hotspotNames, bool autoOnMeteredWifi)
    {
        if (snapshot.Kind == NetworkKind.Ethernet) return (false, "Ethernet"); // a USB-tethered phone too, by design
        if (snapshot.Kind != NetworkKind.Wifi) return (false, "No network");

        // Ordinal whatever comparer the caller's set uses: network names are case-sensitive.
        if (snapshot.Name is { } name && hotspotNames.Any(n => string.Equals(n, name, StringComparison.Ordinal))) return (true, "Your hotspot");
        if (snapshot.Gateways.Contains(IPhoneGateway)) return (true, "iPhone hotspot");
        if (autoOnMeteredWifi && snapshot.Metered) return (true, "Metered Wi-Fi");
        return (false, "Not on a hotspot");
    }
}
