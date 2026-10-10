using System.Diagnostics;
using ConnectionManager.Core.Qos;

namespace ConnectionManager.Integration.Tests;

/// Real ActiveStore QoS policies. Windows stores their names in lowercase, so names compare ignoring case. The names use a test-only prefix and are removed in finally, so the caps of a real
/// install on this PC are left alone. The exe does not need to exist.
public sealed class CimQosStoreTests : IDisposable
{
    private const string TestPrefix = QosPolicy.Prefix + "test-";

    private readonly string _folder = Directory.CreateTempSubdirectory("cm-qos-").FullName;
    private readonly string _name = TestPrefix + Guid.NewGuid().ToString("N")[..8];
    private readonly CimQosStore _store = new();

    private string ExePath => Path.Combine(_folder, "uploader.exe");

    [ElevatedFact]
    public void Creates_lists_and_deletes_one_policy_and_leaves_nothing_behind()
    {
        var others = OthersOfOurs();
        try
        {
            _store.Create(new QosPolicy(_name, ExePath, 100 * UploadCapManager.BitsPerKBps));

            var listed = Assert.Single(_store.List(), p => string.Equals(p.Name, _name, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(ExePath, listed.AppPath, ignoreCase: true);
            Assert.Equal(800_000UL, listed.BitsPerSecond);
            // The ActiveStore only: the persistent store, which would outlive a reboot, never sees it.
            Assert.DoesNotContain(new CimQosStore(policyStore: null).List(), p => string.Equals(p.Name, _name, StringComparison.OrdinalIgnoreCase));
            Assert.Throws<QosException>(() => _store.Create(listed)); // names are unique

            _store.Delete(_name);
            _store.Delete(_name); // already gone: nothing happens
        }
        finally
        {
            Cleanup();
        }

        Assert.DoesNotContain(_store.List(), p => p.Name.StartsWith(TestPrefix, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(others, OthersOfOurs());
    }

    [ElevatedFact]
    public void ThrottleRateAction_is_in_bits_per_second()
    {
        // New-NetQosPolicy names its unit, -ThrottleRateActionBitsPerSecond, and passes the number straight through to
        // ThrottleRateAction. Reading it back through our store shows which unit the store's number is in.
        try
        {
            RunPowerShell($"New-NetQosPolicy -Name '{Quote(_name)}' -PolicyStore ActiveStore -AppPathNameMatchCondition '{Quote(ExePath)}' -ThrottleRateActionBitsPerSecond 800000 | Out-Null");

            Assert.Equal(800_000UL, Assert.Single(_store.List(), p => string.Equals(p.Name, _name, StringComparison.OrdinalIgnoreCase)).BitsPerSecond);
        }
        finally
        {
            Cleanup();
        }
    }

    private List<QosPolicy> OthersOfOurs() =>
        _store.List().Where(p => !p.Name.StartsWith(TestPrefix, StringComparison.OrdinalIgnoreCase)).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();

    // Never masks the test's own failure.
    private void Cleanup()
    {
        try
        {
            foreach (var policy in _store.List().Where(p => p.Name.StartsWith(TestPrefix, StringComparison.OrdinalIgnoreCase))) _store.Delete(policy.Name);
        }
        catch (QosException)
        {
        }
    }

    private static string Quote(string text) => text.Replace("'", "''");

    private static void RunPowerShell(string script)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command", "$ErrorActionPreference = 'Stop'; " + script }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"PowerShell exited with {process.ExitCode}: {error} {output.Result}");
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);
}
