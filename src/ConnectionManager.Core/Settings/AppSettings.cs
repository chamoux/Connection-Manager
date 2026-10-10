using ConnectionManager.Core.Blocking;
using ConnectionManager.Core.Ipc;
using ConnectionManager.Core.Modes;

namespace ConnectionManager.Core.Settings;

/// A program, or every program in a folder, that the user wants blocked during games.
public sealed record UserBlock(string Path, bool IsFolder);

/// The edits below compare paths ignoring case, the way Windows does.
public sealed class AppSettings
{
    public const string DefaultHotkey = "Ctrl+Alt+G";

    public const int MinDownloadCapKBps = 16;
    public const int MaxDownloadCapKBps = 100_000;

    /// Game Mode. The JSON name stays "mode" so Phase 1 files load.
    public ModeSetting Mode { get; set; } = ModeSetting.Auto;
    public int GraceMinutes { get; set; } = 2;
    public string Hotkey { get; set; } = DefaultHotkey;
    public bool Notifications { get; set; } = true;

    public ModeSetting DataSaver { get; set; } = ModeSetting.Auto;
    public bool AutoOnMeteredWifi { get; set; } = true;
    public bool AllowWindowsDownloads { get; set; }
    public int WindowsDownloadCapKBps { get; set; } = 200;
    public bool WifiTuning { get; set; } = true;
    public bool TrafficMeter { get; set; } = true;
    public bool DetectNonSteamGames { get; set; } = true;
    public bool UploadCapsEnabled { get; set; } = true;

    /// Catalog entry id → on/off, overriding the entry's default.
    public Dictionary<string, bool> Catalog { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<UserBlock> ExtraBlocks { get; set; } = new();
    public List<string> AlwaysGames { get; set; } = new();
    public List<string> NeverGames { get; set; } = new();

    /// Programs Data Saver lets through.
    public List<string> AllowApps { get; set; } = new();

    /// Wi-Fi network names the user marked as a phone hotspot.
    public List<string> HotspotNetworks { get; set; } = new();

    public List<UploadCapArgs> UploadCaps { get; set; } = new();

    public void ToggleCatalogEntry(CatalogEntry entry) => Catalog[entry.Id] = !BuiltInCatalog.IsOn(entry, Catalog);

    /// False when the list already holds it.
    public bool AddBlock(UserBlock block)
    {
        if (ExtraBlocks.Exists(b => Same(b, block))) return false;
        ExtraBlocks.Add(block);
        return true;
    }

    public void RemoveBlock(UserBlock block) => ExtraBlocks.RemoveAll(b => Same(b, block));

    public void AddGame(string path) => Move(path, from: NeverGames, to: AlwaysGames);

    public void MarkNotAGame(string path) => Move(path, from: AlwaysGames, to: NeverGames);

    public void RemoveGame(string path, bool fromAlways) => (fromAlways ? AlwaysGames : NeverGames).RemoveAll(p => SamePath(p, path));

    private static void Move(string path, List<string> from, List<string> to)
    {
        from.RemoveAll(p => SamePath(p, path));
        if (!to.Exists(p => SamePath(p, path))) to.Add(path);
    }

    private static bool Same(UserBlock a, UserBlock b) => a.IsFolder == b.IsFolder && SamePath(a.Path, b.Path);

    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
