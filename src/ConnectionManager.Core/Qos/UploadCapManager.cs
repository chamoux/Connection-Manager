using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ConnectionManager.Core.Ipc;
using ConnectionManager.Core.Logging;
using ConnectionManager.Core.Paths;

namespace ConnectionManager.Core.Qos;

/// Caps how fast chosen programs upload, with one Windows QoS policy per program. QoS throttles only what a program
/// sends, never what it receives. The policies live in the ActiveStore, which is memory only: a reboot clears them even
/// after a crash, and the service removes them at start and stop. Never throws: problems come back as text for the
/// status, and each distinct one is logged once. One thread only.
public sealed class UploadCapManager
{
    public const int MinKBps = 8;
    public const int MaxKBps = 1_000_000;
    public const int MaxCaps = 50;

    /// 1 KB = 1000 bytes, so 1 KB/s = 8000 bits/s. Decimal like QoS itself, which shows 800 000 as "800 KBits/sec",
    /// and like the UI, which shows the KB/s number as typed. (The traffic meter's byte totals are 1024-based; at these
    /// rates the 2.4% gap does not matter.)
    public const ulong BitsPerKBps = 8_000;

    /// For the status when caps are in place but a capped app still uploads faster than its cap. Connection Manager
    /// never sets this value itself.
    public const string NlaHint = @"Upload caps are set, but Windows may not be applying them. On a PC that isn't on a work domain, Windows QoS can need the value ""Do not use NLA"" = 1 under HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\QoS, then a restart.";

    private readonly IQosStore _store;
    private readonly ILog _log;
    private readonly HashSet<string> _logged = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal); // problems met during the current call

    public UploadCapManager(IQosStore store, ILog log)
    {
        _store = store;
        _log = log;
    }

    /// Makes our policies exactly the wanted caps: deletes the extras, creates the missing ones and recreates the ones
    /// whose rate changed. Disabled removes them all. Returns a problem for the status, or null.
    public string? Apply(IReadOnlyList<UploadCapArgs>? wanted, bool enabled)
    {
        if (!enabled) return RemoveAll();
        return Run("Could not apply the upload caps", problems =>
        {
            var target = Wanted(wanted ?? [], problems);
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var policy in Ours())
            {
                if (target.TryGetValue(policy.Name, out var want) && Same(policy, want)) keep.Add(policy.Name);
                else if (!Delete(policy, problems)) keep.Add(policy.Name); // still there, so creating its replacement would clash
            }
            foreach (var policy in target.Values)
                if (!keep.Contains(policy.Name)) Create(policy, problems);
        });
    }

    /// Deletes every policy of ours, from this run or an earlier one. For service start and stop.
    public string? RemoveAll() => Run("Could not remove the upload caps", problems =>
    {
        foreach (var policy in Ours()) Delete(policy, problems);
    });

    /// The prefix and 16 hex digits of a SHA-256 of the upper-cased path: the same path always gets the same name,
    /// whatever its case or place in the list, so changing one cap never touches the others.
    public static string PolicyName(string fullPath) =>
        QosPolicy.Prefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(fullPath.ToUpperInvariant())), 0, 8);

    private string? Run(string failure, Action<List<string>> work)
    {
        _seen.Clear();
        var problems = new List<string>();
        try
        {
            work(problems);
        }
        catch (QosException ex)
        {
            LogOnce($"store:{ex.Message}", () => _log.Warn($"Upload caps: {ex.Message}"));
            problems.Add($"{failure}: {ex.Message}");
        }
        catch (Exception ex)
        {
            LogOnce($"unexpected:{ex.GetType().Name}:{ex.Message}", () => _log.Error("Upload caps failed", ex));
            problems.Add($"{failure}: {ex.Message}");
        }
        _logged.IntersectWith(_seen); // a problem that went away is logged again if it comes back
        return problems.Count == 0 ? null : string.Join(" ", problems.Distinct());
    }

    // The store lists only ours, but nothing else may ever be deleted, so check again.
    private IEnumerable<QosPolicy> Ours() => _store.List().Where(p => QosPolicy.IsOurs(p.Name));

    private Dictionary<string, QosPolicy> Wanted(IReadOnlyList<UploadCapArgs> wanted, List<string> problems)
    {
        var result = new Dictionary<string, QosPolicy>(StringComparer.OrdinalIgnoreCase);
        int badPaths = 0, badRates = 0, cut = 0;
        foreach (var cap in wanted)
        {
            // QoS expands environment variables in the path, so one with a '%' would match some other file.
            if (cap?.Path is not { } path || !SafePaths.TryLocal(path, out var full) || full.Contains('%'))
            {
                badPaths++;
                LogOnce($"path:{cap?.Path}", () => _log.Warn($"Upload cap skipped: {SafePaths.Printable(cap?.Path)} is not a plain local path"));
                continue;
            }
            if (cap.KBps is < MinKBps or > MaxKBps)
            {
                badRates++;
                LogOnce($"rate:{full}:{cap.KBps}", () => _log.Warn($"Upload cap skipped: {cap.KBps} KB/s for {full} is outside {MinKBps} to {MaxKBps} KB/s"));
                continue;
            }
            var name = PolicyName(full);
            if (result.TryGetValue(name, out var first))
            {
                LogOnce($"twice:{name}:{full}", () => _log.Warn(string.Equals(first.AppPath, full, StringComparison.OrdinalIgnoreCase)
                    ? $"Upload cap for {full} is listed twice; the first one is used"
                    : $"Upload caps for {first.AppPath} and {full} got the same policy name; only the first is used"));
                continue;
            }
            if (result.Count == MaxCaps)
            {
                cut++;
                continue;
            }
            result[name] = new QosPolicy(name, full, (ulong)cap.KBps * BitsPerKBps);
        }

        if (badPaths > 0) problems.Add($"Skipped {Caps(badPaths)} whose app path isn't a plain local path.");
        if (badRates > 0) problems.Add($"Skipped {Caps(badRates)} outside {MinKBps} to {MaxKBps.ToString("N0", CultureInfo.InvariantCulture)} KB/s.");
        if (cut > 0)
        {
            LogOnce($"cut:{cut}", () => _log.Warn($"Upload caps: only the first {MaxCaps} are applied, {cut} more left out"));
            problems.Add($"Only the first {MaxCaps} upload caps are applied; {cut} more were left out.");
        }
        return result;
    }

    private static bool Same(QosPolicy existing, QosPolicy wanted) =>
        existing.BitsPerSecond == wanted.BitsPerSecond && string.Equals(existing.AppPath, wanted.AppPath, StringComparison.OrdinalIgnoreCase);

    private void Create(QosPolicy policy, List<string> problems)
    {
        try
        {
            _store.Create(policy);
            _log.Info($"Upload cap set: {policy.AppPath} at {policy.BitsPerSecond / BitsPerKBps} KB/s");
        }
        catch (QosException ex)
        {
            LogOnce($"create:{policy.Name}:{ex.Message}", () => _log.Warn($"Could not set the upload cap for {policy.AppPath}: {ex.Message}"));
            problems.Add($"Could not set an upload cap: {ex.Message}");
        }
    }

    // False when the policy is still there.
    private bool Delete(QosPolicy policy, List<string> problems)
    {
        try
        {
            _store.Delete(policy.Name);
            _log.Info($"Upload cap removed: {SafePaths.Printable(policy.AppPath)}");
            return true;
        }
        catch (QosException ex)
        {
            LogOnce($"delete:{policy.Name}:{ex.Message}", () => _log.Warn($"Could not remove the upload cap {policy.Name} ({SafePaths.Printable(policy.AppPath)}): {ex.Message}"));
            problems.Add($"Could not remove an upload cap: {ex.Message}");
            return false;
        }
    }

    private void LogOnce(string key, Action log)
    {
        _seen.Add(key);
        if (_logged.Add(key)) log();
    }

    private static string Caps(int count) => count == 1 ? "1 upload cap" : $"{count} upload caps";
}
