namespace ConnectionManager.Core.Qos;

/// One Windows QoS policy that throttles what one program sends. BitsPerSecond is QoS's own unit (ThrottleRateAction).
public sealed record QosPolicy(string Name, string AppPath, ulong BitsPerSecond)
{
    /// Every policy Connection Manager makes is named with this, and it never touches any other. Lowercase, because
    /// Windows stores QoS policy names in lowercase whatever case they were created with.
    public const string Prefix = "connectionmanager-";

    /// Ignoring case, like Windows' own policy names.
    public static bool IsOurs(string? name) =>
        name is not null && name.Length > Prefix.Length && name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
}

/// Every failure of an IQosStore, with a short message that can go in the status as it is.
public sealed class QosException(string message, Exception? inner = null) : Exception(message, inner);

/// Windows QoS policies in the ActiveStore. Every method throws only QosException.
public interface IQosStore
{
    /// Only our policies (QosPolicy.IsOurs).
    IReadOnlyList<QosPolicy> List();

    /// Fails when a policy with that name already exists.
    void Create(QosPolicy policy);

    /// Does nothing when no policy has that name.
    void Delete(string name);
}
