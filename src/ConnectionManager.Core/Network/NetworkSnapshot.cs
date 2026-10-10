using ConnectionManager.Core.Ipc;

namespace ConnectionManager.Core.Network;

public enum NetworkKind { None, Ethernet, Wifi }

public enum NetworkCost { Unknown, Unrestricted, Fixed, Variable }

/// The network the PC uses right now. Name is the NLM network name (usually the SSID), Gateways are IP strings.
public sealed record NetworkSnapshot(
    NetworkKind Kind, Guid? AdapterId, string? Name, IReadOnlyList<string> Gateways, NetworkCost Cost, int IfIndex = 0)
{
    public static readonly NetworkSnapshot None = new(NetworkKind.None, null, null, [], NetworkCost.Unknown);

    public bool Metered => Cost is NetworkCost.Fixed or NetworkCost.Variable;

    public NetworkDto ToDto(IReadOnlySet<string> hotspotNames, bool autoOnMeteredWifi, bool canMarkMetered, bool markedByUs)
    {
        var (isHotspot, reason) = HotspotClassifier.Classify(this, hotspotNames, autoOnMeteredWifi);
        var kind = Kind switch { NetworkKind.Ethernet => "ethernet", NetworkKind.Wifi => "wifi", _ => "none" };
        return new NetworkDto(kind, Name, Metered, isHotspot, isHotspot ? reason : null, canMarkMetered, markedByUs);
    }

    // Records compare lists by reference; compare gateways by content, so an unchanged network reads as unchanged.
    public bool Equals(NetworkSnapshot? other) =>
        other is not null && Kind == other.Kind && AdapterId == other.AdapterId && Name == other.Name && Cost == other.Cost
        && IfIndex == other.IfIndex && Gateways.SequenceEqual(other.Gateways);

    public override int GetHashCode() => HashCode.Combine(Kind, AdapterId, Name, Cost, IfIndex, Gateways.Count);
}
