using System.Runtime.InteropServices;
using ConnectionManager.Core.Native;

namespace ConnectionManager.Core.Tests;

public sealed class ProcessHardeningTests
{
    [Fact]
    public void The_redirection_trust_policy_matches_winnt_h()
    {
        // PROCESS_MITIGATION_POLICY: ProcessRedirectionTrustPolicy is the 17th value; the struct is one DWORD of flags.
        Assert.Equal(16, ProcessHardening.RedirectionTrustPolicy);
        Assert.Equal(4, Marshal.SizeOf<ProcessHardening.RedirectionTrustFlags>());
    }

    [Fact]
    public void Windows_reads_the_policy_and_it_is_off_in_a_normal_process()
    {
        // Only reading: turning it on can't be undone, and would change how this test process follows junctions.
        Assert.Equal(false, ProcessHardening.IsRedirectionTrustEnforced());
    }
}
