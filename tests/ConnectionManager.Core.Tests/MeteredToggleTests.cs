using ConnectionManager.Core.Caps;
using ConnectionManager.Core.Network;
using ConnectionManager.Core.Wifi;
using static ConnectionManager.Core.Tests.FakeWlan;

namespace ConnectionManager.Core.Tests;

public sealed class MeteredToggleTests : IDisposable
{
    private const string Home = "Home WiFi";

    private readonly TempTree _tree = new();
    private readonly FakeWlan _wlan = new();
    private readonly ListLog _log = new();
    private readonly List<string> _runs = [];
    private int _exitCode;
    private Func<string, int>? _runner;

    public MeteredToggleTests()
    {
        _wlan.Adapters.Add(new WlanInterface(A, true));
        _wlan.Profiles[A] = ["Office", Home];
    }

    private PolicyJournal Journal => new(_tree.Root, _log);

    private MeteredToggle NewToggle() => new(_wlan, Journal, _log, _runner ?? (args =>
    {
        _runs.Add(args);
        return _exitCode;
    }));

    private static NetworkSnapshot Wifi(string? name = Home, NetworkCost cost = NetworkCost.Unrestricted, Guid? adapter = null) =>
        new(NetworkKind.Wifi, adapter ?? A, name, ["192.168.1.1"], cost);

    private static string Args(string profile, string cost) => $"wlan set profileparameter name=\"{profile}\" cost={cost}";

    public void Dispose() => _tree.Dispose();

    [Fact]
    public void Mark_journals_the_profile_before_running_netsh_with_the_exact_arguments()
    {
        var journalBefore = new List<JournalEntry>();
        _runner = args =>
        {
            journalBefore.AddRange(Journal.Load().Entries);
            _runs.Add(args);
            return 0;
        };
        var toggle = NewToggle();

        var result = toggle.Mark(Wifi());

        Assert.True(result.Ok);
        Assert.Null(result.Problem);
        Assert.Equal([Args(Home, "Fixed")], _runs);
        Assert.Equal([new WlanCostEntry(Home)], journalBefore);
        Assert.Equal([new WlanCostEntry(Home)], Journal.Load().Entries);
        Assert.True(toggle.IsMarkedByUs(Home));
        Assert.False(_wlan.IsOpen); // the profile lookup closes its handle
    }

    [Theory]
    [InlineData(NetworkCost.Fixed)]
    [InlineData(NetworkCost.Variable)]
    [InlineData(NetworkCost.Unknown)]
    public void Mark_is_refused_unless_the_network_is_unrestricted(NetworkCost cost)
    {
        var result = NewToggle().Mark(Wifi(cost: cost));

        Assert.False(result.Ok);
        Assert.NotNull(result.Problem);
        Assert.Empty(_runs);
        Assert.False(File.Exists(Journal.FilePath));
    }

    [Fact]
    public void Mark_is_refused_off_wifi_and_without_a_matching_profile()
    {
        var toggle = NewToggle();

        Assert.False(toggle.Mark(Wifi() with { Kind = NetworkKind.Ethernet }).Ok);
        Assert.False(toggle.Mark(Wifi("Cafe")).Ok);
        Assert.False(toggle.Mark(Wifi(null)).Ok);
        Assert.Empty(_runs);
        Assert.False(File.Exists(Journal.FilePath));
    }

    [Fact]
    public void A_failed_netsh_removes_the_journal_entry()
    {
        _exitCode = 1;
        var toggle = NewToggle();

        var result = toggle.Mark(Wifi());

        Assert.False(result.Ok);
        Assert.NotNull(result.Problem);
        Assert.Single(_runs);
        Assert.False(File.Exists(Journal.FilePath));
        Assert.False(toggle.IsMarkedByUs(Home));
    }

    [Fact]
    public void A_netsh_that_cannot_start_removes_the_journal_entry_and_keeps_other_entries()
    {
        var registry = new RegistryEntry(DoCapManager.PolicyKey, "DOMaxBackgroundDownloadBandwidth", true, false, null, null, 200);
        Journal.Save([registry]);
        _runner = _ => throw new System.ComponentModel.Win32Exception(2);

        var result = NewToggle().Mark(Wifi());

        Assert.False(result.Ok);
        Assert.Equal([registry], Journal.Load().Entries);
    }

    [Fact]
    public void Mark_is_refused_when_the_journal_cannot_be_read()
    {
        File.WriteAllText(Journal.FilePath, "{}");
        using (new FileStream(Journal.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = NewToggle().Mark(Wifi());

            Assert.False(result.Ok);
        }
        Assert.Empty(_runs);
    }

    [Fact]
    public void Unmark_runs_cost_default_and_removes_the_entry()
    {
        var toggle = NewToggle();
        toggle.Mark(Wifi());
        _runs.Clear();

        var result = toggle.Unmark(Home);

        Assert.True(result.Ok);
        Assert.Equal([Args(Home, "Default")], _runs);
        Assert.False(File.Exists(Journal.FilePath));
        Assert.False(toggle.IsMarkedByUs(Home));
    }

    [Fact]
    public void Unmark_leaves_a_network_it_did_not_mark_alone()
    {
        var result = NewToggle().Unmark(Home);

        Assert.False(result.Ok);
        Assert.NotNull(result.Problem);
        Assert.Empty(_runs);
    }

    [Fact]
    public void A_failed_unmark_keeps_the_entry()
    {
        var toggle = NewToggle();
        toggle.Mark(Wifi());
        _exitCode = 1;

        var result = toggle.Unmark(Home);

        Assert.False(result.Ok);
        Assert.True(toggle.IsMarkedByUs(Home));
    }

    [Fact]
    public void RestoreAll_unmarks_every_profile_and_keeps_the_registry_entries()
    {
        var registry = new RegistryEntry(DoCapManager.PolicyKey, "DOMaxForegroundDownloadBandwidth", false, false, null, null, 300);
        Journal.Save([registry, new WlanCostEntry(Home), new WlanCostEntry("Office")]);

        var result = NewToggle().RestoreAll();

        Assert.True(result.Ok);
        Assert.Equal([Args(Home, "Default"), Args("Office", "Default")], _runs);
        Assert.Equal([registry], Journal.Load().Entries);
    }

    [Fact]
    public void RestoreAll_keeps_the_entries_that_fail()
    {
        Journal.Save([new WlanCostEntry(Home), new WlanCostEntry("Office")]);
        _runner = args =>
        {
            _runs.Add(args);
            return args.Contains("Office", StringComparison.Ordinal) ? 1 : 0;
        };

        var result = NewToggle().RestoreAll();

        Assert.False(result.Ok);
        Assert.Contains("Office", result.Problem);
        Assert.Equal([new WlanCostEntry("Office")], Journal.Load().Entries);
    }

    [Fact]
    public void RestoreAll_drops_a_profile_that_no_longer_exists_on_any_adapter()
    {
        _wlan.Profiles[A] = ["Office"]; // the user forgot Home WiFi since it was marked
        Journal.Save([new WlanCostEntry(Home)]);
        _exitCode = 1;

        var result = NewToggle().RestoreAll();

        Assert.True(result.Ok);
        Assert.False(File.Exists(Journal.FilePath));
    }

    [Fact]
    public void RestoreAll_keeps_a_profile_when_no_adapter_is_present_to_check()
    {
        _wlan.Adapters.Clear(); // a USB adapter that is unplugged right now
        Journal.Save([new WlanCostEntry(Home)]);
        _exitCode = 1;

        var result = NewToggle().RestoreAll();

        Assert.False(result.Ok);
        Assert.Equal([new WlanCostEntry(Home)], Journal.Load().Entries);
    }

    [Fact]
    public void RestoreAll_never_passes_an_unsafe_journaled_name_to_netsh()
    {
        Journal.Save([new WlanCostEntry("x\" interface=\"y"), new WlanCostEntry(Home)]);

        var result = NewToggle().RestoreAll();

        Assert.True(result.Ok);
        Assert.Equal([Args(Home, "Default")], _runs);
        Assert.False(File.Exists(Journal.FilePath));
    }

    [Fact]
    public void RestoreAll_with_nothing_journaled_runs_nothing()
    {
        var result = NewToggle().RestoreAll();

        Assert.True(result.Ok);
        Assert.Empty(_runs);
    }

    [Fact]
    public void CanMark_needs_wifi_and_a_profile_with_the_network_name_on_a_connected_interface()
    {
        _wlan.Adapters.Add(new WlanInterface(B, false));
        _wlan.Profiles[B] = ["Cafe"];
        var toggle = NewToggle();

        Assert.True(toggle.CanMark(Wifi()));
        Assert.True(toggle.CanMark(Wifi(cost: NetworkCost.Fixed))); // already metered by us: the toggle stays usable
        Assert.False(toggle.CanMark(Wifi("home wifi"))); // profile names are matched exactly
        Assert.False(toggle.CanMark(Wifi("Cafe"))); // only on a disconnected interface
        Assert.False(toggle.CanMark(Wifi(null)));
        Assert.False(toggle.CanMark(Wifi() with { Kind = NetworkKind.Ethernet }));
        Assert.False(toggle.CanMark(NetworkSnapshot.None));
    }

    [Fact]
    public void CanMark_reads_the_profiles_once_per_network()
    {
        var toggle = NewToggle();

        toggle.CanMark(Wifi());
        toggle.CanMark(Wifi());
        Assert.Single(_wlan.Calls, c => c.StartsWith("profiles", StringComparison.Ordinal));

        toggle.CanMark(Wifi("Office"));
        Assert.Equal(2, _wlan.Calls.Count(c => c.StartsWith("profiles", StringComparison.Ordinal)));
    }

    [Fact]
    public void CanMark_is_false_and_retried_when_wlan_cannot_open()
    {
        _wlan.OpenCode = WlanNative.ERROR_SERVICE_NOT_ACTIVE;
        var toggle = NewToggle();

        Assert.False(toggle.CanMark(Wifi()));
        _wlan.OpenCode = 0;
        Assert.True(toggle.CanMark(Wifi()));
    }

    [Fact]
    public void IsMarkedByUs_reads_the_journal()
    {
        Journal.Save([new WlanCostEntry(Home)]);
        var toggle = NewToggle();

        Assert.True(toggle.IsMarkedByUs(Home));
        Assert.False(toggle.IsMarkedByUs("Office"));
        Assert.False(toggle.IsMarkedByUs(null));
    }

    [Theory]
    [InlineData("Home WiFi", true)]
    [InlineData("Café ☕ 5G", true)]
    [InlineData("a=b c", true)]
    [InlineData("", false)]
    [InlineData("say \"hi\"", false)]
    [InlineData("tab\there", false)]
    [InlineData("new\nline", false)]
    [InlineData("nul\0", false)]
    [InlineData("c1\u0085", false)]
    public void Profile_names_are_validated(string name, bool valid) =>
        Assert.Equal(valid, MeteredToggle.IsValidProfileName(name));

    [Fact]
    public void Profile_names_longer_than_256_characters_are_rejected()
    {
        Assert.True(MeteredToggle.IsValidProfileName(new string('x', 256)));
        Assert.False(MeteredToggle.IsValidProfileName(new string('x', 257)));
        Assert.False(MeteredToggle.IsValidProfileName(null));
    }

    [Fact]
    public void Arguments_quote_the_name_and_double_trailing_backslashes()
    {
        Assert.Equal("wlan set profileparameter name=\"Home WiFi\" cost=Fixed", MeteredToggle.BuildArguments("Home WiFi", metered: true));
        Assert.Equal("wlan set profileparameter name=\"Home WiFi\" cost=Default", MeteredToggle.BuildArguments("Home WiFi", metered: false));
        // netsh splits its command line with the C runtime's rules, where \" is a literal quote: a name ending in a
        // backslash would otherwise swallow the closing quote and the cost.
        Assert.Equal(@"wlan set profileparameter name=""a\b\\"" cost=Fixed", MeteredToggle.BuildArguments(@"a\b\", metered: true));
        Assert.Throws<ArgumentException>(() => MeteredToggle.BuildArguments("x\" cost=Default", metered: true));
    }

    [Fact]
    public void An_invalid_profile_found_over_wlan_is_never_marked()
    {
        _wlan.Profiles[A] = ["bad\"name"];

        var toggle = NewToggle();

        Assert.False(toggle.CanMark(Wifi("bad\"name")));
        Assert.False(toggle.Mark(Wifi("bad\"name")).Ok);
        Assert.Empty(_runs);
    }

    [Fact]
    public void The_real_runner_starts_netsh_from_system32_and_returns_its_exit_code()
    {
        Assert.Equal(0, MeteredToggle.RunNetsh("wlan show interfaces", _log)); // read-only
    }
}
