using ConnectionManager.Core.Steam;

namespace ConnectionManager.Core.Tests;

public class SteamLibrariesTests
{
    private const string NewFormat = """
        "libraryfolders"
        {
            "0" { "path" "C:\\Program Files (x86)\\Steam" }
            "1" { "path" "D:\\SteamLibrary" "apps" { "570" "123" } }
        }
        """;

    private const string OldFormat = """
        "LibraryFolders"
        {
            "TimeNextStatsReport" "1234"
            "1" "E:\\Games\\Steam"
        }
        """;

    [Fact]
    public void Reads_the_new_format() =>
        Assert.Equal(new[] { @"C:\Program Files (x86)\Steam", @"D:\SteamLibrary" }, SteamLibraries.ParseLibraryRoots(NewFormat));

    [Fact]
    public void Reads_the_old_format() =>
        Assert.Equal(new[] { @"E:\Games\Steam" }, SteamLibraries.ParseLibraryRoots(OldFormat));

    [Fact]
    public void Lists_each_common_folder_once_with_steams_own_first()
    {
        var folders = SteamLibraries.FindCommonFolders("c:/program files (x86)/steam",
            path => path.EndsWith(@"steamapps\libraryfolders.vdf", StringComparison.OrdinalIgnoreCase) ? NewFormat : null);

        Assert.Equal(new[] { @"c:\program files (x86)\steam\steamapps\common", @"D:\SteamLibrary\steamapps\common" }, folders);
    }

    [Fact]
    public void A_damaged_file_still_leaves_steams_own_library() =>
        Assert.Equal(new[] { @"C:\Steam\steamapps\common" }, SteamLibraries.FindCommonFolders(@"C:\Steam", _ => "\"libraryfolders\" {"));

    [Fact]
    public void No_steam_means_no_libraries() => Assert.Empty(SteamLibraries.FindCommonFolders(null, _ => null));

    [Fact]
    public void Falls_back_to_the_config_copy_when_the_main_file_is_damaged()
    {
        var folders = SteamLibraries.FindCommonFolders(@"C:\Steam", path =>
            path.EndsWith(@"steamapps\libraryfolders.vdf", StringComparison.OrdinalIgnoreCase) ? "\"libraryfolders\" {"
            : path.EndsWith(@"config\libraryfolders.vdf", StringComparison.OrdinalIgnoreCase) ? NewFormat
            : null);

        Assert.Contains(@"D:\SteamLibrary\steamapps\common", folders);
    }

    [Fact]
    public void An_empty_main_file_falls_back_to_the_config_copy()
    {
        var folders = SteamLibraries.FindCommonFolders(@"C:\Steam", path =>
            path.EndsWith(@"steamapps\libraryfolders.vdf", StringComparison.OrdinalIgnoreCase) ? ""
            : path.EndsWith(@"config\libraryfolders.vdf", StringComparison.OrdinalIgnoreCase) ? NewFormat
            : null);

        Assert.Contains(@"D:\SteamLibrary\steamapps\common", folders);
    }

    [Fact]
    public void A_bad_entry_is_skipped_and_the_rest_are_kept()
    {
        const string withBadEntry = """
            "libraryfolders"
            {
                "1" { "path" "D:\\A" }
                "2" { "path" "" }
                "3" { "path" "E:\\B" }
            }
            """;

        var folders = SteamLibraries.FindCommonFolders(@"C:\Steam", _ => withBadEntry);

        Assert.Equal(new[] { @"C:\Steam\steamapps\common", @"D:\A\steamapps\common", @"E:\B\steamapps\common" }, folders);
    }

    [Fact]
    public void The_service_takes_the_machine_install_path_first()
    {
        using var tree = new TempTree();
        var machine = Directory.CreateDirectory(Path.Combine(tree.Root, "Machine")).FullName;
        var user = Directory.CreateDirectory(Path.Combine(tree.Root, "User")).FullName;

        Assert.Equal(machine, SteamLibraries.ReadSteamPath(() => machine, () => user));
        Assert.Equal(user, SteamLibraries.ReadSteamPath(() => null, () => user.Replace('\\', '/'))); // Steam writes c:/...
    }

    [Theory]
    [InlineData(@"\\server\share\Steam")]
    [InlineData(@"\\?\C:\Steam")]
    [InlineData(@"Steam")]
    [InlineData(@"C:\Steam\..\Steam.")]
    public void An_unsafe_steam_path_from_the_registry_is_skipped(string unsafePath)
    {
        using var tree = new TempTree();

        Assert.Equal(tree.Root, SteamLibraries.ReadSteamPath(() => unsafePath, () => tree.Root));
        Assert.Null(SteamLibraries.ReadSteamPath(() => null, () => unsafePath));
    }

    [Fact]
    public void A_steam_path_through_a_junction_is_skipped()
    {
        using var tree = new TempTree();
        Directory.CreateDirectory(Path.Combine(tree.Root, "Real", "Steam"));
        var link = tree.Junction(Path.Combine(tree.Root, "Link"), Path.Combine(tree.Root, "Real"));

        Assert.Null(SteamLibraries.ReadSteamPath(() => Path.Combine(link, "Steam"), () => null));
    }

    [Fact]
    public void For_the_service_unsafe_library_roots_are_dropped()
    {
        using var tree = new TempTree();
        var steam = Directory.CreateDirectory(Path.Combine(tree.Root, "Steam")).FullName;
        var good = Directory.CreateDirectory(Path.Combine(tree.Root, "Lib")).FullName;
        Directory.CreateDirectory(Path.Combine(tree.Root, "Real"));
        var link = tree.Junction(Path.Combine(tree.Root, "Link"), Path.Combine(tree.Root, "Real"));
        var vdf = $$"""
            "libraryfolders"
            {
                "0" { "path" "{{Escape(steam)}}" }
                "1" { "path" "\\\\server\\share\\Lib" }
                "2" { "path" "{{Escape(link)}}" }
                "3" { "path" "{{Escape(good)}}" }
            }
            """;

        var folders = SteamLibraries.FindCommonFoldersSafely(steam, path => path.EndsWith(@"steamapps\libraryfolders.vdf", StringComparison.Ordinal) ? vdf : null);

        Assert.Equal(new[] { Path.Combine(steam, "steamapps", "common"), Path.Combine(good, "steamapps", "common") }, folders);
    }

    [Fact]
    public void For_the_service_an_unsafe_steam_folder_gives_no_libraries() =>
        Assert.Empty(SteamLibraries.FindCommonFoldersSafely(@"\\server\Steam", _ => NewFormat));

    [Fact]
    public void An_untrusted_file_is_read_only_when_safe_and_small()
    {
        using var tree = new TempTree();
        var small = tree.Touch("small.vdf");
        File.WriteAllText(small, NewFormat);
        var big = tree.Touch("big.vdf");
        File.WriteAllBytes(big, new byte[SteamLibraries.MaxFileBytes + 1]);

        Assert.Equal(NewFormat, SteamLibraries.ReadUntrustedFile(small));
        Assert.Null(SteamLibraries.ReadUntrustedFile(big));
        Assert.Null(SteamLibraries.ReadUntrustedFile(Path.Combine(tree.Root, "missing.vdf")));
        Assert.Null(SteamLibraries.ReadUntrustedFile(@"\\server\share\libraryfolders.vdf"));
    }

    private static string Escape(string path) => path.Replace(@"\", @"\\");
}
