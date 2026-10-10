using ConnectionManager.Core.Games;

namespace ConnectionManager.Core.Tests;

public class GameClassifierTests
{
    private static readonly string[] Libraries = [@"C:\Steam\steamapps\common", @"D:\SteamLibrary\steamapps\common\"];

    private static GameClassifier Make(string[]? always = null, string[]? never = null) => new(Libraries, always ?? [], never ?? []);

    [Theory]
    [InlineData(@"C:\Steam\steamapps\common\Elden Ring\Game\eldenring.exe")]
    [InlineData(@"d:\steamlibrary\STEAMAPPS\common\Counter-Strike Global Offensive\game\bin\win64\cs2.exe")]
    public void A_program_inside_a_steam_library_is_a_game(string path) => Assert.True(Make().IsGame(path));

    [Theory]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe")]
    [InlineData(@"C:\Steam\steam.exe")]
    [InlineData(@"C:\Steam\steamapps\commonish\x.exe")]
    public void A_program_outside_the_libraries_is_not_a_game(string path) => Assert.False(Make().IsGame(path));

    [Theory]
    [InlineData(@"C:\Steam\steamapps\common\Steamworks Shared\_CommonRedist\vcredist\2022\VC_redist.x64.exe")]
    [InlineData(@"C:\Steam\steamapps\common\Some Game\_CommonRedist\DirectX\Jun2010\DXSETUP.exe")]
    [InlineData(@"C:\Steam\steamapps\common\Some Game\UnityCrashHandler64.exe")]
    [InlineData(@"C:\Steam\steamapps\common\wallpaper_engine\wallpaper64.exe")]
    public void Installers_crash_handlers_and_wallpaper_tools_are_not_games(string path) => Assert.False(Make().IsGame(path));

    [Fact]
    public void Your_always_list_works_outside_the_libraries() =>
        Assert.True(Make(always: [@"C:\Games\Minecraft\minecraft.exe"]).IsGame(@"c:\games\minecraft\MINECRAFT.EXE"));

    [Fact]
    public void Your_never_list_wins_inside_the_libraries()
    {
        const string tool = @"C:\Steam\steamapps\common\Lossless Scaling\LosslessScaling.exe";
        Assert.False(Make(never: [tool]).IsGame(tool));
    }

    [Fact]
    public void Blank_relative_and_null_list_entries_are_ignored()
    {
        var classifier = new GameClassifier(Libraries, ["", "  ", null!, "game.exe"], [null!, ""]);

        Assert.True(classifier.IsGame(@"C:\Steam\steamapps\common\Elden Ring\Game\eldenring.exe"));
        Assert.False(classifier.IsGame("game.exe"));
        Assert.False(classifier.IsGame(""));
    }

    [Fact]
    public void The_never_list_beats_the_always_list()
    {
        const string both = @"C:\Games\x.exe";
        Assert.False(Make(always: [both], never: [both]).IsGame(both));
    }
}
