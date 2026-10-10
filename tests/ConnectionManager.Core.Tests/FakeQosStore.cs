using ConnectionManager.Core.Qos;

namespace ConnectionManager.Core.Tests;

/// QoS policies in memory. Unlike the real store it lists every policy, other programs' too, so the tests prove
/// UploadCapManager keeps to its own names.
internal sealed class FakeQosStore : IQosStore
{
    public Dictionary<string, QosPolicy> Policies { get; } = new(StringComparer.Ordinal);

    /// "list", "create <name>", "delete <name>", in order.
    public List<string> Calls { get; } = [];

    public Exception? ListFailure { get; set; }

    /// Like Windows, which stores every QoS policy name in lowercase (seen on build 26100).
    public bool LowercasesNames { get; set; }
    public HashSet<string> FailCreateFor { get; } = new(StringComparer.OrdinalIgnoreCase); // app paths
    public HashSet<string> FailDeleteFor { get; } = new(StringComparer.Ordinal);           // names

    /// Adds a policy as some other program (or an earlier run) would.
    public void Seed(string name, string appPath = @"C:\Other\app.exe", ulong bitsPerSecond = 1_000_000) =>
        Policies[name] = new QosPolicy(name, appPath, bitsPerSecond);

    public IReadOnlyList<QosPolicy> List()
    {
        Calls.Add("list");
        if (ListFailure is not null) throw ListFailure;
        return Policies.Values.ToList();
    }

    public void Create(QosPolicy policy)
    {
        Calls.Add($"create {policy.Name}");
        if (FailCreateFor.Contains(policy.AppPath)) throw new QosException("Windows QoS failed: create refused.");
        if (LowercasesNames) policy = policy with { Name = policy.Name.ToLowerInvariant() };
        if (Policies.Keys.Any(k => string.Equals(k, policy.Name, StringComparison.OrdinalIgnoreCase)))
            throw new QosException("A QoS policy with that name already exists."); // Windows names ignore case
        Policies[policy.Name] = policy;
    }

    public void Delete(string name)
    {
        Calls.Add($"delete {name}");
        if (FailDeleteFor.Contains(name)) throw new QosException("Windows QoS failed: delete refused.");
        foreach (var key in Policies.Keys.Where(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)).ToList()) Policies.Remove(key);
    }
}
