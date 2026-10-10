using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ConnectionManager.Core.Blocking;
using ConnectionManager.Core.Ipc;
using ConnectionManager.Core.Modes;
using ConnectionManager.Core.Paths;
using ConnectionManager.Core.Qos;
using ConnectionManager.Core.Settings;

namespace ConnectionManager.Core.Engine;

// The pipe commands. Everything a client sends is untrusted: arguments go through Framing.ParseArgs (shape and length),
// paths the service will use through SafePaths, and numbers through their ranges. Errors are short plain messages that
// never repeat what the client sent. Each change is saved, logged with who made it, applied by a tick at once, and
// answered with the new status.
public sealed partial class ServiceEngine
{
    /// The contract's range for OptionsDto.GraceMinutes.
    public const int MaxGraceMinutes = 30;

    /// How many programs GetUsage returns.
    public const int UsageCount = 100;

    /// The largest status a command that adds to a list may lead to: well below what a pipe frame carries
    /// (IpcLimits.MaxServerFrame), since every list is in the status and a path can take 6 bytes a character escaped.
    public const int MaxStatusBytes = 1536 * 1024;

    private const string NotAvailable = "Not available yet.";
    private const string NotActive = "Only the active user can change settings.";
    private const string NotActiveToRead = "Only the active user can see the status.";
    private const string NotLocal = "Use a program or folder on a local drive. Network paths, device paths and links are not allowed.";
    private const string NotAProgram = "Pick a program, not a folder.";
    private const string TooMany = "Too many entries: remove some first.";

    private static readonly string ListFull = $"That list is full ({IpcLimits.MaxListEntries} entries). Remove one first.";

    private Response Handle(ClientIdentity client, Request request)
    {
        if (request.Cmd == Command.Subscribe) return Refuse(request, "Subscribe on a connection of its own.");
        // Reads too: the status, usage and reports belong to the active session's user, not to another one signed in.
        if (!client.MayChange) return Refuse(request, IsReadOnly(request.Cmd) ? NotActiveToRead : NotActive);
        try
        {
            return request.Cmd switch
            {
                Command.GetStatus => Ok(request),
                Command.SetGameMode => SetMode(client, request, gameMode: true),
                Command.SetDataSaver => SetMode(client, request, gameMode: false),
                Command.AddBlock => AddBlock(client, request),
                Command.RemoveBlock => RemoveBlock(client, request),
                Command.AddGame => AddGame(client, request),
                Command.MarkNotAGame => MarkNotAGame(client, request),
                Command.RemoveGame => RemoveGame(client, request),
                Command.ToggleCatalog => ToggleCatalog(client, request),
                Command.AllowApp => AllowApp(client, request),
                Command.RemoveAllowedApp => RemoveAllowedApp(client, request),
                Command.SetHotspot => SetHotspot(client, request),
                Command.SetMetered => SetMetered(client, request),
                Command.SetOptions => SetOptions(client, request),
                Command.GetUsage => GetUsage(request),
                Command.GetReports => GetReports(request),
                Command.DismissReport => DismissReport(client, request),
                Command.RestoreEverything => RestoreEverything(client, request),
                Command.ReportFullscreenGame => ReportFullscreenGame(client, request),
                Command.SetUploadCap => SetUploadCap(client, request),
                Command.RemoveUploadCap => RemoveUploadCap(client, request),
                _ => Refuse(request, "Unknown command."),
            };
        }
        catch (InvalidDataException ex) // from ParseArgs: short, and never repeats what the client sent
        {
            return Refuse(request, ex.Message);
        }
    }

    private static bool IsReadOnly(Command command) => command is Command.GetStatus or Command.Subscribe or Command.GetUsage or Command.GetReports;

    private Response SetMode(ClientIdentity client, Request request, bool gameMode)
    {
        var mode = Framing.ParseArgs<ModeArgs>(request).Mode;
        if (!Enum.IsDefined(mode)) return Refuse(request, "Unknown mode.");
        return Change(client, request, $"{(gameMode ? "Game Mode" : "Data Saver")} set to {mode}", () =>
        {
            if (gameMode) _settings.Mode = mode;
            else _settings.DataSaver = mode;
        });
    }

    private Response AddBlock(ClientIdentity client, Request request)
    {
        var args = Framing.ParseArgs<PathArgs>(request);
        if (!SafePaths.TryLocal(args.Path, out var path)) return Refuse(request, NotLocal);
        var block = new UserBlock(path, args.IsFolder);
        if (_controller.IsProtected(block))
            return Refuse(request, $"{NameOf(path)} is protected and won't be blocked: Game Mode never cuts off Windows, Steam, games, voice chat, anti-cheat or itself.");
        if (!_settings.ExtraBlocks.Exists(b => b.IsFolder == block.IsFolder && SamePath(b.Path, path)))
        {
            if (IsFull(_settings.ExtraBlocks)) return Refuse(request, ListFull);
            if (WouldOutgrowStatus(block, IpcJson.Wire.UserBlock)) return Refuse(request, TooMany);
        }
        return Change(client, request, $"Blocked during games: {path}{(args.IsFolder ? " (folder)" : "")}", () => _settings.AddBlock(block));
    }

    // Removing only compares strings, so the path needs no SafePaths check; ParseArgs has capped its length.
    private Response RemoveBlock(ClientIdentity client, Request request)
    {
        var args = Framing.ParseArgs<PathArgs>(request);
        return Change(client, request, $"No longer blocked during games: {SafePaths.Printable(args.Path)}",
            () => _settings.RemoveBlock(new UserBlock(args.Path, args.IsFolder)));
    }

    private Response AddGame(ClientIdentity client, Request request)
    {
        var args = Framing.ParseArgs<PathArgs>(request);
        if (args.IsFolder) return Refuse(request, NotAProgram);
        if (!SafePaths.TryLocal(args.Path, out var path)) return Refuse(request, NotLocal);
        // A game may use the internet during Data Saver, so a Windows program must never become one.
        if (IsWindowsProgram(path)) return Refuse(request, "Programs that are part of Windows can't be games.");
        if (!Contains(_settings.AlwaysGames, path))
        {
            if (IsFull(_settings.AlwaysGames)) return Refuse(request, ListFull);
            if (WouldOutgrowStatus(path, IpcJson.Wire.String)) return Refuse(request, TooMany);
        }
        return Change(client, request, $"Treated as a game: {path}", () => _settings.AddGame(path));
    }

    private Response MarkNotAGame(ClientIdentity client, Request request)
    {
        var args = Framing.ParseArgs<PathArgs>(request);
        if (args.IsFolder) return Refuse(request, NotAProgram);
        if (!SafePaths.TryLocal(args.Path, out var path)) return Refuse(request, NotLocal);
        if (!Contains(_settings.NeverGames, path))
        {
            if (IsFull(_settings.NeverGames)) return Refuse(request, ListFull);
            if (WouldOutgrowStatus(path, IpcJson.Wire.String)) return Refuse(request, TooMany);
        }
        return Change(client, request, $"Not a game: {path}", () => _settings.MarkNotAGame(path));
    }

    private Response RemoveGame(ClientIdentity client, Request request)
    {
        var args = Framing.ParseArgs<PathArgs>(request); // IsFolder means "from the always list"
        return Change(client, request, $"Removed from the {(args.IsFolder ? "games" : "not-a-game")} list: {SafePaths.Printable(args.Path)}",
            () => _settings.RemoveGame(args.Path, args.IsFolder));
    }

    private Response ToggleCatalog(ClientIdentity client, Request request)
    {
        var id = Framing.ParseArgs<IdArgs>(request).Id;
        var entry = BuiltInCatalog.Entries.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return Refuse(request, "Unknown catalog entry.");
        var on = !BuiltInCatalog.IsOn(entry, _settings.Catalog);
        return Change(client, request, $"{entry.Name} {(on ? "blocked" : "no longer blocked")} during games", () => _settings.ToggleCatalogEntry(entry));
    }

    private Response AllowApp(ClientIdentity client, Request request)
    {
        var args = Framing.ParseArgs<PathArgs>(request);
        if (args.IsFolder) return Refuse(request, NotAProgram);
        if (!SafePaths.TryLocal(args.Path, out var path)) return Refuse(request, NotLocal);
        // Allowing svchost.exe one by one would let every Windows download through; the rest is already handled.
        if (_controller.IsProtected(new UserBlock(path, false)))
            return Refuse(request, $"{NameOf(path)} can't be allowed one by one: Data Saver already decides for Windows, Steam, games, anti-cheat and Connection Manager.");
        if (Contains(_settings.AllowApps, path)) return Ok(request);
        if (IsFull(_settings.AllowApps)) return Refuse(request, ListFull);
        if (WouldOutgrowStatus(path, IpcJson.Wire.String)) return Refuse(request, TooMany);
        return Change(client, request, $"Allowed during Data Saver: {path}", () => _settings.AllowApps.Add(path));
    }

    private Response RemoveAllowedApp(ClientIdentity client, Request request)
    {
        var args = Framing.ParseArgs<PathArgs>(request);
        return Change(client, request, $"No longer allowed during Data Saver: {SafePaths.Printable(args.Path)}",
            () => _settings.AllowApps.RemoveAll(p => SamePath(p, args.Path)));
    }

    private Response SetHotspot(ClientIdentity client, Request request)
    {
        var args = Framing.ParseArgs<HotspotArgs>(request);
        if (args.Name.Any(char.IsControl)) return Refuse(request, "That network name has characters that can't be used.");
        var listed = _settings.HotspotNetworks.Contains(args.Name, StringComparer.Ordinal); // names are case-sensitive
        if (args.IsHotspot && !listed)
        {
            if (IsFull(_settings.HotspotNetworks)) return Refuse(request, ListFull);
            if (WouldOutgrowStatus(args.Name, IpcJson.Wire.String)) return Refuse(request, TooMany);
        }
        return Change(client, request, $"Network {args.Name} {(args.IsHotspot ? "marked" : "no longer marked")} as a hotspot", () =>
        {
            if (!args.IsHotspot) _settings.HotspotNetworks.RemoveAll(n => n == args.Name);
            else if (!listed) _settings.HotspotNetworks.Add(args.Name);
        });
    }

    private Response SetMetered(ClientIdentity client, Request request)
    {
        if (_parts.Metered is not { } metered) return Refuse(request, NotAvailable);
        var on = Framing.ParseArgs<OnArgs>(request).On;
        var problem = Call("The metered setting", () => metered.Set(_network, on));
        if (problem is not null) return Refuse(request, problem);
        _log.Info($"This Wi-Fi {(on ? "marked" : "no longer marked")} as metered by {Who(client)}");
        Tick();
        return Ok(request);
    }

    private Response SetOptions(ClientIdentity client, Request request)
    {
        var o = Framing.ParseArgs<OptionsDto>(request);
        if (o.GraceMinutes is < 0 or > MaxGraceMinutes) return Refuse(request, $"The grace time must be 0 to {MaxGraceMinutes} minutes.");
        if (o.WindowsDownloadCapKBps is < AppSettings.MinDownloadCapKBps or > AppSettings.MaxDownloadCapKBps)
            return Refuse(request, $"The Windows download cap must be {AppSettings.MinDownloadCapKBps} to {AppSettings.MaxDownloadCapKBps} KB/s.");
        var hotkey = o.Hotkey.Trim();
        if (!Hotkey.TryParse(hotkey, out _)) return Refuse(request, "That hotkey can't be used. Try something like Ctrl+Alt+G.");
        var capsSwitched = o.UploadCapsEnabled != _settings.UploadCapsEnabled;
        return Change(client, request, "Options changed", () =>
        {
            var s = _settings;
            s.GraceMinutes = o.GraceMinutes;
            s.Notifications = o.Notifications;
            s.Hotkey = hotkey;
            s.AutoOnMeteredWifi = o.AutoOnMeteredWifi;
            s.AllowWindowsDownloads = o.AllowWindowsDownloads;
            s.WindowsDownloadCapKBps = o.WindowsDownloadCapKBps;
            s.WifiTuning = o.WifiTuning;
            s.TrafficMeter = o.TrafficMeter;
            s.DetectNonSteamGames = o.DetectNonSteamGames;
            s.UploadCapsEnabled = o.UploadCapsEnabled;
        }, uploadCaps: capsSwitched);
    }

    private Response SetUploadCap(ClientIdentity client, Request request)
    {
        if (_parts.UploadCaps is null) return Refuse(request, NotAvailable);
        var args = Framing.ParseArgs<UploadCapArgs>(request);
        if (args.KBps is < UploadCapManager.MinKBps or > UploadCapManager.MaxKBps)
            return Refuse(request, Invariant($"An upload cap must be {UploadCapManager.MinKBps} to {UploadCapManager.MaxKBps:N0} KB/s."));
        if (!SafePaths.TryLocal(args.Path, out var path)) return Refuse(request, NotLocal);
        // QoS expands environment variables in the path, so one with a '%' would match some other program.
        if (path.Contains('%')) return Refuse(request, "Upload caps can't be used for a program with % in its path.");
        if (Directory.Exists(path)) return Refuse(request, NotAProgram);
        var caps = _settings.UploadCaps;
        var index = caps.FindIndex(c => c is not null && SamePath(c.Path, path));
        if (index < 0 && caps.Count >= UploadCapManager.MaxCaps)
            return Refuse(request, $"At most {UploadCapManager.MaxCaps} apps can have an upload cap. Remove one first.");
        if (index < 0 && WouldOutgrowStatus(new UploadCapArgs(path, args.KBps), IpcJson.Wire.UploadCapArgs)) return Refuse(request, TooMany);
        return Change(client, request, Invariant($"Upload cap for {path}: {args.KBps} KB/s"), () =>
        {
            var cap = new UploadCapArgs(path, args.KBps);
            if (index < 0) caps.Add(cap);
            else caps[index] = cap; // one cap per program: a new speed replaces the old one
        }, uploadCaps: true);
    }

    // Removing only compares strings, so the path needs no SafePaths check; ParseArgs has capped its length.
    private Response RemoveUploadCap(ClientIdentity client, Request request)
    {
        if (_parts.UploadCaps is null) return Refuse(request, NotAvailable);
        var args = Framing.ParseArgs<PathArgs>(request);
        return Change(client, request, $"Upload cap removed: {SafePaths.Printable(args.Path)}",
            () => _settings.UploadCaps.RemoveAll(c => c is not null && SamePath(c.Path, args.Path)), uploadCaps: true);
    }

    private Response GetUsage(Request request) => _parts.Meter is { } meter
        ? new Response(request.Id, true, null, JsonSerializer.SerializeToElement(meter.Top(UsageCount), IpcJson.Default.IReadOnlyListUsageDto))
        : Refuse(request, NotAvailable);

    private Response GetReports(Request request) => _parts.Reports is { } reports
        ? new Response(request.Id, true, null, JsonSerializer.SerializeToElement(reports.All(), IpcJson.Default.IReadOnlyListReportDto))
        : Refuse(request, NotAvailable);

    private Response DismissReport(ClientIdentity client, Request request)
    {
        if (_parts.Reports is not { } reports) return Refuse(request, NotAvailable);
        var id = Framing.ParseArgs<IdArgs>(request).Id;
        if (!reports.Dismiss(id)) return Refuse(request, "There is no such report.");
        _log.Info($"Report {SafePaths.Printable(id)} dismissed by {Who(client)}");
        Tick();
        return Ok(request);
    }

    private Response RestoreEverything(ClientIdentity client, Request request)
    {
        _log.Info($"Restore everything requested by {Who(client)}");
        _restore.RunAll("on request", includeUserChoices: true);
        Tick();
        return Ok(request);
    }

    // uploadCaps: the change touched the caps or their switch, so they go to Windows QoS again.
    private Response Change(ClientIdentity client, Request request, string what, Action change, bool uploadCaps = false)
    {
        change();
        Save();
        ApplySettings();
        if (uploadCaps) QueueUploadCaps();
        _log.Info($"{what} by {Who(client)}");
        Tick();
        return Ok(request);
    }

    // A failed save keeps the change in memory, and the status says it won't survive a restart.
    private void Save()
    {
        try
        {
            _store.Save(_settings);
            _settingsProblem = null;
            _lastSaveError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (ex.Message != _lastSaveError) _log.Error("Could not save settings", ex);
            _lastSaveError = ex.Message;
            _settingsProblem = ServiceStartup.SaveProblem;
        }
    }

    private Response Ok(Request request)
    {
        if (_status is null) PublishIfChanged(); // a GetStatus before the first tick
        return new Response(request.Id, true, null, _status is { } status ? JsonSerializer.SerializeToElement(status, IpcJson.Default.StatusDto) : null);
    }

    private static Response Refuse(Request request, string error) => new(request.Id, false, error);

    private static string Who(ClientIdentity client) =>
        Invariant($"{SafePaths.Printable(client.ExePath ?? "an unknown program")} (pid {client.Pid})");

    private static string NameOf(string path) => Path.GetFileName(path) is { Length: > 0 } name ? name : path;

    private static bool IsWindowsProgram(string path) =>
        PathRules.FolderPrefix(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) is { } windows && PathRules.IsUnder(path, windows);

    private static bool IsFull<T>(List<T> list) => list.Count >= IpcLimits.MaxListEntries;

    // The status as it is plus the new entry as the status would carry it, and a little for the separator: a list entry
    // appears in the status once.
    private bool WouldOutgrowStatus<T>(T entry, JsonTypeInfo<T> type)
    {
        if (_status is null) PublishIfChanged();
        return _statusJson.Length + JsonSerializer.SerializeToUtf8Bytes(entry, type).Length + 16 > MaxStatusBytes;
    }

    private static bool Contains(List<string> list, string path) => list.Exists(p => SamePath(p, path));

    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
