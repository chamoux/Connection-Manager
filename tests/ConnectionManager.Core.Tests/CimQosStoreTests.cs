using ConnectionManager.Core.Qos;

namespace ConnectionManager.Core.Tests;

/// The guards run before any call to Windows, so they need no admin rights. The real round trip is in the
/// integration tests.
public sealed class CimQosStoreTests
{
    private readonly CimQosStore _store = new();

    [Theory]
    [InlineData("Someone else")]
    [InlineData("ConnectionManager-")]
    [InlineData("connectionmanagerx-1")]
    public void Refuses_to_create_or_delete_a_policy_that_is_not_ours(string name)
    {
        Assert.Throws<QosException>(() => _store.Create(new QosPolicy(name, @"C:\Games\x.exe", 800_000)));
        Assert.Throws<QosException>(() => _store.Delete(name));
    }

    [Fact]
    public void Refuses_to_create_a_policy_without_an_app_or_a_rate()
    {
        Assert.Throws<QosException>(() => _store.Create(new QosPolicy(QosPolicy.Prefix + "1", "", 800_000)));
        Assert.Throws<QosException>(() => _store.Create(new QosPolicy(QosPolicy.Prefix + "1", @"C:\Games\x.exe", 0)));
    }
}
