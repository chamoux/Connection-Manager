using System.Security.Principal;

namespace ConnectionManager.Core.Wfp;

public sealed record ReconcileResult(int Added, int Removed, IReadOnlyList<(FilterRule Rule, string Message)> Failures);

/// Keeps the engine's filters equal to the wanted set of rules, touching only what changed.
public sealed class FilterReconciler
{
    private readonly IWfpEngine _engine;
    private readonly Dictionary<FilterRule, IReadOnlyList<ulong>> _active = new();

    public FilterReconciler(IWfpEngine engine) => _engine = engine;

    public IReadOnlyCollection<FilterRule> Active => _active.Keys;

    public ReconcileResult Apply(IReadOnlySet<FilterRule> wanted)
    {
        var toRemove = _active.Keys.Where(r => !wanted.Contains(r)).ToList();
        var toAdd = wanted.Where(r => !_active.ContainsKey(r)).ToList();
        if (toRemove.Count == 0 && toAdd.Count == 0) return new ReconcileResult(0, 0, []); // no transaction, no engine lock

        var before = new Dictionary<FilterRule, IReadOnlyList<ulong>>(_active);
        var failures = new List<(FilterRule, string)>();
        var added = 0;
        try
        {
            _engine.InTransaction(() =>
            {
                foreach (var rule in toRemove)
                {
                    foreach (var id in _active[rule]) _engine.DeleteFilter(id);
                    _active.Remove(rule);
                }
                foreach (var rule in toAdd)
                {
                    try
                    {
                        _active[rule] = _engine.Add(rule);
                        added++;
                    }
                    catch (Exception ex) when (ex is WfpException or IdentityNotMappedException)
                    {
                        failures.Add((rule, ex.Message)); // e.g. the file vanished; the rest still apply, and it is retried next time
                    }
                }
            });
        }
        catch
        {
            // The transaction was rolled back, so the engine still holds the old filters.
            _active.Clear();
            foreach (var (rule, ids) in before) _active[rule] = ids;
            throw;
        }
        return new ReconcileResult(added, toRemove.Count, failures);
    }

    public ReconcileResult Clear() => Apply(new HashSet<FilterRule>());
}
