namespace ConnectionManager.Core.Reports;

/// Which programs a post-game report leaves out: the user can't or shouldn't block them during games.
public static class ReportExclusions
{
    /// Excludes the game exes, anything under the Steam folder or a Steam library, protected programs and the service.
    /// The meter's pseudo-paths ("System", "Other", "Unknown") and bare file names are excluded too: they are not
    /// full paths, so there is nothing to block. Blank, relative or malformed folders are skipped.
    public static Func<string, bool> Create(IEnumerable<string> gameExes, IEnumerable<string?> steamFolders,
        Func<string, bool> isProtected, string? serviceExe)
    {
        string?[] exes = [.. gameExes, serviceExe];
        var exact = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var exe in exes)
            if (PathRules.TryFull(exe, out var full)) exact.Add(full);
        var folders = steamFolders.Select(PathRules.FolderPrefix).OfType<string>().ToArray();

        return path => !PathRules.TryFull(path, out var full)
            || exact.Contains(full)
            || folders.Any(f => PathRules.IsUnder(full, f))
            || isProtected(full);
    }
}
