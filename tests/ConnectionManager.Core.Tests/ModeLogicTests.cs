using ConnectionManager.Core.Games;
using ConnectionManager.Core.Modes;

namespace ConnectionManager.Core.Tests;

public class ModeLogicTests
{
    private static readonly GameState Playing = new(true, "game.exe", null);

    [Theory]
    [InlineData(ModeSetting.Auto, false, false)]
    [InlineData(ModeSetting.Auto, true, true)]
    [InlineData(ModeSetting.On, false, true)]
    [InlineData(ModeSetting.Off, true, false)]
    public void The_setting_and_the_game_decide_game_mode(ModeSetting setting, bool playing, bool expected) =>
        Assert.Equal(expected, ModeLogic.IsGameModeActive(setting, playing ? Playing : GameState.Idle));

    [Theory]
    [InlineData(ModeSetting.Auto, ModeSetting.On)]
    [InlineData(ModeSetting.On, ModeSetting.Auto)]
    [InlineData(ModeSetting.Off, ModeSetting.On)]
    public void Toggle_switches_between_auto_and_on(ModeSetting from, ModeSetting to) =>
        Assert.Equal(to, ModeLogic.Toggle(from));
}
