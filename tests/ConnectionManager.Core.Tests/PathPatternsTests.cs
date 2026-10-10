using ConnectionManager.Core.Blocking;

namespace ConnectionManager.Core.Tests;

public sealed class PathPatternsTests : IDisposable
{
    private readonly TempTree _tree = new();

    [Fact]
    public void Expands_a_wildcard_version_folder()
    {
        var a = _tree.Touch(@"Google\GoogleUpdater\155.0.1\updater.exe");
        var b = _tree.Touch(@"Google\GoogleUpdater\156.0.8067.0\updater.exe");
        _tree.Touch(@"Google\GoogleUpdater\156.0.8067.0\other.exe");

        var found = PathPatterns.Expand(@"%ROOT%\Google\GoogleUpdater\*\updater.exe", _tree.Expand).Order().ToArray();

        Assert.Equal(new[] { a, b }, found);
    }

    [Fact]
    public void Expands_a_file_wildcard()
    {
        var exe = _tree.Touch(@"EdgeUpdate\MicrosoftEdgeUpdate.exe");
        _tree.Touch(@"EdgeUpdate\readme.txt");

        Assert.Equal(new[] { exe }, PathPatterns.Expand(@"%ROOT%\EdgeUpdate\*.exe", _tree.Expand).ToArray());
    }

    [Fact]
    public void Wildcards_also_find_hidden_folders_and_files()
    {
        var exe = _tree.Touch(@"Vendor\1.0\updater.exe");
        File.SetAttributes(exe, FileAttributes.Hidden);
        File.SetAttributes(Path.GetDirectoryName(exe)!, FileAttributes.Directory | FileAttributes.Hidden);

        Assert.Equal(new[] { exe }, PathPatterns.Expand(@"%ROOT%\Vendor\*\*.exe", _tree.Expand).ToArray());
    }

    [Fact]
    public void A_plain_path_must_exist()
    {
        var exe = _tree.Touch(@"Update\GoogleUpdate.exe");

        Assert.Equal(new[] { exe }, PathPatterns.Expand(@"%ROOT%\Update\GoogleUpdate.exe", _tree.Expand).ToArray());
        Assert.Empty(PathPatterns.Expand(@"%ROOT%\Update\Missing.exe", _tree.Expand));
    }

    [Fact]
    public void An_unset_variable_matches_nothing() =>
        Assert.Empty(PathPatterns.Expand(@"%NOT_SET_ANYWHERE%\x.exe", _tree.Expand));

    [Fact]
    public void Wildcards_skip_junctions()
    {
        var real = _tree.Touch(@"Vendor\Real\updater.exe");
        _tree.Junction(Path.Combine(_tree.Root, @"Vendor\Link"), Path.Combine(_tree.Root, @"Vendor\Real"));

        Assert.Equal(new[] { real }, PathPatterns.Expand(@"%ROOT%\Vendor\*\updater.exe", _tree.Expand).ToArray());
    }

    [Fact]
    public void Plain_folders_that_are_junctions_are_not_followed()
    {
        _tree.Touch(@"Real\1.0\updater.exe");
        _tree.Junction(Path.Combine(_tree.Root, "Vendor"), Path.Combine(_tree.Root, "Real"));

        Assert.Empty(PathPatterns.Expand(@"%ROOT%\Vendor\1.0\updater.exe", _tree.Expand));
        Assert.Empty(PathPatterns.Expand(@"%ROOT%\Vendor\*\updater.exe", _tree.Expand));
    }

    [Fact]
    public void Patterns_must_start_at_a_local_drive()
    {
        Assert.Empty(PathPatterns.Expand(@"\\server.invalid\share\*.exe"));
        Assert.Empty(PathPatterns.Expand(@"relative\*.exe"));
    }

    [Fact]
    public void Exes_are_not_listed_through_a_junction()
    {
        _tree.Touch(@"Real\Sub\a.exe");
        _tree.Touch(@"Real\Sub\Deeper\b.exe");
        _tree.Junction(Path.Combine(_tree.Root, "Vendor"), Path.Combine(_tree.Root, "Real"));
        _tree.Junction(Path.Combine(_tree.Root, @"Real\Sub\Loop"), Path.Combine(_tree.Root, "Real"));

        Assert.Empty(PathPatterns.ExesUnder(@"%ROOT%\Vendor", 10, _tree.Expand));
        Assert.Empty(PathPatterns.ExesUnder(@"%ROOT%\Vendor\Sub", 10, _tree.Expand));
        Assert.Equal(2, PathPatterns.ExesUnder(@"%ROOT%\Real", 10, _tree.Expand).Count); // the loop below is skipped too
    }

    [Fact]
    public void Lists_exes_under_a_folder_up_to_a_limit()
    {
        _tree.Touch(@"Razer\Synapse\a.exe");
        _tree.Touch(@"Razer\Central\deep\b.exe");
        _tree.Touch(@"Razer\Central\c.dll");

        Assert.Equal(2, PathPatterns.ExesUnder(@"%ROOT%\Razer", 10, _tree.Expand).Count);
        Assert.Single(PathPatterns.ExesUnder(@"%ROOT%\Razer", 1, _tree.Expand));
        Assert.Empty(PathPatterns.ExesUnder(@"%ROOT%\Missing", 10, _tree.Expand));
    }

    [Fact]
    public void A_folder_scan_stops_after_so_many_entries_whatever_they_are()
    {
        // A user can fill a folder with empty ones; the scan runs on the engine loop.
        for (var i = 0; i < 30; i++) Directory.CreateDirectory(Path.Combine(_tree.Root, $@"Big\empty{i:00}"));
        var late = _tree.Touch(@"Big\zz\late.exe"); // listed after the 30 folders beside its own

        var cut = PathPatterns.ExesUnder(@"%ROOT%\Big", 10, 20, out var visited, out var capped, _tree.Expand);
        var all = PathPatterns.ExesUnder(@"%ROOT%\Big", 10, 100, out var allVisited, out var allCapped, _tree.Expand);

        Assert.Equal((0, 20, true), (cut.Count, visited, capped));
        Assert.Equal(new[] { late }, all);
        Assert.Equal((32, false), (allVisited, allCapped)); // 31 folders and the exe
    }

    [Fact]
    public void A_folder_scan_that_finds_the_limit_of_programs_says_so()
    {
        _tree.Touch(@"Big\a.exe");
        _tree.Touch(@"Big\b.exe");

        Assert.Equal(2, PathPatterns.ExesUnder(@"%ROOT%\Big", 2, 100, out _, out var capped, _tree.Expand).Count);
        Assert.True(capped);
        PathPatterns.ExesUnder(@"%ROOT%\Big", 3, 100, out _, out var notCapped, _tree.Expand);
        Assert.False(notCapped);
    }

    [Fact]
    public void A_wildcard_lists_only_so_many_entries()
    {
        for (var i = 0; i < 30; i++) Directory.CreateDirectory(Path.Combine(_tree.Root, $@"Vendor\a{i:00}"));
        _tree.Touch(@"Vendor\zz\updater.exe");

        Assert.Empty(PathPatterns.Expand(@"%ROOT%\Vendor\*\updater.exe", _tree.Expand, maxEntries: 20));
        Assert.Single(PathPatterns.Expand(@"%ROOT%\Vendor\*\updater.exe", _tree.Expand, maxEntries: 100));
    }

    public void Dispose() => _tree.Dispose();
}
