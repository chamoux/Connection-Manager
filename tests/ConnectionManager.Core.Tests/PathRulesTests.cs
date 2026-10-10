namespace ConnectionManager.Core.Tests;

public class PathRulesTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("game.exe")]
    [InlineData(@"relative\folder\game.exe")]
    [InlineData(null)]
    public void Blank_and_relative_paths_are_rejected(string? path) => Assert.False(PathRules.TryFull(path, out _));

    [Fact]
    public void Forward_slashes_are_normalised()
    {
        Assert.True(PathRules.TryFull("c:/steam/x.exe", out var full));
        Assert.Equal(@"c:\steam\x.exe", full);
    }

    [Theory]
    [InlineData(@"C:\a\.\b\..\c.exe", @"C:\a\c.exe")]
    [InlineData(@"C:\a\..\..\b", @"C:\b")]
    [InlineData(@"C:\a\\b\", @"C:\a\b")]
    [InlineData(@"\\server\share\a\..\b.exe", @"\\server\share\b.exe")]
    [InlineData(@"//server/share/../x.exe", @"\\server\share\x.exe")] // .. stops at the share, like GetFullPath
    public void Dot_segments_and_separators_are_normalised(string path, string expected)
    {
        Assert.True(PathRules.TryFull(path, out var full));
        Assert.Equal(expected, full);
    }

    [Theory]
    [InlineData(@"\\?\C:\x.exe")]
    [InlineData(@"\\.\pipe\x")]
    [InlineData(@"\??\C:\x.exe")]
    [InlineData(@"\\server")]
    [InlineData(@"\\server\\x.exe")]
    [InlineData("C:\\a\0b.exe")]
    public void Device_incomplete_and_null_paths_are_rejected(string path) => Assert.False(PathRules.TryFull(path, out _));

    [Fact]
    public void A_short_name_is_not_expanded() // that would look it up on disk; SafePaths expands names it has checked
    {
        Assert.True(PathRules.TryFull(@"C:\PROGRA~1\x.exe", out var full));
        Assert.Equal(@"C:\PROGRA~1\x.exe", full);
    }

    [Fact]
    public void A_network_path_is_never_looked_up()
    {
        // 192.0.2.1 is reserved for documentation: a lookup (GetFullPath expands a '~' name) would wait about 21 s for it.
        var watch = System.Diagnostics.Stopwatch.StartNew();

        Assert.True(PathRules.TryFull(@"\\192.0.2.1\share\GAME~1\game.exe", out var full));
        Assert.Equal(@"\\192.0.2.1\share\GAME~1\game.exe", full);
        Assert.Equal(@"\\192.0.2.1\share\GAME~1\", PathRules.FolderPrefix(@"\\192.0.2.1\share\GAME~1"));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
    }

    [Fact]
    public void Folder_prefixes_end_in_one_slash_and_keep_drive_roots()
    {
        Assert.Equal(@"C:\Steam\", PathRules.FolderPrefix(@"C:\Steam\"));
        Assert.Equal(@"C:\Steam\", PathRules.FolderPrefix(@"C:\Steam"));
        Assert.Equal(@"D:\", PathRules.FolderPrefix("D:/"));
        Assert.Null(PathRules.FolderPrefix(""));
    }

    [Fact]
    public void A_folder_name_prefix_is_not_inside_the_folder()
    {
        var steam = PathRules.FolderPrefix(@"C:\Steam")!;
        Assert.True(PathRules.IsUnder(@"c:\steam\steam.exe", steam));
        Assert.False(PathRules.IsUnder(@"C:\SteamTools\x.exe", steam));
    }
}
