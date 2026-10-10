using Microsoft.Management.Infrastructure;
using Microsoft.Management.Infrastructure.Options;

namespace ConnectionManager.Core.Qos;

/// The real store: Windows Policy-based QoS through MI, the same calls New-NetQosPolicy, Get-NetQosPolicy and
/// Remove-NetQosPolicy -PolicyStore ActiveStore make, with no PowerShell and no WinRM. Every call needs admin rights,
/// even listing; the service has them as SYSTEM. A new local session per call, so nothing goes stale.
public sealed class CimQosStore : IQosStore
{
    public const string ActiveStore = "ActiveStore";

    private const string Namespace = @"root\StandardCimv2";
    private const string ClassName = "MSFT_NetQosPolicySettingData";
    private const uint ProtocolBoth = 3; // 0 none, 1 TCP, 2 UDP, 3 both
    private const uint AllProfiles = 7;  // 1 domain | 2 public | 4 private
    private const int MaxMessage = 200;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10); // a stuck WMI must not hold the upload worker for long

    private readonly string? _policyStore;

    public CimQosStore() : this(ActiveStore) { }

    /// Null leaves the option out, which means the persistent store: for a test that checks nothing went there.
    internal CimQosStore(string? policyStore) => _policyStore = policyStore;

    public IReadOnlyList<QosPolicy> List() => Run((session, options) =>
    {
        var result = new List<QosPolicy>();
        foreach (var instance in session.EnumerateInstances(Namespace, ClassName, options))
            using (instance)
            {
                var properties = instance.CimInstanceProperties;
                if (properties["Name"]?.Value is not string name || !QosPolicy.IsOurs(name)) continue;
                result.Add(new QosPolicy(name, properties["AppPathNameMatchCondition"]?.Value as string ?? "",
                    properties["ThrottleRateAction"]?.Value is ulong bits ? bits : 0));
            }
        return result;
    });

    public void Create(QosPolicy policy)
    {
        if (!QosPolicy.IsOurs(policy.Name) || policy.AppPath.Length == 0 || policy.BitsPerSecond == 0)
            throw new QosException($"Refused to create the QoS policy {policy.Name}: it needs our name, an app and a rate.");
        Run((session, options) =>
        {
            // The properties and types New-NetQosPolicy sends (MSFT_NetQosPolicySettingData's own types).
            using var instance = new CimInstance(ClassName, Namespace);
            var properties = instance.CimInstanceProperties;
            properties.Add(CimProperty.Create("Name", policy.Name, CimType.String, CimFlags.None));
            properties.Add(CimProperty.Create("AppPathNameMatchCondition", policy.AppPath, CimType.String, CimFlags.None));
            properties.Add(CimProperty.Create("ThrottleRateAction", policy.BitsPerSecond, CimType.UInt64, CimFlags.None));
            properties.Add(CimProperty.Create("IPProtocolMatchCondition", ProtocolBoth, CimType.UInt32, CimFlags.None));
            properties.Add(CimProperty.Create("NetworkProfile", AllProfiles, CimType.UInt32, CimFlags.None));
            using var created = session.CreateInstance(Namespace, instance, options);
            return true;
        });
    }

    public void Delete(string name)
    {
        if (!QosPolicy.IsOurs(name)) throw new QosException($"Refused to delete the QoS policy {name}: it is not ours.");
        Run((session, options) =>
        {
            // Listed in full first: deleting while the enumeration is still open on the same session is not safe.
            var all = session.EnumerateInstances(Namespace, ClassName, options).ToList();
            try
            {
                if (all.FirstOrDefault(i => string.Equals(i.CimInstanceProperties["Name"]?.Value as string, name, StringComparison.OrdinalIgnoreCase)) is { } match)
                    session.DeleteInstance(Namespace, match, options);
            }
            finally
            {
                foreach (var instance in all) instance.Dispose();
            }
            return true;
        });
    }

    private T Run<T>(Func<CimSession, CimOperationOptions, T> work)
    {
        try
        {
            using var session = CimSession.Create(null); // the local machine
            using var options = new CimOperationOptions { Timeout = Timeout };
            // What the cmdlets' -PolicyStore does. mustComply false, like PowerShell; the integration test checks the
            // policy really stays out of the persistent store.
            if (_policyStore is not null) options.SetCustomOption("PolicyStore", _policyStore, mustComply: false);
            return work(session, options);
        }
        catch (CimException ex)
        {
            throw new QosException(Describe(ex), ex);
        }
        catch (Exception ex) // COM errors, a missing native part of MI, anything else: callers see only QosException
        {
            throw new QosException($"Windows QoS failed: {Short(ex.Message)}", ex);
        }
    }

    private static string Describe(CimException ex) => ex.NativeErrorCode switch
    {
        NativeErrorCode.AccessDenied => "Windows refused access to its QoS policies; this needs admin rights.",
        NativeErrorCode.InvalidNamespace or NativeErrorCode.InvalidClass => "Windows QoS policies are not available on this PC.",
        NativeErrorCode.AlreadyExists => "A QoS policy with that name already exists.",
        _ => $"Windows QoS failed: {Short(ex.Message)}",
    };

    private static string Short(string message)
    {
        var line = message.ReplaceLineEndings(" ").Trim();
        return line.Length <= MaxMessage ? line : line[..MaxMessage] + "…";
    }
}
