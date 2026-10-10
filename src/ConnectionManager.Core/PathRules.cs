using ConnectionManager.Core.Paths;

namespace ConnectionManager.Core;

/// Windows path checks shared by game detection, Steam library discovery and the never-block rules.
public static class PathRules
{
    /// The normalised full path of a drive path (C:\...) or a network one (\\server\share\...): '/' becomes '\', and "."
    /// and ".." are applied. Strings only, never the disk: GetFullPath looks up a short (~) name, and as SYSTEM a lookup
    /// of a network path signs in to that server. Short names stay as they are; SafePaths expands the ones it has checked.
    /// False for blank, relative, device or malformed input: hand-edited settings can hold anything.
    public static bool TryFull(string? path, out string full)
    {
        full = "";
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0')) return false;
        if (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && IsSeparator(path[2]))
        {
            full = path[..2] + @"\" + SafePaths.JoinSegments(path[3..]);
            return true;
        }
        if (path.Length < 5 || !IsSeparator(path[0]) || !IsSeparator(path[1]) || IsSeparator(path[2])) return false;

        // \\server\share\rest. A server of "." or "?" makes it a device path (\\.\pipe, \\?\C:).
        var parts = path[2..].Split(['\\', '/'], 3);
        if (parts.Length < 2 || parts[0] is "." or "?" or ".." || parts[1] is "" or "." or "..") return false;
        var rest = parts.Length == 3 ? SafePaths.JoinSegments(parts[2]) : "";
        full = $@"\\{parts[0]}\{parts[1]}" + (rest.Length > 0 ? @"\" + rest : "");
        return true;
    }

    /// A folder as a prefix ending in exactly one '\', so "C:\Steam" never matches "C:\SteamTools". Null when invalid.
    public static string? FolderPrefix(string? folder) => TryFull(folder, out var full) ? full.TrimEnd('\\') + '\\' : null;

    public static bool IsUnder(string fullPath, string folderPrefix) => fullPath.StartsWith(folderPrefix, StringComparison.OrdinalIgnoreCase);

    private static bool IsSeparator(char c) => c is '\\' or '/';
}
