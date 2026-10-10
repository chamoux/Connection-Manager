using ConnectionManager.Core.Logging;
using ConnectionManager.Core.Modes;
using ConnectionManager.Core.Settings;

namespace ConnectionManager.Core.Engine;

public static class ServiceStartup
{
    public const string SaveProblem = "Settings could not be saved, so the last change lasts only until the service restarts. The log says why.";

    /// Loads the settings, and turns a manual On back to Auto for both modes and saves that, so a switch someone forgot
    /// can't block updates forever. Problem is set when the save failed.
    public static (AppSettings Settings, string? Problem) LoadSettings(SettingsStore store, ILog log)
    {
        var settings = store.Load();
        if (!ResetManualOn(settings)) return (settings, null);
        log.Info("Game Mode and Data Saver start in Auto: a manual On does not survive a restart");
        try
        {
            store.Save(settings);
            return (settings, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Error("Could not save settings", ex);
            return (settings, SaveProblem);
        }
    }

    /// True when either mode was On.
    public static bool ResetManualOn(AppSettings settings)
    {
        var changed = false;
        if (settings.Mode == ModeSetting.On)
        {
            settings.Mode = ModeSetting.Auto;
            changed = true;
        }
        if (settings.DataSaver == ModeSetting.On)
        {
            settings.DataSaver = ModeSetting.Auto;
            changed = true;
        }
        return changed;
    }
}
