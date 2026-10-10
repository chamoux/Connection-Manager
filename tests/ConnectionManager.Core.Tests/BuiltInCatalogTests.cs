using ConnectionManager.Core.Blocking;

namespace ConnectionManager.Core.Tests;

public class BuiltInCatalogTests
{
    [Fact]
    public void Ids_are_unique() =>
        Assert.Equal(BuiltInCatalog.Entries.Count, BuiltInCatalog.Entries.Select(e => e.Id).Distinct().Count());

    [Fact]
    public void Defaults_leave_only_the_nvidia_app_off()
    {
        var enabled = BuiltInCatalog.Enabled(new Dictionary<string, bool>()).Select(e => e.Id);

        Assert.Equal(["nvidia-app"], BuiltInCatalog.Entries.Select(e => e.Id).Except(enabled));
    }

    [Fact]
    public void Your_choices_override_the_defaults()
    {
        var enabled = BuiltInCatalog.Enabled(new Dictionary<string, bool> { ["nvidia-app"] = true, ["razer"] = false })
            .Select(e => e.Id).ToList();

        Assert.Contains("nvidia-app", enabled);
        Assert.DoesNotContain("razer", enabled);
    }

    [Fact]
    public void An_entry_is_on_by_default_until_you_choose()
    {
        var razer = BuiltInCatalog.Entries.Single(e => e.Id == "razer");
        var nvidia = BuiltInCatalog.Entries.Single(e => e.Id == "nvidia-app");

        Assert.True(BuiltInCatalog.IsOn(razer, new Dictionary<string, bool>()));
        Assert.False(BuiltInCatalog.IsOn(nvidia, new Dictionary<string, bool>()));
        Assert.False(BuiltInCatalog.IsOn(razer, new Dictionary<string, bool> { ["razer"] = false }));
        Assert.True(BuiltInCatalog.IsOn(nvidia, new Dictionary<string, bool> { ["nvidia-app"] = true }));
    }
}
