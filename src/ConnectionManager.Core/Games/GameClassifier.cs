namespace ConnectionManager.Core.Games;

/// Decides whether a running program counts as a game: your lists first, then "inside a Steam library".
public sealed class GameClassifier
{
    // Steam folders and files that run around games but are not games: installers, crash reporters, wallpaper tools.
    private static readonly string[] ExcludedFolderNames =
        ["_CommonRedist", "__Installer", "Redist", "Redistributables", "DirectX", "Prerequisites", "Steamworks Shared", "wallpaper_engine"];
    private static readonly string[] ExcludedFileNameParts = ["CrashHandler", "CrashReport", "vc_redist", "vcredist", "dxsetup", "PrereqSetup"];

    private readonly string[] _libraryFolders; // each ends with '\'
    private readonly HashSet<string> _always;
    private readonly HashSet<string> _never;

    public GameClassifier(IEnumerable<string> steamCommonFolders, IEnumerable<string> alwaysGames, IEnumerable<string> neverGames)
    {
        _libraryFolders = steamCommonFolders.Select(PathRules.FolderPrefix).OfType<string>().ToArray();
        _always = FullPaths(alwaysGames);
        _never = FullPaths(neverGames);
    }

    public bool IsGame(string exePath)
    {
        if (!PathRules.TryFull(exePath, out var path)) return false;
        if (_never.Contains(path)) return false;
        if (_always.Contains(path)) return true;

        var library = _libraryFolders.FirstOrDefault(f => PathRules.IsUnder(path, f));
        if (library is null) return false;

        var folders = path[library.Length..].Split('\\')[..^1];
        if (folders.Any(f => ExcludedFolderNames.Contains(f, StringComparer.OrdinalIgnoreCase))) return false;
        var fileName = Path.GetFileName(path);
        return !ExcludedFileNameParts.Any(p => fileName.Contains(p, StringComparison.OrdinalIgnoreCase));
    }

    // Blank, relative or malformed entries (say, from a hand-edited settings.json) are skipped instead of failing start-up.
    private static HashSet<string> FullPaths(IEnumerable<string?> paths) =>
        new(paths.Select(p => PathRules.TryFull(p, out var full) ? full : null).OfType<string>(), StringComparer.OrdinalIgnoreCase);
}
