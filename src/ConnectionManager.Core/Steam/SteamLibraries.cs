using System.Security;
using System.Security.Principal;
using System.Text;
using ConnectionManager.Core.Paths;
using Microsoft.Win32;

namespace ConnectionManager.Core.Steam;

/// Finds the steamapps\common folders of every Steam library on this PC.
public static class SteamLibraries
{
    /// libraryfolders.vdf is a few KB; anything bigger than this is not read.
    public const int MaxFileBytes = 1024 * 1024;

    /// Steam's own folder from the registry, or null when Steam is not installed. For a process running as the user.
    public static string? ReadSteamPath()
    {
        using var user = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        if (user?.GetValue("SteamPath") is string path && path.Length > 0) return path;
        using var machine = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
        return machine?.GetValue("InstallPath") as string;
    }

    /// For the SYSTEM service: Steam's folder for the signed-in user (null when no one is). The machine-wide install path
    /// comes first, then the user's own SteamPath. Both can hold anything, so only one that passes SafePaths is used.
    public static string? ReadSteamPathForUser(string? userSid) => ReadSteamPath(ReadMachineInstallPath, () => ReadUserSteamPath(userSid));

    internal static string? ReadSteamPath(Func<string?> machine, Func<string?> user) =>
        SafePaths.TryLocal(machine(), out var full) || SafePaths.TryLocal(user(), out full) ? full : null;

    private static string? ReadMachineInstallPath() =>
        ReadString(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");

    private static string? ReadUserSteamPath(string? userSid)
    {
        if (userSid is null) return null;
        string sid;
        try
        {
            sid = new SecurityIdentifier(userSid).Value; // canonical S-1-5-..., so it can only name a hive
        }
        catch (ArgumentException)
        {
            return null;
        }
        return ReadString(Registry.Users, sid + @"\Software\Valve\Steam", "SteamPath");
    }

    // The raw text: expanding a REG_EXPAND_SZ here would use the service's environment, not the user's.
    private static string? ReadString(RegistryKey hive, string key, string name)
    {
        try
        {
            using var opened = hive.OpenSubKey(key);
            return opened?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        }
        catch (Exception ex) when (ex is SecurityException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// One steamapps\common folder per library, Steam's own folder first. `readFile` returns null for a missing file.
    public static IReadOnlyList<string> FindCommonFolders(string? steamPath, Func<string, string?> readFile) =>
        Find(steamPath, readFile, path => PathRules.TryFull(path, out var full) ? full : null);

    /// The same for the SYSTEM service: a normal user can edit Steam's files, so Steam's folder and every library root
    /// must pass SafePaths before it counts. Pass ReadUntrustedFile as `readFile`.
    public static IReadOnlyList<string> FindCommonFoldersSafely(string? steamPath, Func<string, string?> readFile) =>
        Find(steamPath, readFile, path => SafePaths.TryLocal(path, out var full) ? full : null);

    private static IReadOnlyList<string> Find(string? steamPath, Func<string, string?> readFile, Func<string?, string?> check)
    {
        if (check(steamPath) is not { } steamFull) return [];
        var steam = TrimSlash(steamFull);
        var roots = new List<string> { steam };
        foreach (var file in new[] { Path.Combine(steam, "steamapps", "libraryfolders.vdf"), Path.Combine(steam, "config", "libraryfolders.vdf") })
        {
            if (readFile(file) is not { } text) continue;
            try
            {
                var found = 0;
                foreach (var root in ParseLibraryRoots(text))
                {
                    if (check(root) is not { } full) continue; // a bad entry is skipped; the good ones around it still count
                    roots.Add(TrimSlash(full));
                    found++;
                }
                if (found > 0) break; // an empty or useless file falls through to the other copy
            }
            catch (FormatException)
            {
                // Damaged: try the other copy. Steam's own library stays either way.
            }
        }
        return roots.Distinct(StringComparer.OrdinalIgnoreCase).Select(r => Path.Combine(r, "steamapps", "common")).ToList();
    }

    /// Library folders from libraryfolders.vdf, in both the old ("1" "D:\\Lib") and new ("1" { "path" ... }) layouts.
    public static IReadOnlyList<string> ParseLibraryRoots(string vdfText)
    {
        var folders = Vdf.Parse(vdfText)["libraryfolders"];
        if (folders is null) return [];
        var roots = new List<string>();
        foreach (var (key, node) in folders.Children)
        {
            if (node.Value is { } oldStyle && int.TryParse(key, out _)) roots.Add(oldStyle);
            else if (node["path"]?.Value is { } path) roots.Add(path);
        }
        return roots;
    }

    private static string TrimSlash(string full) => full.Length > 3 ? full.TrimEnd('\\') : full;

    public static string? ReadFileOrNull(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// A file a normal user can write, read by the SYSTEM service: only through SafePaths, and only up to MaxFileBytes.
    /// Null when it is missing, unsafe, too big or unreadable.
    public static string? ReadUntrustedFile(string path)
    {
        if (!SafePaths.TryLocal(path, out var full)) return null;
        try
        {
            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxFileBytes) return null;
            var buffer = new byte[stream.Length];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false); // it may shrink meanwhile, never grow past this
            using var reader = new StreamReader(new MemoryStream(buffer, 0, read), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
