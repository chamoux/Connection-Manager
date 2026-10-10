using System.Runtime.InteropServices;
using ConnectionManager.Core.Paths;

namespace ConnectionManager.Core.Tests;

public sealed class SafePathsTests : IDisposable
{
    private const string Device = @"\\?\";

    private readonly TempTree _tree = new();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"\\server\share\x.exe")] // the service would sign in to the server as the machine account
    [InlineData(@"//server/share/x.exe")]
    [InlineData(@"\/server\share\x.exe")]
    [InlineData(@"\\?\C:\x.exe")]
    [InlineData(@"\\?\UNC\server\share\x.exe")]
    [InlineData(@"\\.\pipe\x")]
    [InlineData(@"\\.\C:\x.exe")]
    [InlineData(@"\??\C:\x.exe")]
    [InlineData(@"C:x.exe")] // relative to the current folder on C:
    [InlineData(@"C:..\x.exe")]
    [InlineData(@"\Windows\x.exe")] // the current drive
    [InlineData(@"x.exe")]
    [InlineData(@"1:\x.exe")]
    [InlineData(@" C:\x.exe")]
    [InlineData(@"C:\a\b.exe:stream")]
    [InlineData(@"C:\a\b.exe::$DATA")]
    [InlineData(@"C:\a:b\c.exe")]
    [InlineData(@"C:\a\C:\b.exe")]
    public void Network_device_relative_and_stream_paths_are_rejected(string? path) => Assert.False(SafePaths.TryLocal(path, out _));

    [Theory]
    [InlineData(@"C:\a\b.exe.")] // Windows opens C:\a\b.exe
    [InlineData(@"C:\a\b.exe ")]
    [InlineData(@"C:\a\b.exe. .")]
    [InlineData(@"C:\a.\b.exe")]
    [InlineData(@"C:\a \b.exe")]
    [InlineData(@"C:\a\...\b.exe")]
    [InlineData(@"C:\a\ \b.exe")]
    [InlineData(@"C:\a\..\b.\")]
    public void Segments_ending_in_a_dot_or_space_are_rejected(string path) => Assert.False(SafePaths.TryLocal(path, out _));

    [Theory]
    [InlineData(@"C:\a\*.exe")]
    [InlineData(@"C:\a\b?.exe")]
    [InlineData(@"C:\a\<.exe")] // < > and " are wildcards to FindFirstFile
    [InlineData(@"C:\a\>.exe")]
    [InlineData("C:\\a\\\".exe")]
    [InlineData(@"C:\a\b|c.exe")]
    [InlineData(@"C:\*\b.exe")]
    public void Wildcards_and_reserved_characters_are_rejected(string path) => Assert.False(SafePaths.TryLocal(path, out _));

    [Fact]
    public void Control_characters_are_rejected()
    {
        foreach (var c in new[] { '\0', '\t', '\n', '\r', '\u001f', '\u007f', '\u0085' })
            Assert.False(SafePaths.TryLocal(_tree.Root + $@"\a{c}b.exe", out _), $"U+{(int)c:X4}");
    }

    [Theory]
    [InlineData(@"C:\a\NUL")] // GetFullPath turns this into \\.\NUL
    [InlineData(@"C:\a\nul.txt")]
    [InlineData(@"C:\a\CON")]
    [InlineData(@"C:\a\con .log")]
    [InlineData(@"C:\a\PRN.exe")]
    [InlineData(@"C:\AUX\b.exe")]
    [InlineData(@"C:\a\COM1")]
    [InlineData(@"C:\a\com9.exe")]
    [InlineData(@"C:\a\COM0")]
    [InlineData(@"C:\a\LPT1")]
    [InlineData(@"C:\a\lpt5.dll")]
    [InlineData(@"C:\a\COM¹")]
    [InlineData(@"C:\a\LPT³.txt")]
    [InlineData(@"C:\a\CONIN$")]
    [InlineData(@"C:\a\CONOUT$")]
    public void Device_names_are_rejected(string path) => Assert.False(SafePaths.TryLocal(path, out _));

    [Theory]
    [InlineData("CONSOLE.exe")]
    [InlineData("COM10")]
    [InlineData("NULL")]
    [InlineData("my.nul")]
    public void Names_that_only_look_like_devices_pass(string name) => Assert.True(SafePaths.TryLocal(Path.Combine(_tree.Root, name), out _));

    [Fact]
    public void Paths_over_1024_characters_are_rejected()
    {
        // Segments of 100, which NTFS allows, so only the total length counts.
        var tail = string.Concat(Enumerable.Repeat(@"\" + new string('a', 100), 10));
        var limit = SystemDrive + new string('m', SafePaths.MaxLength - SystemDrive.Length - tail.Length) + tail;

        Assert.True(SafePaths.TryLocal(limit, out _));
        Assert.False(SafePaths.TryLocal(limit + "a", out _));
        Assert.False(SafePaths.TryLocal(limit + @"\..\a", out _)); // shorter once normalised, but the input is too long
    }

    [Fact]
    public void A_drive_that_is_not_there_is_rejected()
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        var free = Enumerable.Range('D', 23).Select(c => (char)c).First(c => !used.Contains(c));

        Assert.False(SafePaths.TryLocal($@"{free}:\x.exe", out _));
    }

    [Fact]
    public void A_normal_file_in_temp_passes_and_passes_again()
    {
        var exe = _tree.Touch(@"Tools\app.exe");

        Assert.True(SafePaths.TryLocal(exe, out var full));
        Assert.Equal(exe, full);
        Assert.True(SafePaths.TryLocal(full, out var again));
        Assert.Equal(full, again);
    }

    [Fact]
    public void A_path_that_does_not_exist_yet_passes() // a block for a program that is installed later
    {
        var missing = Path.Combine(_tree.Root, @"Not\Yet\app.exe");

        Assert.True(SafePaths.TryLocal(missing, out var full));
        Assert.Equal(missing, full);
    }

    [Fact]
    public void The_drive_root_passes()
    {
        Assert.True(SafePaths.TryLocal(SystemDrive, out var full));
        Assert.Equal(SystemDrive, full);
    }

    [Fact]
    public void Mixed_separators_and_dot_segments_give_the_normalised_path()
    {
        var mixed = _tree.Root.Replace('\\', '/') + @"/a\..\b\.\\c.exe";

        Assert.True(SafePaths.TryLocal(mixed, out var full));
        Assert.Equal(Path.Combine(_tree.Root, @"b\c.exe"), full);
        Assert.True(SafePaths.TryLocal(_tree.Root + @"\b\\", out var folder));
        Assert.Equal(Path.Combine(_tree.Root, "b"), folder);
        Assert.True(SafePaths.TryLocal(SystemDrive + @"..\..\Windows", out var climbed)); // .. stops at the drive root
        Assert.Equal(SystemDrive + "Windows", climbed);
    }

    [Fact]
    public void Short_names_are_expanded() // so blocks, protection checks and WFP all see one name per program
    {
        var exe = _tree.Touch(@"Long Folder Name\app.exe");

        Assert.True(SafePaths.TryLocal(ShortPath(exe), out var full));
        Assert.Equal(exe, full);
    }

    [Fact]
    public void A_short_name_that_hides_a_trailing_dot_is_rejected()
    {
        // Only a \\?\ path can make a folder like this. Its short name (LONGEV~1) looks harmless until it is expanded.
        var folder = Path.Combine(_tree.Root, "Long Evil Name.");
        Directory.CreateDirectory(Device + folder);
        try
        {
            File.WriteAllText(Device + folder + @"\app.exe", "");
            var shortPath = ShortPath(Device + folder + @"\app.exe")[Device.Length..];

            Assert.False(SafePaths.TryLocal(shortPath, out _));
            Assert.False(SafePaths.TryLocal(Path.GetDirectoryName(shortPath), out _));
        }
        finally
        {
            Directory.Delete(Device + folder, recursive: true); // a plain path would name "Long Evil Name" instead
        }
    }

    [Fact]
    public void A_program_compressed_with_compact_exe_passes() // CompactGUI does this to Steam games
    {
        var exe = _tree.Touch(@"Game\game.exe");
        File.WriteAllBytes(exe, new byte[64 * 1024]);
        TempTree.RunSystemTool("compact.exe", "/c", "/exe:xpress4k", exe); // stored behind a reparse point, which Windows hides

        Assert.True(SafePaths.TryLocal(exe, out _));
    }

    [Fact]
    public void A_junction_anywhere_in_the_path_is_rejected()
    {
        _tree.Touch(@"Elsewhere\Sub\app.exe");
        var link = _tree.Junction(Path.Combine(_tree.Root, "Link"), Path.Combine(_tree.Root, "Elsewhere"));

        Assert.False(SafePaths.TryLocal(link, out _));
        Assert.False(SafePaths.TryLocal(Path.Combine(link, @"Sub\app.exe"), out _));
        Assert.False(SafePaths.TryLocal(Path.Combine(link, @"Missing\app.exe"), out _));
    }

    [Fact]
    public void Dot_dot_after_a_junction_gives_the_path_without_it()
    {
        var real = _tree.Touch(@"Elsewhere\app.exe");
        var link = _tree.Junction(Path.Combine(_tree.Root, "Link"), Path.Combine(_tree.Root, "Somewhere"));

        Assert.True(SafePaths.TryLocal(Path.Combine(link, @"..\Elsewhere\app.exe"), out var full));
        Assert.Equal(real, full); // the junction is never looked up: the service opens this string, not the input
    }

    [Fact]
    public void Only_existing_safe_folders_are_safe_to_scan()
    {
        var file = _tree.Touch(@"Games\game.exe");
        var link = _tree.Junction(Path.Combine(_tree.Root, "Link"), Path.Combine(_tree.Root, "Games"));

        Assert.True(SafePaths.IsSafeFolderToScan(Path.Combine(_tree.Root, "Games")));
        Assert.False(SafePaths.IsSafeFolderToScan(Path.Combine(_tree.Root, "Missing")));
        Assert.False(SafePaths.IsSafeFolderToScan(file));
        Assert.False(SafePaths.IsSafeFolderToScan(link));
        Assert.False(SafePaths.IsSafeFolderToScan(@"\\server\share"));
    }

    [Fact]
    public void Printable_replaces_control_characters_and_caps_the_length()
    {
        Assert.Equal(@"C:\a?b?.exe", SafePaths.Printable("C:\\a\nb\u0085.exe"));
        Assert.Equal("", SafePaths.Printable(null));
        Assert.Equal(SafePaths.MaxLength + 1, SafePaths.Printable(new string('a', 5000)).Length);
    }

    private static string SystemDrive => Path.GetPathRoot(Environment.SystemDirectory)!;

    // The 8.3 form, or the path unchanged when the volume does not make short names.
    private static string ShortPath(string path)
    {
        var buffer = new char[2048];
        var length = GetShortPathNameW(path, buffer, (uint)buffer.Length);
        Assert.InRange(length, 1u, (uint)buffer.Length - 1);
        return new string(buffer, 0, (int)length);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetShortPathNameW(string longPath, [Out] char[] shortPath, uint size);

    public void Dispose() => _tree.Dispose();
}
