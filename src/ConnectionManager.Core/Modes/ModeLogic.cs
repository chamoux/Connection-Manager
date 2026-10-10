using ConnectionManager.Core.Games;

namespace ConnectionManager.Core.Modes;

/// The user's choice for Game Mode and Data Saver. The JSON names (Auto, On, Off) are stored in settings.json.
public enum ModeSetting { Auto, On, Off }

public static class ModeLogic
{
    public static bool IsGameModeActive(ModeSetting setting, GameState state) => setting switch
    {
        ModeSetting.On => true,
        ModeSetting.Off => false,
        _ => state.Active,
    };

    /// The hotkey and a double-click on the tray icon switch between forced on and automatic detection.
    public static ModeSetting Toggle(ModeSetting setting) => setting == ModeSetting.On ? ModeSetting.Auto : ModeSetting.On;
}
