using ConnectionManager.Core.Ipc;
using ConnectionManager.Core.Qos;

namespace ConnectionManager.Core.Tests;

public sealed class UploadCapManagerTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly FakeQosStore _store = new();
    private readonly ListLog _log = new();

    private UploadCapManager NewManager() => new(_store, _log);

    private string App(string relative) => _tree.Touch(relative);

    private static string N(string path) => UploadCapManager.PolicyName(path);

    private List<(string Path, ulong Bits)> Ours => _store.Policies.Values
        .Where(p => QosPolicy.IsOurs(p.Name))
        .OrderBy(p => p.AppPath, StringComparer.OrdinalIgnoreCase)
        .Select(p => (p.AppPath, p.BitsPerSecond))
        .ToList();

    private IEnumerable<string> Warnings => _log.Lines.Where(l => !l.StartsWith("INFO ", StringComparison.Ordinal));

    [Fact]
    public void An_empty_store_gets_one_policy_per_cap_at_8000_bits_per_KBps()
    {
        var a = App("a.exe");
        var b = App(@"sub\b.exe");

        var problem = NewManager().Apply([new UploadCapArgs(a, 100), new UploadCapArgs(b, 8)], enabled: true);

        Assert.Null(problem);
        Assert.Equal([(a, 800_000UL), (b, 64_000UL)], Ours);
        Assert.All(_store.Policies.Values, p => Assert.Equal(N(p.AppPath), p.Name));
        Assert.Empty(Warnings);
    }

    [Fact]
    public void Names_are_the_prefix_and_a_stable_hash_that_ignores_case()
    {
        var name = N(@"C:\Games\X\game.exe");

        Assert.Matches("^connectionmanager-[0-9a-f]{16}$", name);
        Assert.Equal(name, N(@"c:\games\x\GAME.EXE"));
        Assert.NotEqual(name, N(@"C:\Games\X\other.exe"));
    }

    [Fact]
    public void Policies_whose_names_Windows_lowercased_are_still_ours()
    {
        _store.LowercasesNames = true;
        var caps = new[] { new UploadCapArgs(App("a.exe"), 100) };
        var manager = NewManager();

        Assert.Null(manager.Apply(caps, true));
        _store.Calls.Clear();
        Assert.Null(manager.Apply(caps, true));

        Assert.Equal(["list"], _store.Calls); // recognised: no duplicate create, no delete and re-create
        Assert.Null(manager.RemoveAll());
        Assert.Empty(_store.Policies);
    }

    [Fact]
    public void Applying_the_same_caps_again_only_lists()
    {
        var caps = new[] { new UploadCapArgs(App("a.exe"), 100), new UploadCapArgs(App("b.exe"), 200) };
        var manager = NewManager();
        manager.Apply(caps, true);
        _store.Calls.Clear();

        Assert.Null(manager.Apply(caps, true));
        Assert.Null(NewManager().Apply(caps.Reverse().ToArray(), true)); // a restarted service, another order

        Assert.Equal(["list", "list"], _store.Calls);
    }

    [Fact]
    public void A_changed_rate_recreates_only_that_policy()
    {
        var a = App("a.exe");
        var b = App("b.exe");
        var manager = NewManager();
        manager.Apply([new UploadCapArgs(a, 100), new UploadCapArgs(b, 200)], true);
        _store.Calls.Clear();

        var problem = manager.Apply([new UploadCapArgs(a, 100), new UploadCapArgs(b, 300)], true);

        Assert.Null(problem);
        Assert.Equal(["list", $"delete {N(b)}", $"create {N(b)}"], _store.Calls);
        Assert.Equal([(a, 800_000UL), (b, 2_400_000UL)], Ours);
    }

    [Fact]
    public void A_removed_cap_is_deleted()
    {
        var a = App("a.exe");
        var b = App("b.exe");
        var manager = NewManager();
        manager.Apply([new UploadCapArgs(a, 100), new UploadCapArgs(b, 200)], true);
        _store.Calls.Clear();

        Assert.Null(manager.Apply([new UploadCapArgs(a, 100)], true));

        Assert.Equal(["list", $"delete {N(b)}"], _store.Calls);
        Assert.Equal([(a, 800_000UL)], Ours);
    }

    [Fact]
    public void A_leftover_policy_of_ours_is_deleted_by_the_next_apply()
    {
        _store.Seed(QosPolicy.Prefix + "1"); // a crashed run with another naming
        var a = App("a.exe");

        Assert.Null(NewManager().Apply([new UploadCapArgs(a, 100)], true));

        Assert.Equal([(a, 800_000UL)], Ours);
    }

    [Fact]
    public void Disabled_removes_every_policy_of_ours()
    {
        var caps = new[] { new UploadCapArgs(App("a.exe"), 100), new UploadCapArgs(App("b.exe"), 200) };
        var manager = NewManager();
        manager.Apply(caps, true);

        Assert.Null(manager.Apply(caps, enabled: false));

        Assert.Empty(_store.Policies);
    }

    [Fact]
    public void RemoveAll_deletes_ours_and_nothing_else()
    {
        _store.Seed(QosPolicy.Prefix + "OLD");
        _store.Seed("Teams upload");

        Assert.Null(NewManager().RemoveAll());

        Assert.Equal(["Teams upload"], _store.Policies.Keys);
    }

    [Fact]
    public void Foreign_policies_are_never_touched()
    {
        string[] foreign = ["Teams upload", "ConnectionManager", "connectionmanagerx-1", "ConnectionManager-"];
        foreach (var name in foreign) _store.Seed(name);
        var before = _store.Policies.Values.ToList();
        var manager = NewManager();

        manager.Apply([new UploadCapArgs(App("a.exe"), 100)], true);
        manager.Apply([], true);
        manager.Apply([new UploadCapArgs(App("b.exe"), 100)], false);
        manager.RemoveAll();

        Assert.Equal(before, _store.Policies.Values);
        Assert.DoesNotContain(_store.Calls, c => foreign.Any(f => c == $"delete {f}" || c == $"create {f}"));
    }

    [Fact]
    public void More_than_50_caps_are_cut_to_the_first_50_and_the_cut_is_reported()
    {
        var caps = Enumerable.Range(0, 55).Select(i => new UploadCapArgs(App($"app{i:D2}.exe"), 100)).ToList();

        var problem = NewManager().Apply(caps, true);

        Assert.Equal(caps.Take(50).Select(c => c.Path).Order(StringComparer.OrdinalIgnoreCase), Ours.Select(o => o.Path));
        Assert.NotNull(problem);
        Assert.Contains("first 50", problem);
        Assert.Contains("5 more", problem);
    }

    [Fact]
    public void A_bad_path_is_skipped_reported_and_logged_once()
    {
        var a = App("a.exe");
        var junction = _tree.Junction(Path.Combine(_tree.Root, "link"), Path.GetDirectoryName(a)!);
        UploadCapArgs[] caps =
        [
            new(@"\\server\share\x.exe", 100),
            new("C:relative.exe", 100),
            new(Path.Combine(junction, "a.exe"), 100),
            new(Path.Combine(_tree.Root, "%TEMP%", "x.exe"), 100), // QoS would expand the variable to some other path
            new(null!, 100),
            new(a, 100),
        ];
        var manager = NewManager();

        var problem = manager.Apply(caps, true);
        manager.Apply(caps, true);

        Assert.Equal([(a, 800_000UL)], Ours);
        Assert.NotNull(problem);
        Assert.Contains("5 upload caps", problem);
        Assert.Equal(5, Warnings.Count());
    }

    [Theory]
    [InlineData(7)]
    [InlineData(0)]
    [InlineData(-100)]
    [InlineData(1_000_001)]
    public void A_rate_outside_8_to_1000000_KBps_is_skipped_and_reported(int kbps)
    {
        var a = App("a.exe");

        var problem = NewManager().Apply([new UploadCapArgs(App("bad.exe"), kbps), new UploadCapArgs(a, 100)], true);

        Assert.Equal([(a, 800_000UL)], Ours);
        Assert.NotNull(problem);
        Assert.Contains("8 to 1,000,000 KB/s", problem);
    }

    [Fact]
    public void The_largest_rate_fits()
    {
        var a = App("a.exe");

        Assert.Null(NewManager().Apply([new UploadCapArgs(a, 1_000_000)], true));

        Assert.Equal([(a, 8_000_000_000UL)], Ours);
    }

    [Fact]
    public void A_path_listed_twice_keeps_its_first_rate()
    {
        var a = App("a.exe");

        Assert.Null(NewManager().Apply([new UploadCapArgs(a, 100), new UploadCapArgs(a.ToUpperInvariant(), 500)], true));

        Assert.Equal([(a, 800_000UL)], Ours);
    }

    [Fact]
    public void A_list_failure_becomes_a_problem_logged_once_and_nothing_is_changed()
    {
        _store.ListFailure = new QosException("Windows refused access to its QoS policies.");
        var manager = NewManager();

        var problem = manager.Apply([new UploadCapArgs(App("a.exe"), 100)], true);
        manager.Apply([new UploadCapArgs(App("b.exe"), 100)], true);
        var removeProblem = manager.RemoveAll();

        Assert.NotNull(problem);
        Assert.Contains("Windows refused access to its QoS policies.", problem);
        Assert.NotNull(removeProblem);
        Assert.Empty(_store.Policies);
        Assert.All(_store.Calls, c => Assert.Equal("list", c));
        Assert.Single(Warnings, l => l.Contains("refused access", StringComparison.Ordinal));
    }

    [Fact]
    public void A_problem_that_clears_is_logged_again_when_it_comes_back()
    {
        var manager = NewManager();
        _store.ListFailure = new QosException("Windows QoS failed: busy.");
        manager.Apply([], true);
        _store.ListFailure = null;
        manager.Apply([], true);
        _store.ListFailure = new QosException("Windows QoS failed: busy.");

        manager.Apply([], true);

        Assert.Equal(2, Warnings.Count());
    }

    [Fact]
    public void A_create_failure_becomes_a_problem_and_the_other_caps_still_apply()
    {
        var a = App("a.exe");
        var b = App("b.exe");
        _store.FailCreateFor.Add(a);

        var problem = NewManager().Apply([new UploadCapArgs(a, 100), new UploadCapArgs(b, 100)], true);

        Assert.Equal([(b, 800_000UL)], Ours);
        Assert.NotNull(problem);
        Assert.Contains("create refused", problem);
    }

    [Fact]
    public void A_delete_failure_is_reported_and_its_replacement_is_not_attempted()
    {
        var a = App("a.exe");
        var manager = NewManager();
        manager.Apply([new UploadCapArgs(a, 100)], true);
        _store.FailDeleteFor.Add(N(a));
        _store.Calls.Clear();

        var problem = manager.Apply([new UploadCapArgs(a, 200)], true);

        Assert.Equal(["list", $"delete {N(a)}"], _store.Calls);
        Assert.Equal([(a, 800_000UL)], Ours);
        Assert.NotNull(problem);
        Assert.Contains("delete refused", problem);
    }

    [Fact]
    public void An_unexpected_store_exception_never_reaches_the_caller()
    {
        _store.ListFailure = new InvalidOperationException("boom");
        var manager = NewManager();

        var problem = manager.Apply([new UploadCapArgs(App("a.exe"), 100)], true);

        Assert.NotNull(problem);
        Assert.NotNull(manager.RemoveAll());
    }

    public void Dispose() => _tree.Dispose();
}
