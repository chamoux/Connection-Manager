using ConnectionManager.Core.Blocking;
using ConnectionManager.Core.Settings;

namespace ConnectionManager.Core.Tests;

public class AppSettingsTests
{
    private readonly AppSettings _settings = new();

    [Fact]
    public void Adding_a_block_twice_ignores_case()
    {
        Assert.True(_settings.AddBlock(new UserBlock(@"C:\Tools\Updater.exe", false)));
        Assert.False(_settings.AddBlock(new UserBlock(@"c:\tools\updater.EXE", false)));

        Assert.Single(_settings.ExtraBlocks);
    }

    [Fact]
    public void Removing_a_block_ignores_case()
    {
        _settings.AddBlock(new UserBlock(@"C:\Tools", true));

        _settings.RemoveBlock(new UserBlock(@"c:\TOOLS", true));

        Assert.Empty(_settings.ExtraBlocks);
    }

    [Fact]
    public void Adding_a_game_moves_it_off_the_not_a_game_list()
    {
        _settings.NeverGames.Add(@"C:\Games\X.exe");

        _settings.AddGame(@"c:\games\x.exe");
        _settings.AddGame(@"C:\GAMES\X.EXE");

        Assert.Equal(@"c:\games\x.exe", Assert.Single(_settings.AlwaysGames));
        Assert.Empty(_settings.NeverGames);
    }

    [Fact]
    public void Marking_not_a_game_moves_it_off_the_always_list()
    {
        _settings.AlwaysGames.Add(@"C:\Games\X.exe");

        _settings.MarkNotAGame(@"c:\games\x.exe");
        _settings.MarkNotAGame(@"C:\GAMES\X.EXE");

        Assert.Equal(@"c:\games\x.exe", Assert.Single(_settings.NeverGames));
        Assert.Empty(_settings.AlwaysGames);
    }

    [Fact]
    public void Removing_a_game_ignores_case_and_leaves_the_other_list()
    {
        _settings.AlwaysGames.Add(@"C:\Games\X.exe");
        _settings.NeverGames.Add(@"C:\Games\X.exe");

        _settings.RemoveGame(@"c:\games\x.exe", fromAlways: true);

        Assert.Empty(_settings.AlwaysGames);
        Assert.Single(_settings.NeverGames);
    }

    [Fact]
    public void Toggling_a_catalog_entry_flips_its_current_state()
    {
        var razer = BuiltInCatalog.Entries.Single(e => e.Id == "razer");
        var nvidia = BuiltInCatalog.Entries.Single(e => e.Id == "nvidia-app");

        _settings.ToggleCatalogEntry(razer);
        _settings.ToggleCatalogEntry(nvidia);
        Assert.False(_settings.Catalog["razer"]);
        Assert.True(_settings.Catalog["nvidia-app"]);

        _settings.ToggleCatalogEntry(razer);
        Assert.True(_settings.Catalog["razer"]);
    }
}
