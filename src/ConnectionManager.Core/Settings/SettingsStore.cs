using System.Text.Json;
using ConnectionManager.Core.Ipc;
using ConnectionManager.Core.Modes;

namespace ConnectionManager.Core.Settings;

/// Loads and saves settings.json. The installer locks the folder so only admins can write it.
public sealed class SettingsStore
{
    private const int SaveAttempts = 4;

    private readonly string _path;

    public SettingsStore(string path) => _path = path;

    /// The pause between save attempts. Tests replace it so they need no timing.
    internal Action RetryDelay { get; init; } = () => Thread.Sleep(100);

    /// C:\ProgramData\ConnectionManager: settings.json and the logs folder.
    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ConnectionManager");

    /// A missing or unreadable file gives defaults. A broken one is copied to settings.json.bad and defaults are used.
    public AppSettings Load()
    {
        string text;
        try
        {
            if (!File.Exists(_path)) return new AppSettings();
            text = File.ReadAllText(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new AppSettings(); // locked or no access right now: run on defaults rather than not start
        }

        try
        {
            return Repair(JsonSerializer.Deserialize(text, SettingsJson.Default.AppSettings) ?? new AppSettings());
        }
        catch (JsonException)
        {
            KeepAside();
            return new AppSettings();
        }
    }

    private void KeepAside()
    {
        try
        {
            File.Copy(_path, _path + ".bad", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: losing the copy is better than failing to start.
        }
    }

    /// Flushes to disk, then swaps the new file in. The previous version is kept as settings.json.bak.
    /// A file another program holds open for a moment (antivirus, backup) is retried a few times.
    public void Save(AppSettings settings)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Write(settings);
                return;
            }
            catch (IOException ex) when (attempt < SaveAttempts && IsBusy(ex))
            {
                RetryDelay();
            }
        }
    }

    // ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION, and File.Replace's ERROR_UNABLE_TO_REMOVE_REPLACED,
    // ERROR_UNABLE_TO_MOVE_REPLACEMENT and _2. After _2 the old file is already settings.json.bak and the retry moves the new one in.
    private static bool IsBusy(IOException ex) =>
        ex.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021)
            or unchecked((int)0x80070497) or unchecked((int)0x80070498) or unchecked((int)0x80070499);

    private void Write(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, settings, SettingsJson.Default.AppSettings);
            stream.Flush(flushToDisk: true); // a power cut must never leave an empty settings file
        }
        if (File.Exists(_path)) File.Replace(temp, _path, _path + ".bak");
        else File.Move(temp, _path);
    }

    // A hand-edited file can hold nulls or silly numbers; fix them instead of failing later.
    private static AppSettings Repair(AppSettings s)
    {
        s.GraceMinutes = Math.Clamp(s.GraceMinutes, 0, 60);
        if (string.IsNullOrWhiteSpace(s.Hotkey)) s.Hotkey = AppSettings.DefaultHotkey;
        s.Catalog ??= new();
        s.ExtraBlocks ??= new();
        s.AlwaysGames ??= new();
        s.NeverGames ??= new();
        s.AllowApps ??= new();
        s.HotspotNetworks ??= new();
        s.UploadCaps ??= new();
        if (!Enum.IsDefined(s.Mode)) s.Mode = ModeSetting.Auto; // "mode": 7 parses, but means nothing
        if (!Enum.IsDefined(s.DataSaver)) s.DataSaver = ModeSetting.Auto;
        s.WindowsDownloadCapKBps = Math.Clamp(s.WindowsDownloadCapKBps, AppSettings.MinDownloadCapKBps, AppSettings.MaxDownloadCapKBps);
        var catalog = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase); // "Razer" still matches the razer entry
        foreach (var (id, on) in s.Catalog) catalog[id] = on;
        s.Catalog = catalog;
        s.ExtraBlocks.RemoveAll(b => b is null || string.IsNullOrWhiteSpace(b.Path));
        s.AlwaysGames.RemoveAll(string.IsNullOrWhiteSpace);
        s.NeverGames.RemoveAll(string.IsNullOrWhiteSpace);
        s.AllowApps.RemoveAll(string.IsNullOrWhiteSpace);
        s.HotspotNetworks.RemoveAll(string.IsNullOrWhiteSpace);
        s.UploadCaps.RemoveAll(c => c is null || string.IsNullOrWhiteSpace(c.Path));
        // The status sends every list in one frame, and the service refuses to add beyond this.
        Cap(s.ExtraBlocks);
        Cap(s.AlwaysGames);
        Cap(s.NeverGames);
        Cap(s.AllowApps);
        Cap(s.HotspotNetworks);
        Cap(s.UploadCaps);
        return s;
    }

    private static void Cap<T>(List<T> list)
    {
        if (list.Count > IpcLimits.MaxListEntries) list.RemoveRange(IpcLimits.MaxListEntries, list.Count - IpcLimits.MaxListEntries);
    }
}
