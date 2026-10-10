using System.Globalization;
using ConnectionManager.Core.Caps;
using ConnectionManager.Core.Logging;
using Microsoft.Win32;

namespace ConnectionManager.Integration.Tests;

/// The real Delivery Optimization policy key. Whatever this PC had before (usually no key at all), apply and restore
/// must leave exactly that.
public sealed class DoCapRoundTripTests : IDisposable
{
    private readonly string _data = Directory.CreateTempSubdirectory("cm-journal-").FullName;

    [ElevatedFact]
    public void Apply_then_restore_leaves_the_policy_key_as_it_was()
    {
        var before = Snapshot();
        var registry = new HklmRegistryStore();
        var log = new FileLog(Path.Combine(_data, "logs"));
        var caps = new DoCapManager(registry, new PolicyJournal(_data, log), log, DoCapManager.RefreshMachinePolicy);

        var applied = caps.Apply(123);
        try
        {
            Assert.Null(applied.Problem);
            Assert.True(registry.KeyExists(DoCapManager.PolicyKey));
            foreach (var name in DoCapManager.ValueNames.Except(applied.Skipped))
                Assert.Equal(new RegistryValue(RegistryValueKind.DWord, 123u), registry.Read(DoCapManager.PolicyKey, name));
        }
        finally
        {
            var restored = caps.Restore();
            Assert.True(restored.Complete, restored.Problem);
            Assert.Null(restored.Problem);
        }

        Assert.Equal(before, Snapshot());
        Assert.False(File.Exists(Path.Combine(_data, "journal.json")));
    }

    // Every value and subkey, read straight from the registry rather than through the store under test.
    private static string Snapshot()
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hklm.OpenSubKey(DoCapManager.PolicyKey);
        if (key is null) return "no key";
        var values = key.GetValueNames().Order(StringComparer.OrdinalIgnoreCase).Select(name =>
            $"{name}={key.GetValueKind(name)}:{Format(key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames))}");
        var subKeys = key.GetSubKeyNames().Order(StringComparer.OrdinalIgnoreCase).Select(name => $@"{name}\");
        return string.Join(Environment.NewLine, ["key", .. values, .. subKeys]);
    }

    private static string Format(object? data) => data switch
    {
        byte[] bytes => Convert.ToHexString(bytes),
        string[] lines => string.Join("|", lines),
        _ => Convert.ToString(data, CultureInfo.InvariantCulture) ?? "",
    };

    public void Dispose() => Directory.Delete(_data, recursive: true);
}
