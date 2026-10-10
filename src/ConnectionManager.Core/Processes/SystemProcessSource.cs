using System.Diagnostics;
using ConnectionManager.Core.Native;

namespace ConnectionManager.Core.Processes;

public sealed record RunningProcess(int Pid, string Path);

/// A running process's program and session, as read by this process.
public sealed record ProcessFacts(string Path, int SessionId);

public interface IProcessSource
{
    /// Running processes whose program path could be read.
    IReadOnlyList<RunningProcess> GetRunning();
}

/// Lists running processes. Paths are cached per PID and process name, so each new process is opened only once,
/// and a PID that Windows hands to a different program between ticks is looked up again.
public sealed class SystemProcessSource : IProcessSource
{
    private readonly Func<IEnumerable<(int Pid, string Name)>> _listProcesses;
    private readonly Func<int, string?> _pathOf;
    private Dictionary<int, (string Name, string? Path)> _cache = new();

    public SystemProcessSource() : this(ListProcesses, NativeProcess.GetPath) { }

    public SystemProcessSource(Func<IEnumerable<(int Pid, string Name)>> listProcesses, Func<int, string?> pathOf)
    {
        _listProcesses = listProcesses;
        _pathOf = pathOf;
    }

    public IReadOnlyList<RunningProcess> GetRunning()
    {
        var next = new Dictionary<int, (string Name, string? Path)>();
        var running = new List<RunningProcess>();
        foreach (var (pid, name) in _listProcesses())
        {
            if (pid is 0 or 4) continue; // Idle and System have no program file
            if (!_cache.TryGetValue(pid, out var entry) || !string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))
                entry = (name, _pathOf(pid));
            next[pid] = entry;
            if (entry.Path is not null) running.Add(new RunningProcess(pid, entry.Path));
        }
        _cache = next; // exited PIDs drop out
        return running;
    }

    /// The program and session of a running process, or null when it has exited or can't be opened.
    public static ProcessFacts? Describe(int pid) =>
        pid is > 0 and not 4 && NativeProcess.GetSessionId(pid) is { } session && NativeProcess.GetPath(pid) is { } path
            ? new ProcessFacts(path, session)
            : null;

    // ProcessName comes from the same snapshot as the PID, so it costs nothing extra.
    private static IEnumerable<(int Pid, string Name)> ListProcesses()
    {
        var processes = Process.GetProcesses();
        try
        {
            return processes.Select(p => (p.Id, p.ProcessName)).ToArray();
        }
        finally
        {
            foreach (var p in processes) p.Dispose();
        }
    }
}
