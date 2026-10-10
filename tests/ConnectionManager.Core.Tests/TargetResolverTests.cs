using ConnectionManager.Core.Blocking;
using ConnectionManager.Core.Settings;

namespace ConnectionManager.Core.Tests;

public sealed class TargetResolverTests : IDisposable
{
    private readonly TempTree _tree = new();

    [Fact]
    public void Resolves_catalog_paths_folders_and_services()
    {
        var updater = _tree.Touch(@"Google\GoogleUpdater\1.0\updater.exe");
        var razer = _tree.Touch(@"Razer\Synapse3\Razer Synapse Service.exe");
        var entries = new[]
        {
            new CatalogEntry("google", "Google Updater", true, [@"%ROOT%\Google\GoogleUpdater\*\updater.exe"], [], []),
            new CatalogEntry("razer", "Razer", true, [], [@"%ROOT%\Razer"], []),
            new CatalogEntry("wu", "Windows Update", true, [], [], ["BITS"]),
        };

        var targets = new TargetResolver(_ => false, _tree.Expand).Resolve(entries, []);

        Assert.Equal(3, targets.Count);
        Assert.Contains(new BlockTarget(TargetKind.App, updater, ""), targets);
        Assert.Contains(new BlockTarget(TargetKind.App, razer, ""), targets);
        Assert.Contains(new BlockTarget(TargetKind.Service, "bits", ""), targets); // case-insensitive, label ignored
    }

    [Fact]
    public void Skips_protected_programs()
    {
        var steam = _tree.Touch(@"Steam\steam.exe");
        var entries = new[] { new CatalogEntry("x", "X", true, [@"%ROOT%\Steam\*.exe"], [], []) };

        Assert.Empty(new TargetResolver(p => p == steam, _tree.Expand).Resolve(entries, []));
    }

    [Fact]
    public void Your_own_blocks_skip_protected_programs()
    {
        var steam = _tree.Touch(@"Steam\steam.exe");
        Assert.Empty(new TargetResolver(p => p == steam, _tree.Expand).Resolve([], [new UserBlock(steam, false)]));
    }

    [Fact]
    public void Adds_your_own_programs_and_folders()
    {
        var single = _tree.Touch(@"Tools\updater.exe");
        var inFolder = _tree.Touch(@"Vendor\bin\helper.exe");
        var blocks = new[]
        {
            new UserBlock(single, false),
            new UserBlock(Path.Combine(_tree.Root, "Vendor"), true),
            new UserBlock(Path.Combine(_tree.Root, "gone.exe"), false),
        };

        var targets = new TargetResolver(_ => false, _tree.Expand).Resolve([], blocks);

        Assert.Equal(2, targets.Count);
        Assert.Contains(new BlockTarget(TargetKind.App, single, ""), targets);
        Assert.Contains(new BlockTarget(TargetKind.App, inFolder, ""), targets);
    }

    [Fact]
    public void Blank_and_relative_user_entries_are_ignored()
    {
        var exe = _tree.Touch(@"Tools\updater.exe");
        var blocks = new[]
        {
            new UserBlock("", false),
            new UserBlock(Path.GetRelativePath(Environment.CurrentDirectory, exe), false),
            new UserBlock(Path.GetRelativePath(Environment.CurrentDirectory, Path.GetDirectoryName(exe)!), true),
        };

        Assert.Empty(new TargetResolver(_ => false, _tree.Expand).Resolve([], blocks));
    }

    [Fact]
    public void The_same_program_found_twice_is_one_target()
    {
        _tree.Touch(@"Shared\tool.exe");
        var entries = new[]
        {
            new CatalogEntry("a", "A", true, [@"%ROOT%\Shared\tool.exe"], [], []),
            new CatalogEntry("b", "B", true, [], [@"%ROOT%\Shared"], []),
        };

        Assert.Single(new TargetResolver(_ => false, _tree.Expand).Resolve(entries, []));
    }

    [Fact]
    public void A_folder_over_the_limit_is_reported()
    {
        _tree.Touch(@"Big\a.exe");
        _tree.Touch(@"Big\b.exe");
        _tree.Touch(@"Big\c.exe");
        var resolver = new TargetResolver(_ => false, _tree.Expand, folderLimit: 2);

        var targets = resolver.Resolve([new CatalogEntry("big", "Big", true, [], [@"%ROOT%\Big"], [])], []);

        Assert.Equal(2, targets.Count);
        Assert.Equal(new[] { @"%ROOT%\Big" }, resolver.CappedFolders);
    }

    [Fact]
    public void A_folder_with_too_many_entries_is_reported()
    {
        for (var i = 0; i < 30; i++) Directory.CreateDirectory(Path.Combine(_tree.Root, $@"Big\empty{i:00}"));
        var resolver = new TargetResolver(_ => false, _tree.Expand, entryLimit: 20);

        resolver.Resolve([new CatalogEntry("big", "Big", true, [], [@"%ROOT%\Big"], [])], []);

        Assert.Equal(new[] { @"%ROOT%\Big" }, resolver.CappedFolders);
    }

    [Fact]
    public void All_folder_scans_together_stop_at_a_total()
    {
        var first = _tree.Touch(@"One\a.exe");
        for (var i = 0; i < 10; i++) Directory.CreateDirectory(Path.Combine(_tree.Root, $@"One\empty{i}"));
        _tree.Touch(@"Two\z.exe"); // listed after the folders beside it
        for (var i = 0; i < 10; i++) Directory.CreateDirectory(Path.Combine(_tree.Root, $@"Two\empty{i}"));
        var resolver = new TargetResolver(_ => false, _tree.Expand, entryLimit: 100, totalEntryLimit: 15);

        var targets = resolver.Resolve([new CatalogEntry("x", "X", true, [], [@"%ROOT%\One", @"%ROOT%\Two"], [])], []);

        Assert.Equal(new[] { first }, targets.Select(t => t.Value));
        Assert.Equal(new[] { @"%ROOT%\Two" }, resolver.CappedFolders);
    }

    [Fact]
    public void Drops_your_blocks_that_go_through_a_junction()
    {
        _tree.Touch(@"Real\tool.exe");
        var link = _tree.Junction(Path.Combine(_tree.Root, "Link"), Path.Combine(_tree.Root, "Real"));
        var blocks = new[] { new UserBlock(Path.Combine(link, "tool.exe"), false), new UserBlock(link, true) };
        var resolver = new TargetResolver(_ => false, _tree.Expand);

        Assert.Empty(resolver.Resolve([], blocks));
        Assert.Equal(new[] { Path.Combine(link, "tool.exe"), link }, resolver.Rejected);
    }

    [Fact]
    public void Drops_catalog_folders_behind_a_junction()
    {
        _tree.Touch(@"Real\Razer\a.exe");
        _tree.Junction(Path.Combine(_tree.Root, "Vendor"), Path.Combine(_tree.Root, "Real"));
        var resolver = new TargetResolver(_ => false, _tree.Expand);

        Assert.Empty(resolver.Resolve([new CatalogEntry("r", "R", true, [@"%ROOT%\Vendor\Razer\a.exe"], [@"%ROOT%\Vendor\Razer"], [])], []));
        Assert.Equal(new[] { Path.Combine(_tree.Root, @"Vendor\Razer") }, resolver.Rejected);
    }

    [Fact]
    public void Unsafe_paths_are_listed_printably_and_only_for_the_last_resolve()
    {
        var resolver = new TargetResolver(_ => false, _tree.Expand);
        UserBlock[] blocks = [new(@"\\server.invalid\share\x.exe", false), new(@"C:\a\b.exe:stream", false), new("C:\\a\nfake log line", true)];

        Assert.Empty(resolver.Resolve([], blocks));
        Assert.Equal(new[] { @"\\server.invalid\share\x.exe", @"C:\a\b.exe:stream", @"C:\a?fake log line" }, resolver.Rejected);
        resolver.Resolve([], []);
        Assert.Empty(resolver.Rejected);
    }

    [Fact]
    public void Programs_found_on_disk_must_pass_too()
    {
        // Older Windows opens the console for C:\...\con.exe, so SafePaths refuses it even where it is a plain file.
        var device = Path.Combine(_tree.Root, @"Vendor\con.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(device)!);
        File.WriteAllText(@"\\?\" + device, "");
        try
        {
            var resolver = new TargetResolver(_ => false, _tree.Expand);

            Assert.Empty(resolver.Resolve([new CatalogEntry("v", "V", true, [@"%ROOT%\Vendor\*.exe"], [@"%ROOT%\Vendor"], [])], []));
            Assert.Equal(new[] { device, device }, resolver.Rejected); // once from the pattern, once from the folder
        }
        finally
        {
            File.Delete(@"\\?\" + device);
        }
    }

    public void Dispose() => _tree.Dispose();
}
