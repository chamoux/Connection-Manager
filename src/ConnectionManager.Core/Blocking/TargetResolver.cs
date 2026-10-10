using ConnectionManager.Core.Paths;
using ConnectionManager.Core.Settings;

namespace ConnectionManager.Core.Blocking;

/// Turns enabled catalog entries and the user's own blocks into concrete targets, skipping anything protected.
/// Every path, including each program found on disk, passes SafePaths before it becomes a target: WFP opens it as SYSTEM.
/// Folder scans run on the engine loop, so each looks at no more than entryLimit files and folders, and all of one
/// Resolve together at no more than totalEntryLimit.
public sealed class TargetResolver
{
    public const int FolderLimit = 400;

    /// On the test PC 20 000 entries took 0.05 to 0.5 s warm and up to 3.2 s cold, so this many stay well inside the
    /// engine's 90 s watchdog.
    public const int TotalEntryLimit = 100_000;

    private static readonly Func<string, string> AsIs = text => text;

    private readonly Func<string, bool> _isProtected;
    private readonly Func<string, string> _expandEnv;
    private readonly int _folderLimit;
    private readonly int _entryLimit;
    private readonly int _totalEntryLimit;
    private readonly List<string> _capped = [];
    private readonly List<string> _rejected = [];
    private int _entriesLeft;

    public TargetResolver(Func<string, bool> isProtected, Func<string, string>? expandEnv = null, int folderLimit = FolderLimit,
        int entryLimit = PathPatterns.MaxEntries, int totalEntryLimit = TotalEntryLimit)
    {
        _isProtected = isProtected;
        _expandEnv = expandEnv ?? Environment.ExpandEnvironmentVariables;
        _folderLimit = folderLimit;
        _entryLimit = entryLimit;
        _totalEntryLimit = totalEntryLimit;
    }

    /// Folders from the last Resolve whose scan stopped early, at the limit of programs or of entries looked at; the
    /// programs it didn't reach are not blocked.
    public IReadOnlyList<string> CappedFolders => _capped;

    /// Paths from the last Resolve that SafePaths refused, made printable for the log.
    public IReadOnlyList<string> Rejected => _rejected;

    public IReadOnlySet<BlockTarget> Resolve(IEnumerable<CatalogEntry> entries, IEnumerable<UserBlock> userBlocks)
    {
        _capped.Clear();
        _rejected.Clear();
        _entriesLeft = _totalEntryLimit;
        var targets = new HashSet<BlockTarget>();
        foreach (var entry in entries)
        {
            foreach (var pattern in entry.Paths)
                foreach (var path in PathPatterns.Expand(pattern, _expandEnv)) AddApp(targets, path, entry.Name);
            foreach (var folder in entry.Folders)
            {
                var expanded = _expandEnv(folder);
                if (!expanded.Contains('%')) AddFolder(targets, expanded, folder, entry.Name); // '%' left: a variable not set on this PC
            }
            foreach (var service in entry.Services) targets.Add(new BlockTarget(TargetKind.Service, service, entry.Name));
        }
        foreach (var block in userBlocks)
        {
            if (!Safe(block.Path, out var path)) continue; // never resolved against the working folder, never expanded
            if (block.IsFolder) AddFolder(targets, path, path, "your list");
            else if (File.Exists(path)) AddApp(targets, path, "your list");
        }
        return targets;
    }

    private void AddFolder(HashSet<BlockTarget> targets, string folder, string shown, string label)
    {
        if (!Safe(folder, out var full)) return;
        var exes = PathPatterns.ExesUnder(full, _folderLimit, Math.Min(_entryLimit, _entriesLeft), out var visited, out var capped, AsIs);
        _entriesLeft -= visited;
        if (capped) _capped.Add(shown);
        foreach (var exe in exes) AddApp(targets, exe, label);
    }

    private void AddApp(HashSet<BlockTarget> targets, string path, string label)
    {
        if (Safe(path, out var full) && !_isProtected(full)) targets.Add(new BlockTarget(TargetKind.App, full, label));
    }

    private bool Safe(string? path, out string full)
    {
        if (SafePaths.TryLocal(path, out full)) return true;
        _rejected.Add(SafePaths.Printable(path));
        return false;
    }
}
