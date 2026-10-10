using ConnectionManager.Core.Caps;

namespace ConnectionManager.Core.Tests;

/// The allowlist is checked before the registry is opened, so these need no admin rights.
public sealed class HklmRegistryStoreTests
{
    private const string Run = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string Background = "DOMaxBackgroundDownloadBandwidth";

    private readonly HklmRegistryStore _store = new();

    [Fact]
    public void Values_off_the_allowlist_are_refused()
    {
        Assert.Throws<ArgumentException>(() => _store.Read(Run, "Updater"));
        Assert.Throws<ArgumentException>(() => _store.WriteDword(Run, Background, 1));
        Assert.Throws<ArgumentException>(() => _store.WriteDword(DoCapManager.PolicyKey, "DOMaxCacheSize", 1));
        Assert.Throws<ArgumentException>(() => _store.DeleteValue(DoCapManager.PolicyKey, "DODownloadMode"));
    }

    [Theory]
    [InlineData(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run")]
    [InlineData(@"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization\Sub")]
    [InlineData(@"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization\")]
    [InlineData(@"SOFTWARE\Policies\Microsoft\Windows")]
    [InlineData(@"SOFTWARE\Policies\Microsoft\Windows\..\Windows\DeliveryOptimization")]
    public void Keys_off_the_allowlist_are_refused(string key)
    {
        Assert.Throws<ArgumentException>(() => _store.KeyExists(key));
        Assert.Throws<ArgumentException>(() => _store.CreateKey(key));
        Assert.Throws<ArgumentException>(() => _store.DeleteKeyIfEmpty(key));
        Assert.Throws<ArgumentException>(() => _store.Read(key, Background));
    }

    [Fact]
    public void An_allowlisted_value_can_be_read_without_admin_rights()
    {
        var value = _store.Read(DoCapManager.PolicyKey, Background); // usually absent; a policy may have set it

        Assert.True(value is null || value.Data is not null);
        _ = _store.KeyExists(DoCapManager.PolicyKey);
    }
}
