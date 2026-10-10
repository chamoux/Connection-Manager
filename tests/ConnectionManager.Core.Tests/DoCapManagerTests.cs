using ConnectionManager.Core.Caps;
using Microsoft.Win32;

namespace ConnectionManager.Core.Tests;

public sealed class DoCapManagerTests : IDisposable
{
    private const string Key = DoCapManager.PolicyKey;
    private const string Background = "DOMaxBackgroundDownloadBandwidth";
    private const string Foreground = "DOMaxForegroundDownloadBandwidth";

    private readonly TempTree _tree = new();
    private readonly FakeRegistryStore _registry = new();
    private readonly ListLog _log = new();
    private int _refreshes;

    private string JournalPath => Path.Combine(_tree.Root, "journal.json");
    private PolicyJournal Journal => new(_tree.Root, _log);

    /// A fresh manager is what the service has after a restart: nothing in memory, only the journal.
    private DoCapManager NewManager() => new(_registry, Journal, _log, () => _refreshes++);

    private static RegistryValue Dword(uint value) => new(RegistryValueKind.DWord, value);

    private static RegistryEntry Entry(string name, uint written, string key = Key, bool keyExisted = false) =>
        new(key, name, keyExisted, PriorExists: false, PriorKind: null, PriorData: null, WrittenData: written);

    [Fact]
    public void Apply_creates_the_missing_key_and_both_values_and_restore_removes_them()
    {
        var caps = NewManager();

        var applied = caps.Apply(200);

        Assert.True(applied.Applied);
        Assert.Null(applied.Problem);
        Assert.Empty(applied.Skipped);
        Assert.True(caps.Active);
        Assert.True(caps.HasJournal);
        Assert.Equal(Dword(200), _registry.Read(Key, Background));
        Assert.Equal(Dword(200), _registry.Read(Key, Foreground));
        Assert.Equal(1, _refreshes);

        var restored = caps.Restore();

        Assert.True(restored.Complete);
        Assert.Null(restored.Problem);
        Assert.False(_registry.KeyExists(Key));
        Assert.False(File.Exists(JournalPath));
        Assert.False(caps.Active);
        Assert.False(caps.HasJournal);
        Assert.Equal(2, _refreshes);
    }

    [Fact]
    public void A_key_that_already_existed_is_kept_with_its_other_values()
    {
        _registry.Set(Key, "DODownloadMode", Dword(1));
        var caps = NewManager();

        caps.Apply(200);
        caps.Restore();

        Assert.True(_registry.KeyExists(Key));
        Assert.Equal(Dword(1), _registry.Read(Key, "DODownloadMode"));
        Assert.Null(_registry.Read(Key, Background));
        Assert.Null(_registry.Read(Key, Foreground));
    }

    [Fact]
    public void A_foreign_value_is_skipped_logged_once_and_left_untouched()
    {
        _registry.Set(Key, Background, Dword(5000)); // set by a GPO or MDM
        var caps = NewManager();

        var first = caps.Apply(200);
        caps.Restore();
        caps.Apply(300);
        var restored = caps.Restore();

        Assert.True(first.Applied); // the foreground value is still ours to cap
        Assert.Equal([Background], first.Skipped);
        Assert.Null(restored.Problem);
        Assert.Equal(Dword(5000), _registry.Read(Key, Background));
        Assert.Null(_registry.Read(Key, Foreground));
        Assert.True(_registry.KeyExists(Key));
        Assert.Single(_log.Lines, l => l.Contains(Background) && l.Contains("already set"));
    }

    [Fact]
    public void When_both_values_are_foreign_nothing_is_written()
    {
        _registry.Set(Key, Background, Dword(5000));
        _registry.Set(Key, Foreground, Dword(6000));
        var caps = NewManager();

        var applied = caps.Apply(200);

        Assert.False(applied.Applied);
        Assert.Null(applied.Problem);
        Assert.Equal([Background, Foreground], applied.Skipped);
        Assert.False(caps.Active);
        Assert.False(File.Exists(JournalPath));
        Assert.Equal(0, _refreshes);
        Assert.Equal(Dword(5000), _registry.Read(Key, Background));
        Assert.Equal(Dword(6000), _registry.Read(Key, Foreground));
    }

    [Theory]
    [InlineData(false)] // the power went before the key was created
    [InlineData(true)]  // or after the key, before the values
    public void A_crash_after_the_journal_write_restores_cleanly(bool keyCreated)
    {
        Journal.Save([Entry(Background, 200), Entry(Foreground, 200)]);
        if (keyCreated) _registry.CreateKey(Key);

        var restored = NewManager().Restore();

        Assert.True(restored.Complete);
        Assert.Null(restored.Problem);
        Assert.Empty(_registry.Keys);
        Assert.False(File.Exists(JournalPath));
    }

    [Fact]
    public void A_value_changed_by_someone_else_is_left_and_reported()
    {
        var caps = NewManager();
        caps.Apply(200);
        _registry.WriteDword(Key, Foreground, 999);

        var restored = caps.Restore();

        Assert.True(restored.Complete);
        Assert.Equal([Foreground], restored.LeftAlone);
        Assert.Contains(Foreground, restored.Problem);
        Assert.Equal(Dword(999), _registry.Read(Key, Foreground));
        Assert.Null(_registry.Read(Key, Background));
        Assert.True(_registry.KeyExists(Key)); // not empty, so not deleted
        Assert.False(File.Exists(JournalPath));
    }

    [Fact]
    public void Apply_with_a_journal_left_over_restores_it_first()
    {
        NewManager().Apply(100); // a previous run capped at 100 and died without restoring
        var caps = NewManager();

        var applied = caps.Apply(300);

        Assert.True(applied.Applied);
        Assert.Equal(Dword(300), _registry.Read(Key, Background));
        Assert.Equal(Dword(300), _registry.Read(Key, Foreground));
        var entries = Journal.Load().Entries.Cast<RegistryEntry>().ToList();
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Equal(300u, e.WrittenData));
        Assert.All(entries, e => Assert.False(e.KeyExisted)); // still known to be our key

        caps.Restore();

        Assert.Empty(_registry.Keys);
    }

    [Fact]
    public void A_tampered_journal_entry_naming_another_value_is_ignored()
    {
        const string Run = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        _registry.Set(Run, "Updater", Dword(7));
        _registry.Set(Key, "DOMaxCacheSize", Dword(7));
        Journal.Save([Entry("Updater", 7, Run), Entry("DOMaxCacheSize", 7)]);

        var restored = NewManager().Restore();

        Assert.True(restored.Complete);
        Assert.Equal(Dword(7), _registry.Read(Run, "Updater"));
        Assert.Equal(Dword(7), _registry.Read(Key, "DOMaxCacheSize"));
        Assert.Equal(2, _log.Lines.Count(l => l.Contains("ignored")));
        Assert.False(File.Exists(JournalPath));
    }

    [Fact]
    public void A_corrupt_journal_is_set_aside_and_nothing_is_touched()
    {
        _registry.Set(Key, Background, Dword(200));
        File.WriteAllText(JournalPath, "{ not json");

        var restored = NewManager().Restore();

        Assert.NotNull(restored.Problem);
        Assert.True(File.Exists(JournalPath + ".bad"));
        Assert.False(File.Exists(JournalPath));
        Assert.Equal(Dword(200), _registry.Read(Key, Background));
        Assert.Equal(0, _refreshes);
        Assert.Contains(_log.Lines, l => l.StartsWith("WARN") && l.Contains("journal.json.bad"));
    }

    [Fact]
    public void Changing_the_cap_ends_the_same_as_a_single_apply()
    {
        var caps = NewManager();

        caps.Apply(200);
        var second = caps.Apply(500);

        Assert.True(second.Applied);
        Assert.Null(second.Problem);
        Assert.Equal(Dword(500), _registry.Read(Key, Background));
        Assert.Equal(Dword(500), _registry.Read(Key, Foreground));
        Assert.Equal([Entry(Background, 500), Entry(Foreground, 500)], Journal.Load().Entries);

        caps.Restore();

        Assert.Empty(_registry.Keys);
        Assert.False(File.Exists(JournalPath));
    }

    [Theory]
    [InlineData(0)] // 0 means unlimited to Delivery Optimization
    [InlineData(-5)]
    public void A_cap_below_one_is_refused(int kbps)
    {
        var caps = NewManager();

        var applied = caps.Apply(kbps);

        Assert.False(applied.Applied);
        Assert.NotNull(applied.Problem);
        Assert.Empty(_registry.Keys);
        Assert.False(File.Exists(JournalPath));
    }

    [Fact]
    public void The_values_written_are_dwords_of_at_least_one()
    {
        NewManager().Apply(1);

        Assert.Equal(new RegistryValue(RegistryValueKind.DWord, 1u), _registry.Read(Key, Background));
        Assert.Equal(new RegistryValue(RegistryValueKind.DWord, 1u), _registry.Read(Key, Foreground));
    }

    [Fact]
    public void A_failed_write_is_rolled_back_and_reported_without_throwing()
    {
        _registry.FailWritesAfter = 1;
        var caps = NewManager();

        var applied = caps.Apply(200);

        Assert.False(applied.Applied);
        Assert.NotNull(applied.Problem);
        Assert.False(caps.Active);
        Assert.Empty(_registry.Keys);
        Assert.False(File.Exists(JournalPath));
    }

    [Fact]
    public void A_failed_restore_keeps_the_journal_for_the_next_try()
    {
        var caps = NewManager();
        caps.Apply(200);
        _registry.FailDeletes = true;

        var first = caps.Restore();

        Assert.False(first.Complete);
        Assert.NotNull(first.Problem);
        Assert.True(caps.Active);
        Assert.True(caps.HasJournal);

        _registry.FailDeletes = false;
        var second = caps.Restore();

        Assert.True(second.Complete);
        Assert.Null(second.Problem);
        Assert.Empty(_registry.Keys);
        Assert.False(File.Exists(JournalPath));
    }

    [Fact]
    public void Apply_writes_nothing_while_the_old_cap_cannot_be_undone()
    {
        NewManager().Apply(100);
        _registry.FailDeletes = true;

        var applied = NewManager().Apply(300);

        Assert.False(applied.Applied);
        Assert.NotNull(applied.Problem);
        Assert.Equal(Dword(100), _registry.Read(Key, Background));
        Assert.Equal(Dword(100), _registry.Read(Key, Foreground));
        Assert.All(Journal.Load().Entries.Cast<RegistryEntry>(), e => Assert.Equal(100u, e.WrittenData));
    }

    [Fact]
    public void A_failing_policy_refresh_is_logged_not_thrown()
    {
        var caps = new DoCapManager(_registry, Journal, _log, () => throw new InvalidOperationException("no refresh"));

        var applied = caps.Apply(200);
        var restored = caps.Restore();

        Assert.True(applied.Applied); // the values are in place; Delivery Optimization reads them on its next policy check
        Assert.True(restored.Complete);
        Assert.Empty(_registry.Keys);
        Assert.Contains(_log.Lines, l => l.Contains("no refresh"));
    }

    [Fact]
    public void Journal_entries_of_other_features_survive_apply_and_restore()
    {
        Journal.Save([new WlanCostEntry("Phone hotspot")]);
        var caps = NewManager();

        Assert.False(caps.HasJournal);
        caps.Apply(200);
        caps.Restore();

        Assert.Equal([new WlanCostEntry("Phone hotspot")], Journal.Load().Entries);
    }

    [Fact]
    public void Restore_without_a_journal_does_nothing()
    {
        var restored = NewManager().Restore();

        Assert.True(restored.Complete);
        Assert.Null(restored.Problem);
        Assert.Equal(0, _refreshes);
    }

    public void Dispose() => _tree.Dispose();
}
