using ConnectionManager.Core.Engine;

namespace ConnectionManager.Core.Tests;

public class RestoreHooksTests
{
    private readonly List<string> _ran = [];

    private RestoreHooks Hooks(params (string Name, string? Problem, bool UserChoice)[] hooks) =>
        new([.. hooks.Select(h => new Restorable(h.Name, () => { _ran.Add(h.Name); return h.Problem; }, h.UserChoice))], new ListLog());

    [Fact]
    public void Start_and_stop_leave_user_choices_in_place()
    {
        var hooks = Hooks(("caps", null, false), ("metered", null, true));

        Assert.True(hooks.RunAll("at start", includeUserChoices: false));

        Assert.Equal(["caps"], _ran);
    }

    [Fact]
    public void Restore_everything_includes_user_choices()
    {
        var hooks = Hooks(("caps", null, false), ("metered", null, true));

        Assert.True(hooks.RunAll("on request", includeUserChoices: true));

        Assert.Equal(["caps", "metered"], _ran);
    }

    [Fact]
    public void A_failing_hook_does_not_stop_the_others()
    {
        var hooks = Hooks(("caps", "Could not restore caps", false), ("metered", null, true));

        Assert.False(hooks.RunAll("on request", includeUserChoices: true));

        Assert.Equal(["caps", "metered"], _ran);
        Assert.Equal(["Could not restore caps"], hooks.Problems);
    }
}
