using ConnectionManager.Core.Wfp;

namespace ConnectionManager.Core.Tests;

public class FilterRuleTests
{
    private static FilterRule App(string path, RuleAction action = RuleAction.Block, RuleWeight weight = RuleWeight.GameBlock, string label = "x") =>
        new(RuleKind.App, path, action, weight, label);

    [Fact]
    public void Equality_ignores_the_label_and_the_case_of_the_value()
    {
        Assert.Equal(App(@"C:\Tools\Updater.exe", label: "one"), App(@"c:\tools\updater.EXE", label: "two"));
        Assert.Equal(App(@"C:\a.exe").GetHashCode(), App(@"c:\A.EXE", label: "other").GetHashCode());
    }

    [Fact]
    public void Kind_action_and_weight_all_count()
    {
        Assert.NotEqual(App(@"C:\a.exe"), App(@"C:\a.exe", RuleAction.Permit));
        Assert.NotEqual(App(@"C:\a.exe"), App(@"C:\a.exe", weight: RuleWeight.SaverPermit));
        Assert.NotEqual(new FilterRule(RuleKind.RemotePort, "53", RuleAction.Permit, RuleWeight.EssentialPermit, ""),
            new FilterRule(RuleKind.LocalPort, "53", RuleAction.Permit, RuleWeight.EssentialPermit, ""));
    }

    [Fact]
    public void Weights_put_game_blocks_above_saver_permits_and_essentials_above_both()
    {
        Assert.True(RuleWeight.SaverBlockAll < RuleWeight.SaverPermit);
        Assert.True(RuleWeight.SaverPermit < RuleWeight.GameBlock);
        Assert.True(RuleWeight.GameBlock < RuleWeight.EssentialPermit);
        Assert.True((byte)RuleWeight.EssentialPermit < 15); // the engine's loopback permit stays on top
    }

    [Theory]
    [InlineData("53", 53, 0)]
    [InlineData("68/udp", 68, 17)]
    [InlineData("546/UDP", 546, 17)]
    [InlineData("443/tcp", 443, 6)]
    [InlineData("65535", 65535, 0)]
    public void Ports_parse_with_an_optional_protocol(string value, int port, int protocol)
    {
        Assert.True(FilterRule.TryParsePort(value, out var p, out var proto));
        Assert.Equal((port, protocol), (p, proto));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("-1")]
    [InlineData("+53")]
    [InlineData(" 53")]
    [InlineData("053")]
    [InlineData("53/")]
    [InlineData("/udp")]
    [InlineData("53/icmp")]
    [InlineData("53/udp/tcp")]
    [InlineData("dns")]
    public void Malformed_ports_are_rejected(string value) => Assert.False(FilterRule.TryParsePort(value, out _, out _));
}
