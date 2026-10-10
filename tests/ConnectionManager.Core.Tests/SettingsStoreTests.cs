using ConnectionManager.Core.Ipc;
using ConnectionManager.Core.Modes;
using ConnectionManager.Core.Settings;

namespace ConnectionManager.Core.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly TempTree _tree = new();
    private string FilePath => Path.Combine(_tree.Root, "settings.json");

    [Fact]
    public void A_missing_file_gives_defaults()
    {
        var settings = new SettingsStore(FilePath).Load();

        Assert.Equal(ModeSetting.Auto, settings.Mode);
        Assert.Equal(2, settings.GraceMinutes);
        Assert.Equal("Ctrl+Alt+G", settings.Hotkey);
        Assert.True(settings.Notifications);
    }

    [Fact]
    public void Saves_and_loads_everything()
    {
        var store = new SettingsStore(FilePath);
        store.Save(new AppSettings
        {
            Mode = ModeSetting.On,
            GraceMinutes = 5,
            Catalog = { ["nvidia-app"] = true },
            ExtraBlocks = { new UserBlock(@"C:\Tools\updater.exe", false) },
            AlwaysGames = { @"C:\Games\x.exe" },
            NeverGames = { @"C:\Steam\steamapps\common\Tool\tool.exe" },
        });

        var loaded = store.Load();

        Assert.Equal(ModeSetting.On, loaded.Mode);
        Assert.Equal(5, loaded.GraceMinutes);
        Assert.True(loaded.Catalog["nvidia-app"]);
        Assert.Equal(new UserBlock(@"C:\Tools\updater.exe", false), Assert.Single(loaded.ExtraBlocks));
        Assert.Equal(@"C:\Games\x.exe", Assert.Single(loaded.AlwaysGames));
        Assert.Single(loaded.NeverGames);
        Assert.Contains("\"mode\": \"On\"", File.ReadAllText(FilePath));
    }

    [Fact]
    public void A_broken_file_is_kept_aside_and_defaults_are_used()
    {
        File.WriteAllText(FilePath, "{ not json");

        var settings = new SettingsStore(FilePath).Load();

        Assert.Equal(ModeSetting.Auto, settings.Mode);
        Assert.True(File.Exists(FilePath + ".bad"));
    }

    [Fact]
    public void A_locked_file_gives_defaults_instead_of_failing()
    {
        File.WriteAllText(FilePath, """{ "mode": "On" }""");
        using var locked = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.Equal(ModeSetting.Auto, new SettingsStore(FilePath).Load().Mode);
    }

    [Fact]
    public void Out_of_range_and_missing_values_are_repaired()
    {
        File.WriteAllText(FilePath, """{ "graceMinutes": 500, "extraBlocks": null, "hotkey": null }""");

        var settings = new SettingsStore(FilePath).Load();

        Assert.Equal(60, settings.GraceMinutes);
        Assert.Empty(settings.ExtraBlocks);
        Assert.Equal("Ctrl+Alt+G", settings.Hotkey);
    }

    [Fact]
    public void Blank_list_entries_are_dropped()
    {
        File.WriteAllText(FilePath, """{ "alwaysGames": ["", "C:\\Games\\x.exe", null], "extraBlocks": [null, { "path": "", "isFolder": true }] }""");

        var settings = new SettingsStore(FilePath).Load();

        Assert.Equal(@"C:\Games\x.exe", Assert.Single(settings.AlwaysGames));
        Assert.Empty(settings.ExtraBlocks);
    }

    [Fact]
    public void Saving_keeps_the_previous_version_as_a_backup()
    {
        var store = new SettingsStore(FilePath);
        store.Save(new AppSettings { GraceMinutes = 3 });
        store.Save(new AppSettings { GraceMinutes = 4 });

        Assert.Equal(4, store.Load().GraceMinutes);
        Assert.Equal(3, new SettingsStore(FilePath + ".bak").Load().GraceMinutes);
    }

    [Theory]
    [InlineData(".tmp")] // a sharing violation
    [InlineData(".bak")] // File.Replace cannot remove the old backup: error 1175
    public void A_file_held_open_for_a_moment_is_saved_once_it_is_free(string suffix)
    {
        File.WriteAllText(FilePath, "{}");
        File.WriteAllText(FilePath + suffix, "{}");
        var held = new FileStream(FilePath + suffix, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var store = new SettingsStore(FilePath) { RetryDelay = held.Dispose }; // the other program lets go while Save waits

            store.Save(new AppSettings { GraceMinutes = 7 });

            Assert.Equal(7, store.Load().GraceMinutes);
        }
        finally
        {
            held.Dispose();
        }
    }

    [Fact]
    public void A_file_held_open_too_long_still_fails_after_a_few_tries()
    {
        using var held = new FileStream(FilePath + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None);
        var waits = 0;
        var store = new SettingsStore(FilePath) { RetryDelay = () => waits++ };

        Assert.Throws<IOException>(() => store.Save(new AppSettings()));
        Assert.Equal(3, waits);
    }

    [Fact]
    public void Saving_works_after_a_failed_replace_left_only_the_backup() // what File.Replace error 1177 leaves behind
    {
        File.WriteAllText(FilePath + ".bak", """{ "graceMinutes": 3 }""");
        File.WriteAllText(FilePath + ".tmp", "half-written");
        var store = new SettingsStore(FilePath);

        store.Save(new AppSettings { GraceMinutes = 7 });

        Assert.Equal(7, store.Load().GraceMinutes);
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public void An_unknown_mode_is_reset_and_catalog_ids_ignore_case()
    {
        File.WriteAllText(FilePath, """{ "mode": 7, "catalog": { "Razer": false } }""");

        var settings = new SettingsStore(FilePath).Load();

        Assert.Equal(ModeSetting.Auto, settings.Mode);
        Assert.False(settings.Catalog["razer"]);
    }

    [Fact]
    public void A_phase_1_file_loads_with_the_new_defaults()
    {
        File.WriteAllText(FilePath, """
            {
              "mode": "On",
              "graceMinutes": 5,
              "hotkey": "Ctrl+Shift+F9",
              "notifications": false,
              "catalog": {
                "nvidia-app": true
              },
              "extraBlocks": [
                {
                  "path": "C:\\Tools\\updater.exe",
                  "isFolder": false
                }
              ],
              "alwaysGames": [
                "C:\\Games\\x.exe"
              ],
              "neverGames": []
            }
            """);

        var settings = new SettingsStore(FilePath).Load();

        Assert.Equal(ModeSetting.On, settings.Mode);
        Assert.Equal(5, settings.GraceMinutes);
        Assert.Equal("Ctrl+Shift+F9", settings.Hotkey);
        Assert.False(settings.Notifications);
        Assert.True(settings.Catalog["nvidia-app"]);
        Assert.Equal(new UserBlock(@"C:\Tools\updater.exe", false), Assert.Single(settings.ExtraBlocks));
        Assert.Equal(@"C:\Games\x.exe", Assert.Single(settings.AlwaysGames));
        Assert.Equal(ModeSetting.Auto, settings.DataSaver);
        Assert.Empty(settings.AllowApps);
        Assert.Empty(settings.HotspotNetworks);
        Assert.True(settings.AutoOnMeteredWifi);
        Assert.False(settings.AllowWindowsDownloads);
        Assert.Equal(200, settings.WindowsDownloadCapKBps);
        Assert.True(settings.WifiTuning);
        Assert.True(settings.TrafficMeter);
        Assert.True(settings.DetectNonSteamGames);
        Assert.True(settings.UploadCapsEnabled);
        Assert.Empty(settings.UploadCaps);
        Assert.False(File.Exists(FilePath + ".bad"));
    }

    [Fact]
    public void Saves_and_loads_the_phase_2_settings()
    {
        var store = new SettingsStore(FilePath);
        store.Save(new AppSettings
        {
            Mode = ModeSetting.Off,
            DataSaver = ModeSetting.On,
            AllowApps = { @"C:\Apps\chat.exe" },
            HotspotNetworks = { "Charbel's iPhone" },
            AutoOnMeteredWifi = false,
            AllowWindowsDownloads = true,
            WindowsDownloadCapKBps = 64,
            WifiTuning = false,
            TrafficMeter = false,
            DetectNonSteamGames = false,
            UploadCapsEnabled = false,
            UploadCaps = { new UploadCapArgs(@"C:\Apps\obs.exe", 500) },
        });

        var loaded = store.Load();

        Assert.Equal(ModeSetting.Off, loaded.Mode);
        Assert.Equal(ModeSetting.On, loaded.DataSaver);
        Assert.Equal(@"C:\Apps\chat.exe", Assert.Single(loaded.AllowApps));
        Assert.Equal("Charbel's iPhone", Assert.Single(loaded.HotspotNetworks));
        Assert.False(loaded.AutoOnMeteredWifi);
        Assert.True(loaded.AllowWindowsDownloads);
        Assert.Equal(64, loaded.WindowsDownloadCapKBps);
        Assert.False(loaded.WifiTuning);
        Assert.False(loaded.TrafficMeter);
        Assert.False(loaded.DetectNonSteamGames);
        Assert.False(loaded.UploadCapsEnabled);
        Assert.Equal(new UploadCapArgs(@"C:\Apps\obs.exe", 500), Assert.Single(loaded.UploadCaps));
        var text = File.ReadAllText(FilePath);
        Assert.Contains("\"mode\": \"Off\"", text); // Game Mode keeps its Phase 1 name
        Assert.Contains("\"dataSaver\": \"On\"", text);
    }

    [Fact]
    public void Phase_2_values_are_repaired()
    {
        File.WriteAllText(FilePath, """
            { "dataSaver": 9, "windowsDownloadCapKBps": 1, "allowApps": null, "hotspotNetworks": ["", null, "Phone"],
              "uploadCaps": [null, { "path": "", "kBps": 10 }, { "path": "C:\\obs.exe", "kBps": 10 }] }
            """);

        var settings = new SettingsStore(FilePath).Load();

        Assert.Equal(ModeSetting.Auto, settings.DataSaver);
        Assert.Equal(16, settings.WindowsDownloadCapKBps);
        Assert.Empty(settings.AllowApps);
        Assert.Equal("Phone", Assert.Single(settings.HotspotNetworks));
        Assert.Equal(@"C:\obs.exe", Assert.Single(settings.UploadCaps).Path);
    }

    [Fact]
    public void A_huge_download_cap_is_brought_down() // the contract allows 16..100000 KB/s
    {
        File.WriteAllText(FilePath, """{ "windowsDownloadCapKBps": 5000000 }""");

        Assert.Equal(100_000, new SettingsStore(FilePath).Load().WindowsDownloadCapKBps);
    }

    [Fact]
    public void Each_list_is_cut_to_the_ipc_limit() // the whole status, with every list, has to fit in one frame
    {
        var many = Enumerable.Range(0, IpcLimits.MaxListEntries + 50).Select(i => $@"C:\Apps\app{i}.exe").ToList();
        new SettingsStore(FilePath).Save(new AppSettings
        {
            ExtraBlocks = many.Select(p => new UserBlock(p, false)).ToList(),
            AlwaysGames = many.ToList(),
            NeverGames = many.ToList(),
            AllowApps = many.ToList(),
            HotspotNetworks = many.ToList(),
            UploadCaps = many.Select(p => new UploadCapArgs(p, 100)).ToList(),
        });

        var settings = new SettingsStore(FilePath).Load();

        Assert.All(new[] { settings.ExtraBlocks.Count, settings.AlwaysGames.Count, settings.NeverGames.Count, settings.AllowApps.Count,
            settings.HotspotNetworks.Count, settings.UploadCaps.Count }, n => Assert.Equal(IpcLimits.MaxListEntries, n));
        Assert.Equal(@"C:\Apps\app0.exe", settings.AllowApps[0]); // the oldest entries are kept
    }

    public void Dispose() => _tree.Dispose();
}
