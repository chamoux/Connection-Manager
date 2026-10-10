using System.IO.Enumeration;
using ConnectionManager.Core.Paths;

namespace ConnectionManager.Core.Blocking;

/// Expands catalog paths: environment variables first, then * and ? wildcards in any folder or file part.
/// Never goes through a junction or other link at any level: a user can plant one in their own profile or in
/// ProgramData, and the service lists these folders as SYSTEM. Every listing is bounded by the entries it looks at,
/// matching or not, since a user can fill a folder with empty ones and the listing runs on the engine loop.
public static class PathPatterns
{
    /// The most files and folders one expansion or one folder scan looks at.
    public const int MaxEntries = 20_000;

    private static readonly EnumerationOptions OneLevel = new()
    {
        MatchType = MatchType.Simple,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint, // hidden and system items count too, like ExesUnder
    };

    private static readonly EnumerationOptions Recursive = new()
    {
        MatchType = MatchType.Simple,
        IgnoreInaccessible = true,
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    /// Existing files matching the pattern. A variable that isn't set on this PC matches nothing. Past `maxEntries`
    /// looked at, the rest of the wildcards match nothing.
    public static IEnumerable<string> Expand(string pattern, Func<string, string>? expandEnv = null, int maxEntries = MaxEntries)
    {
        var expanded = (expandEnv ?? Environment.ExpandEnvironmentVariables)(pattern);
        if (expanded.Contains('%')) return [];
        var parts = expanded.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !SafePaths.TryLocal(parts[0] + '\\', out var root)) return [];

        var budget = maxEntries;
        var current = new List<string> { root }; // "C:\"
        for (var i = 1; i < parts.Length; i++)
        {
            var isFile = i == parts.Length - 1;
            var next = new List<string>();
            foreach (var dir in current)
            {
                if (parts[i].Contains('*') || parts[i].Contains('?'))
                {
                    next.AddRange(Matches(dir, parts[i], isFile, ref budget));
                }
                else
                {
                    var candidate = Path.Combine(dir, parts[i]);
                    if (IsPlain(candidate, isFile)) next.Add(candidate);
                }
            }
            current = next;
        }
        return current;
    }

    /// Every .exe below a folder, at most `limit` of them. Nothing when the folder, or any folder above it, is a link.
    public static IReadOnlyList<string> ExesUnder(string folder, int limit, Func<string, string>? expandEnv = null) =>
        ExesUnder(folder, limit, MaxEntries, out _, out _, expandEnv);

    /// The same, looking at no more than `maxEntries` files and folders. `visited`: how many it looked at. `capped`: it
    /// stopped at `limit` programs or `maxEntries` entries, so there may be more programs below the folder.
    public static IReadOnlyList<string> ExesUnder(string folder, int limit, int maxEntries, out int visited, out bool capped,
        Func<string, string>? expandEnv = null)
    {
        visited = 0;
        capped = false;
        var expanded = (expandEnv ?? Environment.ExpandEnvironmentVariables)(folder);
        if (expanded.Contains('%') || !SafePaths.TryLocal(expanded, out var full) || !Directory.Exists(full)) return [];
        var exes = new List<string>();
        try
        {
            var entries = new FileSystemEnumerable<string?>(full,
                (ref FileSystemEntry e) => !e.IsDirectory && e.FileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? e.ToFullPath() : null,
                Recursive);
            foreach (var exe in entries)
            {
                if (visited >= maxEntries)
                {
                    capped = true;
                    break;
                }
                visited++;
                if (exe is null) continue;
                exes.Add(exe);
                if (exes.Count >= limit)
                {
                    capped = true;
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // gone or unreadable: what was found so far
        }
        return exes;
    }

    // The folders (or files) directly in `dir` whose name matches `pattern`, looking at no more than `budget` entries.
    private static List<string> Matches(string dir, string pattern, bool files, ref int budget)
    {
        var found = new List<string>();
        try
        {
            var entries = new FileSystemEnumerable<string?>(dir,
                (ref FileSystemEntry e) => e.IsDirectory != files && FileSystemName.MatchesSimpleExpression(pattern, e.FileName) ? e.ToFullPath() : null,
                OneLevel);
            foreach (var path in entries)
            {
                if (budget <= 0) break;
                budget--;
                if (path is not null) found.Add(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // gone or unreadable: what was found so far
        }
        return found;
    }

    // An existing file or folder (as asked) that is not a link.
    private static bool IsPlain(string path, bool isFile)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return !attributes.HasFlag(FileAttributes.ReparsePoint) && attributes.HasFlag(FileAttributes.Directory) != isFile;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
