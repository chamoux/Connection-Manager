using System.Globalization;

namespace ConnectionManager.Core.Wfp;

public enum RuleKind { App, Service, RemotePort, LocalPort, All }

public enum RuleAction { Block, Permit }

/// Filter weights inside our sublayer, where the highest matching weight decides: a Game Mode block beats a Data Saver
/// allow, and the essential allows (DNS, DHCP) beat both. The engine's own loopback allow sits above them all, at 15.
public enum RuleWeight : byte { SaverBlockAll = 2, SaverPermit = 8, GameBlock = 10, EssentialPermit = 12 }

/// One thing to block or allow. Value is a full program path (App), a service name (Service), a port such as "53" or
/// "68/udp" (RemotePort, LocalPort; without a protocol it matches any), or "" (All). Label is only for display.
public sealed record FilterRule(RuleKind Kind, string Value, RuleAction Action, RuleWeight Weight, string Label)
{
    public const byte ProtocolTcp = 6;  // IPPROTO_TCP
    public const byte ProtocolUdp = 17; // IPPROTO_UDP

    // Paths and service names compare case-insensitively, so the same exe found twice is one rule.
    public bool Equals(FilterRule? other) =>
        other is not null && Kind == other.Kind && Action == other.Action && Weight == other.Weight
        && string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() => HashCode.Combine(Kind, Action, Weight, StringComparer.OrdinalIgnoreCase.GetHashCode(Value));

    /// Parses "53", "68/udp" or "443/tcp". Protocol 0 means any. Only the plain form passes ("053", "+53" and " 53" don't),
    /// so two values for the same port are also the same rule.
    public static bool TryParsePort(string value, out ushort port, out byte protocol)
    {
        var slash = value.IndexOf('/');
        var number = slash < 0 ? value : value[..slash];
        protocol = slash < 0 ? (byte)0 : value[(slash + 1)..].ToLowerInvariant() switch
        {
            "tcp" => ProtocolTcp,
            "udp" => ProtocolUdp,
            _ => byte.MaxValue,
        };
        if (protocol != byte.MaxValue
            && ushort.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out port)
            && port != 0 && port.ToString(CultureInfo.InvariantCulture) == number)
            return true;
        (port, protocol) = (0, 0);
        return false;
    }
}
