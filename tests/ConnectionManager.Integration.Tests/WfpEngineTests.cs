using ConnectionManager.Core;
using ConnectionManager.Core.DataSaver;
using ConnectionManager.Core.Engine;
using ConnectionManager.Core.Logging;
using ConnectionManager.Core.Network;
using ConnectionManager.Core.Processes;
using ConnectionManager.Core.Settings;
using ConnectionManager.Core.Wfp;

namespace ConnectionManager.Integration.Tests;

/// Real WFP filters against a private copy of curl. Needs admin rights and a working internet connection.
public sealed class WfpEngineTests : IDisposable
{
    private readonly CurlCopy _curl = new();

    private FilterRule CurlBlock => new(RuleKind.App, _curl.ExePath, RuleAction.Block, RuleWeight.GameBlock, "test curl");

    private FilterRule CurlPermit => new(RuleKind.App, _curl.ExePath, RuleAction.Permit, RuleWeight.SaverPermit, "test curl");

    // Data Saver's real plan for this PC: block-all, the essential permits (DNS stays open, so a blocked connect shows as
    // curl's exit 7 rather than 6) and running security software. That last part matters here: Kaspersky on the dev VM
    // decrypts curl's HTTPS (the certificate curl sees is signed by Kaspersky's own CA) and fetches the page itself, so
    // with block-all and only curl allowed, Kaspersky's onward connection is blocked and curl stalls (exit 28).
    private FilterRule[] BlockAll => [.. new DataSaverPlanner(new NullLog()).Plan(new TickContext(DateTimeOffset.UtcNow, new AppSettings(),
        NetworkSnapshot.None, new SystemProcessSource().GetRunning(), ControllerStatus.Starting, SaverActive: true, SteamPath: null,
        SteamLibraries: [], IsProtected: _ => false, ServiceExe: null))];

    // An antivirus that fetches for curl may need a few tries before its own connection recovers from a block.
    private static readonly TimeSpan Recovery = TimeSpan.FromSeconds(30);

    private static void AddAll(WfpEngine engine, params FilterRule[] rules) =>
        engine.InTransaction(() => { foreach (var rule in rules) engine.Add(rule); });

    [ElevatedFact]
    public void Blocks_an_app_until_the_engine_is_disposed()
    {
        Assert.Equal(0, _curl.Fetch());
        using (var engine = new WfpEngine("cm-test"))
        {
            engine.InTransaction(() => engine.Add(CurlBlock));
            Assert.Equal(7, _curl.Fetch());
        }
        Assert.True(_curl.FetchesWithin(Recovery));
    }

    [ElevatedFact]
    public void Deleting_the_filters_unblocks_the_app()
    {
        using var engine = new WfpEngine("cm-test");
        IReadOnlyList<ulong> ids = [];
        engine.InTransaction(() => ids = engine.Add(CurlBlock));
        Assert.Equal(4, ids.Count);
        Assert.Equal(7, _curl.Fetch());

        engine.InTransaction(() => { foreach (var id in ids) engine.DeleteFilter(id); });

        Assert.True(_curl.FetchesWithin(Recovery));
    }

    [ElevatedFact]
    public void Loopback_stays_open_for_a_blocked_app()
    {
        using var server = new LoopbackServer();
        using var engine = new WfpEngine("cm-test");
        engine.InTransaction(() => engine.Add(CurlBlock));

        Assert.Equal(7, _curl.Fetch());
        Assert.Equal(0, _curl.Fetch(server.Url));
    }

    [ElevatedFact]
    public void Service_filters_can_be_added_and_removed()
    {
        using var engine = new WfpEngine("cm-test");
        IReadOnlyList<ulong> ids = [];

        engine.InTransaction(() => ids = engine.Add(new FilterRule(RuleKind.Service, "BITS", RuleAction.Block, RuleWeight.GameBlock, "test service")));
        Assert.Equal(4, ids.Count);

        engine.InTransaction(() => { foreach (var id in ids) engine.DeleteFilter(id); });
    }

    [ElevatedFact]
    public void Port_filters_with_a_protocol_can_be_added_and_removed()
    {
        using var engine = new WfpEngine("cm-test");
        IReadOnlyList<ulong> ids = [];

        engine.InTransaction(() => ids = engine.Add(new FilterRule(RuleKind.LocalPort, "68/udp", RuleAction.Permit, RuleWeight.EssentialPermit, "test DHCP")));
        Assert.Equal(4, ids.Count);

        engine.InTransaction(() => { foreach (var id in ids) engine.DeleteFilter(id); });
    }

    [ElevatedFact]
    public void A_missing_program_fails_cleanly()
    {
        using var engine = new WfpEngine("cm-test");
        Assert.Throws<WfpException>(() => engine.Add(new FilterRule(RuleKind.App, @"C:\does\not\exist.exe", RuleAction.Block, RuleWeight.GameBlock, "missing")));
    }

    [ElevatedFact]
    public void Block_all_cuts_off_an_app()
    {
        Assert.Equal(0, _curl.Fetch());
        using var engine = new WfpEngine("cm-test");

        Saver(new FilterReconciler(engine));

        Assert.Equal(7, _curl.Fetch());
    }

    [ElevatedFact]
    public void An_app_permit_beats_block_all()
    {
        using var engine = new WfpEngine("cm-test");
        var filters = new FilterReconciler(engine);
        Saver(filters);
        Assert.Equal(7, _curl.Fetch());

        Saver(filters, CurlPermit);

        Assert.True(_curl.FetchesWithin(Recovery));
    }

    [ElevatedFact]
    public void A_game_block_beats_an_app_permit()
    {
        using var engine = new WfpEngine("cm-test");
        var filters = new FilterReconciler(engine);
        Saver(filters, CurlPermit);
        Assert.True(_curl.FetchesWithin(Recovery));

        Saver(filters, CurlPermit, CurlBlock);

        Assert.Equal(7, _curl.Fetch());
    }

    // Through the reconciler, as the service applies them: one program that can't be resolved (it exited, say) is
    // skipped instead of failing the rest. The test's own curl rules must never be among those skipped.
    private void Saver(FilterReconciler filters, params FilterRule[] extra)
    {
        var result = filters.Apply(new HashSet<FilterRule>([.. BlockAll, .. extra]));
        Assert.DoesNotContain(result.Failures, f => f.Rule.Value == _curl.ExePath);
        Assert.DoesNotContain(result.Failures, f => f.Rule.Kind == RuleKind.All);
    }

    public void Dispose() => _curl.Dispose();

    private sealed class NullLog : ILog
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? ex = null) { }
    }
}
