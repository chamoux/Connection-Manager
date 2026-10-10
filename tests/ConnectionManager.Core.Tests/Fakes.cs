using System.Security.Principal;
using ConnectionManager.Core.Engine;
using ConnectionManager.Core.Ipc;
using ConnectionManager.Core.Logging;
using ConnectionManager.Core.Processes;
using ConnectionManager.Core.Wfp;

namespace ConnectionManager.Core.Tests;

/// Records filters in memory. Each rule gets four ids, like the real engine (connect + accept, IPv4 + IPv6).
internal sealed class FakeWfpEngine : IWfpEngine
{
    private ulong _next = 1;

    public Dictionary<ulong, FilterRule> Filters { get; } = new();
    public HashSet<string> FailFor { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> MissingServices { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool FailCommit { get; set; }
    public bool FailDelete { get; set; }
    public int Transactions { get; private set; }
    public bool Disposed { get; private set; }

    /// Each distinct rule the engine holds.
    public IEnumerable<FilterRule> Rules => Filters.Values.Distinct();

    public IReadOnlyList<ulong> Add(FilterRule rule)
    {
        if (FailFor.Contains(rule.Value)) throw new WfpException("FwpmGetAppIdFromFileName0", 2); // the file vanished
        if (rule.Kind == RuleKind.Service && MissingServices.Contains(rule.Value)) throw new IdentityNotMappedException("not installed");
        var ids = Enumerable.Range(0, 4).Select(_ => _next++).ToList();
        foreach (var id in ids) Filters[id] = rule;
        return ids;
    }

    public void DeleteFilter(ulong filterId)
    {
        if (FailDelete) throw new InvalidOperationException("delete failed");
        Filters.Remove(filterId);
    }

    public void InTransaction(Action work)
    {
        Transactions++;
        var before = new Dictionary<ulong, FilterRule>(Filters);
        try
        {
            work();
            if (FailCommit) throw new WfpException("FwpmTransactionCommit0", 0x8032000F); // FWP_E_TXN_ABORTED
        }
        catch
        {
            Filters.Clear(); // like WFP: a failed or aborted transaction leaves nothing behind
            foreach (var (id, rule) in before) Filters[id] = rule;
            throw;
        }
    }

    public void Dispose() => Disposed = true;
}

internal sealed class FakeProcessSource : IProcessSource
{
    public List<RunningProcess> Running { get; } = [];
    public bool Fail { get; set; }
    public IReadOnlyList<RunningProcess> GetRunning() => Fail ? throw new InvalidOperationException("snapshot failed") : Running.ToList();
}

internal sealed class ListLog : ILog
{
    public List<string> Lines { get; } = [];
    public void Info(string message) => Lines.Add("INFO " + message);
    public void Warn(string message) => Lines.Add("WARN " + message);
    public void Error(string message, Exception? ex = null) => Lines.Add("ERROR " + message);
}

/// A traffic meter whose totals and top list the test sets.
internal sealed class FakeMeter : ITrafficMeter
{
    public Dictionary<string, long> Totals { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<UsageDto> TopList { get; } = [];
    public bool Enabled { get; set; }
    public string? Problem { get; set; }
    public int Ticks { get; private set; }
    public int? LastTopCount { get; private set; }
    public bool Disposed { get; private set; }

    public void Tick(DateTimeOffset now) => Ticks++;

    public IReadOnlyList<UsageDto> Top(int n)
    {
        LastTopCount = n;
        return TopList.Take(n).ToList();
    }

    public IReadOnlyDictionary<string, long> Snapshot() => new Dictionary<string, long>(Totals, StringComparer.OrdinalIgnoreCase);

    public void Dispose() => Disposed = true;
}
