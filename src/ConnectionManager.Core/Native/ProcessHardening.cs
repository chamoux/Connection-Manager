using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ConnectionManager.Core.Native;

/// Process-wide hardening for the SYSTEM service.
public static class ProcessHardening
{
    /// PROCESS_MITIGATION_POLICY.ProcessRedirectionTrustPolicy (winnt.h).
    internal const int RedirectionTrustPolicy = 16;

    private const uint EnforceFlag = 1; // bit 0 EnforceRedirectionTrust; bit 1 only audits

    /// Turns on RedirectionGuard: from now on this process never follows a junction that a normal user created, wherever
    /// it is. Null when it is on, otherwise why not (a Windows too old for it, say). It can't be turned off again.
    public static string? EnforceRedirectionTrust()
    {
        var policy = new RedirectionTrustFlags { Flags = EnforceFlag };
        if (!SetProcessMitigationPolicy(RedirectionTrustPolicy, ref policy, (nuint)Marshal.SizeOf<RedirectionTrustFlags>()))
            return new Win32Exception(Marshal.GetLastPInvokeError()).Message;
        return IsRedirectionTrustEnforced() == true ? null : "Windows accepted RedirectionGuard but does not report it as on.";
    }

    /// Whether RedirectionGuard is on for this process, or null when Windows can't say.
    public static bool? IsRedirectionTrustEnforced() =>
        GetProcessMitigationPolicy(GetCurrentProcess(), RedirectionTrustPolicy, out var policy, (nuint)Marshal.SizeOf<RedirectionTrustFlags>())
            ? (policy.Flags & EnforceFlag) != 0
            : null;

    /// PROCESS_MITIGATION_REDIRECTION_TRUST_POLICY: one DWORD of bit flags.
    [StructLayout(LayoutKind.Sequential)]
    internal struct RedirectionTrustFlags
    {
        public uint Flags;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessMitigationPolicy(int policy, ref RedirectionTrustFlags buffer, nuint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMitigationPolicy(IntPtr process, int policy, out RedirectionTrustFlags buffer, nuint length);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
}
