using ConnectionManager.Core.Wfp;

namespace ConnectionManager.Core.Tests;

public class FilterReconcilerTests
{
    private static readonly FilterRule Google = Block(RuleKind.App, @"C:\Google\updater.exe", "Google Updater");
    private static readonly FilterRule Edge = Block(RuleKind.App, @"C:\Edge\MicrosoftEdgeUpdate.exe", "Edge Update");
    private static readonly FilterRule Bits = Block(RuleKind.Service, "BITS", "Windows Update");

    private readonly FakeWfpEngine _engine = new();

    private static FilterRule Block(RuleKind kind, string value, string label) => new(kind, value, RuleAction.Block, RuleWeight.GameBlock, label);

    private static HashSet<FilterRule> Set(params FilterRule[] rules) => new(rules);

    [Fact]
    public void Adds_four_filters_per_wanted_rule_in_one_transaction()
    {
        var result = new FilterReconciler(_engine).Apply(Set(Google, Bits));

        Assert.Equal(2, result.Added);
        Assert.Equal(8, _engine.Filters.Count);
        Assert.Equal(1, _engine.Transactions);
    }

    [Fact]
    public void Applying_the_same_set_again_changes_nothing()
    {
        var reconciler = new FilterReconciler(_engine);
        reconciler.Apply(Set(Google));

        var result = reconciler.Apply(Set(Google));

        Assert.Equal(0, result.Added);
        Assert.Equal(0, result.Removed);
        Assert.Equal(4, _engine.Filters.Count);
    }

    [Fact]
    public void A_rule_no_longer_wanted_loses_its_filters()
    {
        var reconciler = new FilterReconciler(_engine);
        reconciler.Apply(Set(Google, Edge));

        var result = reconciler.Apply(Set(Edge));

        Assert.Equal(1, result.Removed);
        Assert.All(_engine.Filters.Values, t => Assert.Equal(Edge, t));
        Assert.Equal(new[] { Edge }, reconciler.Active);
    }

    [Fact]
    public void One_failing_rule_is_reported_and_the_rest_still_apply()
    {
        _engine.FailFor.Add(Bits.Value);
        var reconciler = new FilterReconciler(_engine);

        var result = reconciler.Apply(Set(Google, Bits));

        Assert.Equal(1, result.Added);
        Assert.Equal(Bits, Assert.Single(result.Failures).Rule);
        Assert.Equal(new[] { Google }, reconciler.Active);
    }

    [Fact]
    public void A_failed_commit_keeps_the_previous_state()
    {
        var reconciler = new FilterReconciler(_engine);
        reconciler.Apply(Set(Google));
        _engine.FailCommit = true;

        Assert.Throws<WfpException>(() => reconciler.Apply(Set(Edge)));

        Assert.Equal(new[] { Google }, reconciler.Active);
        Assert.All(_engine.Filters.Values, t => Assert.Equal(Google, t));
    }

    [Fact]
    public void Clear_removes_everything()
    {
        var reconciler = new FilterReconciler(_engine);
        reconciler.Apply(Set(Google, Edge, Bits));

        reconciler.Clear();

        Assert.Empty(_engine.Filters);
        Assert.Empty(reconciler.Active);
    }

    [Fact]
    public void An_unexpected_error_rolls_everything_back()
    {
        var reconciler = new FilterReconciler(_engine);
        reconciler.Apply(Set(Google));
        _engine.FailDelete = true;

        Assert.Throws<InvalidOperationException>(() => reconciler.Apply(Set(Edge)));

        Assert.Equal(new[] { Google }, reconciler.Active);
        Assert.Equal(4, _engine.Filters.Count);
        Assert.All(_engine.Filters.Values, t => Assert.Equal(Google, t));
    }

    [Fact]
    public void After_a_failed_commit_the_next_apply_starts_from_the_real_state()
    {
        var reconciler = new FilterReconciler(_engine);
        reconciler.Apply(Set(Google));
        _engine.FailCommit = true;
        Assert.Throws<WfpException>(() => reconciler.Apply(Set(Edge)));
        _engine.FailCommit = false;

        var result = reconciler.Apply(Set(Edge));

        Assert.Equal((1, 1), (result.Added, result.Removed));
        Assert.All(_engine.Filters.Values, t => Assert.Equal(Edge, t));
    }

    [Fact]
    public void A_service_that_is_not_installed_is_reported_and_skipped()
    {
        _engine.MissingServices.Add(Bits.Value);

        var result = new FilterReconciler(_engine).Apply(Set(Google, Bits));

        Assert.Equal(1, result.Added);
        Assert.Equal(Bits, Assert.Single(result.Failures).Rule);
    }

    [Fact]
    public void A_failed_rule_is_tried_again_next_time()
    {
        _engine.FailFor.Add(Google.Value);
        var reconciler = new FilterReconciler(_engine);
        reconciler.Apply(Set(Google));
        _engine.FailFor.Clear();

        var result = reconciler.Apply(Set(Google));

        Assert.Equal(1, result.Added);
        Assert.Equal(new[] { Google }, reconciler.Active);
    }

    [Fact]
    public void Nothing_to_change_opens_no_transaction()
    {
        var reconciler = new FilterReconciler(_engine);
        reconciler.Apply(Set(Google));

        reconciler.Apply(Set(Google));
        reconciler.Clear();
        reconciler.Clear();

        Assert.Equal(2, _engine.Transactions); // the first apply and the first clear
    }

    [Fact]
    public void Block_and_permit_for_the_same_app_are_different_rules()
    {
        var permit = Google with { Action = RuleAction.Permit, Weight = RuleWeight.SaverPermit };

        var result = new FilterReconciler(_engine).Apply(Set(Google, permit));

        Assert.Equal(2, result.Added);
        Assert.Equal(8, _engine.Filters.Count);
        Assert.Equal(Set(Google, permit), _engine.Rules.ToHashSet());
    }

    [Fact]
    public void A_new_label_is_not_a_change()
    {
        var reconciler = new FilterReconciler(_engine);
        reconciler.Apply(Set(Google));

        var result = reconciler.Apply(Set(Google with { Label = "renamed" }));

        Assert.Equal((0, 0), (result.Added, result.Removed));
        Assert.Equal(1, _engine.Transactions);
    }

    [Fact]
    public void A_new_weight_replaces_the_filters()
    {
        var reconciler = new FilterReconciler(_engine);
        reconciler.Apply(Set(Google));

        var result = reconciler.Apply(Set(Google with { Weight = RuleWeight.EssentialPermit }));

        Assert.Equal((1, 1), (result.Added, result.Removed));
        Assert.All(_engine.Filters.Values, r => Assert.Equal(RuleWeight.EssentialPermit, r.Weight));
    }
}
