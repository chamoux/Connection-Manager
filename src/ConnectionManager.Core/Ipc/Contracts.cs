using System.Text.Json;
using ConnectionManager.Core.Modes;
using ConnectionManager.Core.Settings;

namespace ConnectionManager.Core.Ipc;

// The wire protocol between the service, the tray and the web UI. Names are camelCase on the wire, enum values too ("getStatus", "auto").

public enum Command
{
    GetStatus, Subscribe,
    SetGameMode, SetDataSaver,                    // ModeArgs
    AddBlock, RemoveBlock,                        // PathArgs (IsFolder used)
    AddGame, MarkNotAGame, RemoveGame,            // PathArgs (RemoveGame: IsFolder = "from the always list")
    ToggleCatalog,                                // IdArgs
    AllowApp, RemoveAllowedApp,                   // PathArgs
    SetHotspot,                                   // HotspotArgs: mark/unmark a network name as a hotspot
    SetMetered,                                   // OnArgs: current Wi-Fi network metered on/off
    SetOptions,                                   // OptionsDto (whole object, validated)
    GetUsage, GetReports, DismissReport,          // -, -, IdArgs
    RestoreEverything,
    ReportFullscreenGame,                         // GameArgs (Phase 3)
    SetUploadCap, RemoveUploadCap,                // UploadCapArgs / PathArgs (Phase 3)
}

public sealed record Request(int V, int Id, Command Cmd, JsonElement? Args = null);           // V = 1
public sealed record Response(int Id, bool Ok, string? Error = null, JsonElement? Data = null); // Id 0 = pushed StatusDto

public sealed record ModeArgs(ModeSetting Mode);
public sealed record PathArgs(string Path, bool IsFolder = false);
public sealed record IdArgs(string Id);
public sealed record HotspotArgs(string Name, bool IsHotspot);
public sealed record OnArgs(bool On);
public sealed record GameArgs(int Pid, string Path);
public sealed record UploadCapArgs(string Path, int KBps);

public sealed record OptionsDto(
    int GraceMinutes,            // 0..30
    bool Notifications,
    string Hotkey,               // must parse with Hotkey.TryParse
    bool AutoOnMeteredWifi,
    bool AllowWindowsDownloads,
    int WindowsDownloadCapKBps,  // 16..100000
    bool WifiTuning,
    bool TrafficMeter,
    bool DetectNonSteamGames,    // Phase 3
    bool UploadCapsEnabled);     // Phase 3

public sealed record StatusDto(
    long Version,
    ModeSetting GameMode, bool GameActive, string? GameName, DateTimeOffset? GraceEndsAt,
    IReadOnlyList<string> RunningGames, int BlockedCount,
    ModeSetting DataSaver, bool SaverActive, string SaverReason, int AllowedCount,
    NetworkDto Network,
    IReadOnlyList<string> Problems,
    SettingsDto Settings,
    ReportSummaryDto? LatestReport);

public sealed record NetworkDto(string Kind /* "ethernet" | "wifi" | "none" */, string? Name, bool Metered,
    bool IsHotspot, string? HotspotReason, bool CanMarkMetered, bool MarkedMeteredByUs);

public sealed record SettingsDto(OptionsDto Options, IReadOnlyList<CatalogItemDto> Catalog,
    IReadOnlyList<UserBlock> ExtraBlocks, IReadOnlyList<string> AlwaysGames, IReadOnlyList<string> NeverGames,
    IReadOnlyList<string> AllowApps, IReadOnlyList<string> HotspotNetworks, IReadOnlyList<UploadCapArgs> UploadCaps);
public sealed record CatalogItemDto(string Id, string Label, bool On);
public sealed record UsageDto(string Path, string Name, long BytesIn, long BytesOut);                  // GetUsage: list, last 24 h, top 100
public sealed record ReportSummaryDto(string Id, string Game, DateTimeOffset EndedAt, long TotalBytes, int AppCount);
public sealed record ReportDto(string Id, string Game, DateTimeOffset StartedAt, DateTimeOffset EndedAt, IReadOnlyList<UsageDto> Apps);
