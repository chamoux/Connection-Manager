using ConnectionManager.Core.Logging;

namespace ConnectionManager.Core.Engine;

/// A feature that changes something outside the dynamic WFP session (a policy value, a Wi-Fi profile's cost) and
/// journals it first, so it can always be put back.
public interface IRestorable
{
    /// For logs and the status, such as "Windows download caps".
    string Name { get; }

    /// Puts back everything still journaled. Safe to call again. Null when nothing is left to undo, otherwise a short
    /// plain problem. Must not throw.
    string? Restore();

    /// True for a change the user asked for that does no harm if left in place (a Wi-Fi network marked metered): it is
    /// only undone by "Restore everything" and the uninstaller, not at every start and stop.
    bool UserChoice => false;
}

/// An IRestorable from a name and a function, for wiring an existing module.
public sealed class Restorable(string name, Func<string?> restore, bool userChoice = false) : IRestorable
{
    public string Name { get; } = name;

    public bool UserChoice { get; } = userChoice;

    public string? Restore() => restore();
}

/// Runs the restores at service start (before anything else) and at stop, and every restore, user choices included, on
/// "Restore everything" and for `--restore-only`. Never concurrently: the engine calls it on its own thread, or after its loop has stopped.
public sealed class RestoreHooks
{
    private readonly IReadOnlyList<IRestorable> _hooks;
    private readonly ILog _log;

    public RestoreHooks(IReadOnlyList<IRestorable> hooks, ILog log)
    {
        _hooks = hooks;
        _log = log;
    }

    /// What the last run could not undo, for the status.
    public IReadOnlyList<string> Problems { get; private set; } = [];

    /// Runs every hook (skipping user choices unless asked), even after one fails. True when everything was put back.
    public bool RunAll(string when, bool includeUserChoices)
    {
        var problems = new List<string>();
        foreach (var hook in _hooks)
        {
            if (hook.UserChoice && !includeUserChoices) continue;
            string? problem;
            try
            {
                problem = hook.Restore();
            }
            catch (Exception ex)
            {
                _log.Error($"Restoring {hook.Name} {when} failed", ex);
                problem = $"Could not restore {hook.Name}: {ex.Message}";
            }
            if (problem is null) continue;
            _log.Warn($"Restoring {hook.Name} {when}: {problem}");
            problems.Add(problem);
        }
        if (_hooks.Count > 0 && problems.Count == 0) _log.Info($"Restored everything {when}");
        Problems = problems;
        return problems.Count == 0;
    }
}
