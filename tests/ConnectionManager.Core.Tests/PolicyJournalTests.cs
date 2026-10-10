using System.Text.Json;
using ConnectionManager.Core.Caps;

namespace ConnectionManager.Core.Tests;

public sealed class PolicyJournalTests : IDisposable
{
    private const string Key = DoCapManager.PolicyKey;

    private readonly TempTree _tree = new();
    private readonly ListLog _log = new();

    private string FilePath => Path.Combine(_tree.Root, "journal.json");
    private PolicyJournal Journal => new(_tree.Root, _log);

    private static RegistryEntry Entry(string name = "DOMaxBackgroundDownloadBandwidth", uint written = 200) =>
        new(Key, name, KeyExisted: false, PriorExists: false, PriorKind: null, PriorData: null, WrittenData: written);

    [Fact]
    public void A_missing_file_is_an_empty_journal()
    {
        var load = Journal.Load();

        Assert.Empty(load.Entries);
        Assert.Null(load.Problem);
        Assert.False(load.Unreadable);
    }

    [Fact]
    public void Saves_and_loads_both_kinds_of_entry()
    {
        var prior = JsonDocument.Parse("42").RootElement;
        var withPrior = new RegistryEntry(Key, "DOMaxForegroundDownloadBandwidth", KeyExisted: true, PriorExists: true, "DWord", prior, 300);
        Journal.Save([Entry(), withPrior, new WlanCostEntry("Phone hotspot")]);

        var entries = Journal.Load().Entries;

        Assert.Equal(3, entries.Count);
        Assert.Equal(Entry(), entries[0]);
        var loaded = Assert.IsType<RegistryEntry>(entries[1]);
        Assert.Equal(42, loaded.PriorData!.Value.GetInt32());
        Assert.Equal("DWord", loaded.PriorKind);
        Assert.Equal(new WlanCostEntry("Phone hotspot"), entries[2]);
        var text = File.ReadAllText(FilePath);
        Assert.Contains("\"kind\": \"registry\"", text);
        Assert.Contains("\"kind\": \"wlanCost\"", text);
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public void Saving_no_entries_deletes_the_file()
    {
        Journal.Save([Entry()]);

        Journal.Save([]);

        Assert.False(File.Exists(FilePath));
        Assert.False(Journal.Exists);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{ "v": 1, "entries": [ { "kind": "script", "path": "C:\\x.cmd" } ] }""")]                    // unknown kind
    [InlineData("""{ "v": 1, "entries": [ { "profile": "x" } ] }""")]                                           // no kind
    [InlineData("""{ "v": 1, "entries": [ null ] }""")]
    [InlineData("""{ "v": 2, "entries": [] }""")]                                                               // a newer format
    [InlineData("""{ "v": 1, "entries": [ { "kind": "wlanCost", "profile": "x", "extra": 1 } ] }""")]           // unknown field
    [InlineData("""{ "v": 1, "entries": [ { "kind": "wlanCost", "profile": "" } ] }""")]
    [InlineData("""{ "v": 1, "entries": [ { "kind": "registry", "key": "k", "name": "n", "keyExisted": false, "priorExists": false, "priorKind": null, "priorData": null, "writtenData": 0 } ] }""")]
    [InlineData("""{ "v": 1, "entries": [ { "kind": "registry", "key": "k", "keyExisted": false, "priorExists": false, "priorKind": null, "priorData": null, "writtenData": 5 } ] }""")]
    [InlineData("""{ "v": 1, "entries": [ { "kind": "registry", "key": "k", "name": "n", "keyExisted": false, "priorExists": true, "priorKind": "Nonsense", "priorData": 1, "writtenData": 5 } ] }""")]
    [InlineData("""{ "v": 1, "entries": [ { "kind": "registry", "key": "k", "name": "n", "keyExisted": false, "priorExists": true, "priorKind": "DWord", "priorData": null, "writtenData": 5 } ] }""")]
    public void A_corrupt_journal_is_renamed_to_bad_and_reported(string json)
    {
        File.WriteAllText(FilePath, json);

        var load = Journal.Load();

        Assert.Empty(load.Entries);
        Assert.NotNull(load.Problem);
        Assert.False(load.Unreadable);
        Assert.False(File.Exists(FilePath));
        Assert.Equal(json, File.ReadAllText(FilePath + ".bad"));
        Assert.Contains(_log.Lines, l => l.StartsWith("WARN") && l.Contains("journal.json.bad"));
    }

    [Fact]
    public void A_journal_over_64_KB_is_corrupt()
    {
        File.WriteAllText(FilePath, """{ "v": 1, "entries": [] }""" + new string(' ', PolicyJournal.MaxBytes));

        var load = Journal.Load();

        Assert.NotNull(load.Problem);
        Assert.True(File.Exists(FilePath + ".bad"));
    }

    [Fact]
    public void A_journal_that_would_pass_64_KB_is_not_saved()
    {
        Journal.Save([Entry()]);
        var many = Enumerable.Range(0, 300).Select(i => (JournalEntry)new WlanCostEntry(new string('x', 250) + i)).ToList();

        Assert.Throws<InvalidOperationException>(() => Journal.Save(many));

        Assert.Equal([Entry()], Journal.Load().Entries); // the old journal is untouched
    }

    [Fact]
    public void A_locked_journal_is_unreadable_and_left_in_place()
    {
        Journal.Save([Entry()]);
        using var locked = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None);

        var load = Journal.Load();

        Assert.True(load.Unreadable);
        Assert.NotNull(load.Problem);
        Assert.True(File.Exists(FilePath));
        Assert.False(File.Exists(FilePath + ".bad"));
    }

    public void Dispose() => _tree.Dispose();
}
