using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace ConnectionManager.Core.Ipc;

/// Win32 calls for the service pipe. Every handle is a SafeHandle, and DLLs load from System32 only.
internal static class PipeNative
{
    private const int ErrorInsufficientBuffer = 122, ErrorSemTimeout = 121;

    private const uint PipeAccessDuplex = 0x3, FileFlagOverlapped = 0x40000000, FileFlagFirstPipeInstance = 0x80000;
    private const uint PipeTypeByte = 0x0, PipeRejectRemoteClients = 0x8;
    private const uint BufferSize = 64 * 1024;
    private const uint SddlRevision1 = 1;
    private const uint TokenQuery = 0x0008;
    private const int TokenUser = 1, TokenSessionId = 12, TokenIntegrityLevel = 25, TokenIsAppContainer = 29;
    private const int WtsConnectState = 8, WtsActive = 0;
    private const uint ScManagerConnect = 0x0001, ServiceQueryStatus = 0x0004;
    private const int ScStatusProcessInfo = 0;

    static PipeNative()
    {
        // Bind OpenThreadToken now: a P/Invoke's first call loads its DLL, and that must not happen while impersonating.
        OpenThreadToken(GetCurrentThread(), TokenQuery, true, out var none);
        none.Dispose();
    }

    /// PipeSecurity silently drops the mandatory label, so the pipe is created here from SDDL and then wrapped.
    /// Throws Win32Exception: with first set and the name taken, 5 (or 231 when that pipe has no free instance); 1307
    /// when the SDDL's owner can't be assigned.
    public static NamedPipeServerStream CreatePipe(string name, string sddl, bool first, int maxInstances)
    {
        var converted = ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, SddlRevision1, out var descriptor, IntPtr.Zero);
        var convertError = Marshal.GetLastPInvokeError();
        using (descriptor)
        {
            if (!converted) throw new Win32Exception(convertError);
            var added = false;
            try
            {
                descriptor.DangerousAddRef(ref added);
                var attributes = new SecurityAttributes
                {
                    Length = Marshal.SizeOf<SecurityAttributes>(),
                    SecurityDescriptor = descriptor.DangerousGetHandle(),
                };
                var handle = CreateNamedPipeW(@"\\.\pipe\" + name,
                    PipeAccessDuplex | FileFlagOverlapped | (first ? FileFlagFirstPipeInstance : 0),
                    PipeTypeByte | PipeRejectRemoteClients, (uint)maxInstances, BufferSize, BufferSize, 0, ref attributes);
                var createError = Marshal.GetLastPInvokeError();
                if (handle.IsInvalid)
                {
                    handle.Dispose();
                    throw new Win32Exception(createError);
                }
                try
                {
                    return new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, handle);
                }
                catch
                {
                    handle.Dispose();
                    throw;
                }
            }
            finally
            {
                if (added) descriptor.DangerousRelease();
            }
        }
    }

    /// Whether any process has a pipe of this name, without connecting to it: an instance is listening, or all are busy.
    public static bool PipeExists(string name) =>
        WaitNamedPipeW(@"\\.\pipe\" + name, 1) || Marshal.GetLastPInvokeError() == ErrorSemTimeout;

    /// The client's token, opened while impersonating it. Only OpenThreadToken runs inside RunAsClient: clients connect
    /// at Identification level, where any file access on the impersonating thread fails with 1346 or hangs, so the token
    /// is queried after reverting. The server must have read from the pipe first.
    public static SafeAccessTokenHandle OpenClientToken(NamedPipeServerStream pipe)
    {
        SafeAccessTokenHandle? token = null;
        var error = 0;
        pipe.RunAsClient(() =>
        {
            if (!OpenThreadToken(GetCurrentThread(), TokenQuery, true, out token)) error = Marshal.GetLastPInvokeError();
        });
        if (error == 0 && token is { IsInvalid: false }) return token;
        token?.Dispose();
        throw new Win32Exception(error);
    }

    public static SecurityIdentifier TokenUserSid(SafeAccessTokenHandle token) =>
        QueryToken(token, TokenUser, p => new SecurityIdentifier(Marshal.ReadIntPtr(p))); // TOKEN_USER.User.Sid

    public static int TokenSession(SafeAccessTokenHandle token) => QueryToken(token, TokenSessionId, Marshal.ReadInt32);

    /// The mandatory label's SID, like S-1-16-8192 for Medium.
    public static string TokenIntegritySid(SafeAccessTokenHandle token) =>
        QueryToken(token, TokenIntegrityLevel, p => new SecurityIdentifier(Marshal.ReadIntPtr(p)).Value); // TOKEN_MANDATORY_LABEL.Label.Sid

    public static bool TokenAppContainer(SafeAccessTokenHandle token) => QueryToken(token, TokenIsAppContainer, Marshal.ReadInt32) != 0;

    private static T QueryToken<T>(SafeAccessTokenHandle token, int infoClass, Func<IntPtr, T> read)
    {
        if (!GetTokenInformation(token, infoClass, IntPtr.Zero, 0, out var length))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error != ErrorInsufficientBuffer) throw new Win32Exception(error);
        }
        if (length is <= 0 or > 64 * 1024) throw new Win32Exception(ErrorInsufficientBuffer);
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetTokenInformation(token, infoClass, buffer, length, out _)) throw new Win32Exception(Marshal.GetLastPInvokeError());
            return read(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// The session's WTSConnectState is WTSActive: someone is using it, at the console or remotely.
    public static bool IsSessionActive(int sessionId)
    {
        var ok = WTSQuerySessionInformationW(IntPtr.Zero, sessionId, WtsConnectState, out var buffer, out var bytes);
        using (buffer)
        {
            return ok && !buffer.IsInvalid && bytes >= sizeof(int) && Marshal.ReadInt32(buffer.DangerousGetHandle()) == WtsActive;
        }
    }

    /// The process id of a service, from the service manager, or 0 when it isn't running. Interactive users may query
    /// a service's status by default. Throws Win32Exception (1060 when there is no such service).
    public static int ServiceProcessId(string serviceName)
    {
        using var manager = OpenSCManagerW(null, null, ScManagerConnect);
        if (manager.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        using var service = OpenServiceW(manager, serviceName, ServiceQueryStatus);
        if (service.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (!QueryServiceStatusEx(service, ScStatusProcessInfo, out var status, Marshal.SizeOf<ServiceStatusProcess>(), out _))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        return (int)status.ProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    private sealed class SafeLocalAllocHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        protected override bool ReleaseHandle() => LocalFree(handle) == IntPtr.Zero;
    }

    private sealed class SafeWtsMemoryHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        protected override bool ReleaseHandle()
        {
            WTSFreeMemory(handle);
            return true;
        }
    }

    private sealed class SafeServiceHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafePipeHandle CreateNamedPipeW(string name, uint openMode, uint pipeMode, uint maxInstances,
        uint outBufferSize, uint inBufferSize, uint defaultTimeout, ref SecurityAttributes attributes);

    [DllImport("kernel32.dll", EntryPoint = "WaitNamedPipeW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WaitNamedPipeW(string name, uint timeout);

    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out SafeLocalAllocHandle descriptor, IntPtr size);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenThreadToken(IntPtr thread, uint access, [MarshalAs(UnmanagedType.Bool)] bool openAsSelf, out SafeAccessTokenHandle token);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GetCurrentThread(); // a pseudo-handle: nothing to close

    [DllImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int infoClass, IntPtr info, int length, out int returned);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetNamedPipeServerSessionId(SafePipeHandle pipe, out uint sessionId);

    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId, int infoClass, out SafeWtsMemoryHandle buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeServiceHandle OpenSCManagerW(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeServiceHandle OpenServiceW(SafeServiceHandle manager, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(SafeServiceHandle service, int infoLevel, out ServiceStatusProcess status, int size, out int needed);

    [DllImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
