namespace ConnectionManager.Core.Blocking;

/// A known background updater. Paths may use %VARIABLES% and * wildcards; Folders block every .exe below them.
public sealed record CatalogEntry(string Id, string Name, bool EnabledByDefault, string[] Paths, string[] Folders, string[] Services);

/// The updaters Game Mode knows about. Sources: docs/superpowers/specs/2026-10-08-connection-manager-design.md.
public static class BuiltInCatalog
{
    public static readonly IReadOnlyList<CatalogEntry> Entries =
    [
        new("google-updater", "Google Updater", true,
            [
                @"%ProgramFiles(x86)%\Google\GoogleUpdater\*\updater.exe",
                @"%ProgramFiles%\Google\GoogleUpdater\*\updater.exe",
                @"%LOCALAPPDATA%\Google\GoogleUpdater\*\updater.exe",
                @"%ProgramFiles(x86)%\Google\Update\GoogleUpdate.exe",
                @"%ProgramFiles(x86)%\Google\Update\*\GoogleUpdate.exe",
                @"%LOCALAPPDATA%\Google\Update\GoogleUpdate.exe",
                @"%LOCALAPPDATA%\Google\Update\*\GoogleUpdate.exe",
            ],
            [], []),
        new("edge-update", "Microsoft Edge Update", true,
            [@"%ProgramFiles(x86)%\Microsoft\EdgeUpdate\*.exe", @"%ProgramFiles(x86)%\Microsoft\EdgeUpdate\*\*.exe"],
            [], []),
        // Network block only: Synapse keeps running, so DPI, macros and lighting keep working.
        new("razer", "Razer (Synapse, Cortex, updaters)", true,
            [], [@"%ProgramFiles(x86)%\Razer", @"%ProgramFiles%\Razer", @"%ProgramData%\Razer"], []),
        // Delivery Optimization also carries Store, Office and Edge downloads, which show up as svchost.
        new("windows-update", "Windows Update and Delivery Optimization", true,
            [], [], ["wuauserv", "BITS", "DoSvc"]),
        new("office", "Microsoft Office updates", true,
            [@"%ProgramFiles%\Common Files\microsoft shared\ClickToRun\OfficeClickToRun.exe"],
            [], []),
        new("adobe", "Adobe updaters", true,
            [@"%ProgramFiles(x86)%\Common Files\Adobe\ARM\*\AdobeARM.exe", @"%ProgramFiles%\Common Files\Adobe\ARM\*\AdobeARM.exe"],
            [], []),
        // Off by default: blocking it can also cut the NVIDIA overlay's online features.
        new("nvidia-app", "NVIDIA app (driver downloads)", false,
            [], [@"%ProgramFiles%\NVIDIA Corporation\NVIDIA app"], []),
    ];

    public static IEnumerable<CatalogEntry> Enabled(IReadOnlyDictionary<string, bool> overrides) => Entries.Where(e => IsOn(e, overrides));

    /// Your choice for the entry when you made one, otherwise its default.
    public static bool IsOn(CatalogEntry entry, IReadOnlyDictionary<string, bool> overrides) =>
        overrides.TryGetValue(entry.Id, out var on) ? on : entry.EnabledByDefault;
}
