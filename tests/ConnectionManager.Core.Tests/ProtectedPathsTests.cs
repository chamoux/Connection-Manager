using ConnectionManager.Core.Blocking;

namespace ConnectionManager.Core.Tests;

public class ProtectedPathsTests
{
    private readonly ProtectedPaths _protected = new([@"C:\Windows", @"C:\Steam\", @"C:\Program Files\ConnectionManager"]);

    [Theory]
    [InlineData(@"C:\Windows\System32\svchost.exe")]
    [InlineData(@"c:\steam\steamapps\common\Game\game.exe")]
    [InlineData(@"C:\Program Files\ConnectionManager\ConnectionManager.exe")]
    [InlineData(@"D:\Anywhere\Steam.exe")]
    [InlineData(@"C:\Users\me\AppData\Local\Discord\app-1.0\Discord.exe")]
    [InlineData(@"C:\Program Files (x86)\EasyAntiCheat\EasyAntiCheat.exe")]
    public void Never_blocks_windows_steam_voice_chat_anti_cheat_or_itself(string path) => Assert.True(_protected.IsProtected(path));

    [Fact]
    public void Other_programs_can_be_blocked() =>
        Assert.False(_protected.IsProtected(@"C:\Program Files (x86)\Google\GoogleUpdater\1.0\updater.exe"));

    [Fact]
    public void A_folder_name_prefix_is_not_a_match() => Assert.False(_protected.IsProtected(@"C:\Windows2\tool.exe"));

    [Fact]
    public void Blank_or_relative_folders_are_ignored() =>
        Assert.False(new ProtectedPaths(["", null, "relative"]).IsProtected(@"C:\Tools\x.exe"));
}
