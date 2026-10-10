namespace ConnectionManager.Core.Blocking;

/// Programs Game Mode must never cut off: Windows itself, Steam and its libraries, voice chat, anti-cheat and this app.
public sealed class ProtectedPaths
{
    private static readonly string[] Names =
    [
        "steam.exe", "steamwebhelper.exe", "steamservice.exe", "gameoverlayui.exe", "discord.exe",
        "easyanticheat.exe", "easyanticheat_eos.exe", "beservice.exe", "beservice_x64.exe", "faceit.exe", "faceitservice.exe",
    ];

    private readonly string[] _folders; // each ends with '\'

    /// Blank, relative or malformed folders are skipped.
    public ProtectedPaths(IEnumerable<string?> folders) => _folders = folders.Select(PathRules.FolderPrefix).OfType<string>().ToArray();

    public static ProtectedPaths ForThisPc(string? steamPath, IEnumerable<string> steamLibraries, string appFolder) =>
        new([Environment.GetFolderPath(Environment.SpecialFolder.Windows), appFolder, steamPath, .. steamLibraries]);

    public bool IsProtected(string path) =>
        Names.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
        || _folders.Any(f => PathRules.IsUnder(path, f));
}
