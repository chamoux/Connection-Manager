using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace ConnectionManager.Setup;

/// The Win32 calls setup makes. Every handle is a SafeHandle, and DLLs load from System32 only.
internal static class Native
{
    public const uint LoadLibrarySearchSystem32 = 0x800;

    private const int TokenElevation = 20; // TOKEN_INFORMATION_CLASS
    private const uint SddlRevision1 = 1;
    private const uint ScManagerConnect = 0x1, ServiceQueryStatus = 0x4, ServiceRunning = 4;
    private const int ScStatusProcessInfo = 0;
    private const uint SeeMaskNoCloseProcess = 0x40, SeeMaskNoAsync = 0x100, SeeMaskFlagNoUi = 0x400, SeeMaskNoZoneChecks = 0x800000;
    private const int SwShowNormal = 1;
    private const uint CoinitApartmentThreaded = 0x2, CoinitDisableOle1Dde = 0x4;
    private const uint Infinite = 0xFFFFFFFF;

    /// Whether this process has admin rights: elevated, or an admin's token with UAC turned off.
    public static bool IsElevated()
    {
        using var me = WindowsIdentity.GetCurrent();
        return GetTokenInformation(me.AccessToken, TokenElevation, out var elevated, sizeof(int), out _) && elevated != 0;
    }

    /// The Windows folder from Windows itself, not from %windir% or %SystemRoot%, which a user can override.
    public static string WindowsFolder()
    {
        var buffer = new char[260];
        var length = GetSystemWindowsDirectoryW(buffer, (uint)buffer.Length);
        if (length == 0 || length >= buffer.Length) throw new Win32Exception(Marshal.GetLastPInvokeError());
        return new string(buffer, 0, (int)length);
    }

    /// Creates the folder with the whole security descriptor `sddl` in one step. Fails when anything, a link included, is
    /// already at that path. Throws Win32Exception.
    public static void CreateDirectory(string path, string sddl)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, SddlRevision1, out var descriptor, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        using (descriptor)
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), SecurityDescriptor = descriptor.DangerousGetHandle() };
            if (!CreateDirectoryW(path, ref attributes)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    /// Whether the service is running, from the service manager. False when there is no such service.
    public static bool IsServiceRunning(string name)
    {
        using var manager = OpenSCManagerW(null, null, ScManagerConnect);
        if (manager.IsInvalid) return false;
        using var service = OpenServiceW(manager, name, ServiceQueryStatus);
        return !service.IsInvalid
            && QueryServiceStatusEx(service, ScStatusProcessInfo, out var status, Marshal.SizeOf<ServiceStatusProcess>(), out _)
            && status.CurrentState == ServiceRunning;
    }

    /// Runs the program elevated, through the UAC prompt, and returns its exit code once it ends. Throws Win32Exception,
    /// with 1223 when the prompt was cancelled. No zone check: the user already chose to run this file, so one that came
    /// from the internet does not get a second SmartScreen prompt.
    public static int RunElevated(string file, string arguments, string directory)
    {
        IntPtr[] text = [Marshal.StringToHGlobalUni("runas"), Marshal.StringToHGlobalUni(file), Marshal.StringToHGlobalUni(arguments), Marshal.StringToHGlobalUni(directory)];
        try
        {
            var info = new ShellExecuteInfo
            {
                Size = Marshal.SizeOf<ShellExecuteInfo>(),
                Mask = SeeMaskNoCloseProcess | SeeMaskNoAsync | SeeMaskFlagNoUi | SeeMaskNoZoneChecks,
                Verb = text[0],
                File = text[1],
                Parameters = text[2],
                Directory = text[3],
                Show = SwShowNormal,
            };
            // COM as ShellExecuteEx asks, for the call only: this thread then waits without pumping messages.
            var com = CoInitializeEx(IntPtr.Zero, CoinitApartmentThreaded | CoinitDisableOle1Dde);
            var started = ShellExecuteExW(ref info);
            var error = Marshal.GetLastPInvokeError();
            if (com >= 0) CoUninitialize();
            if (!started) throw new Win32Exception(error);
            using var process = new SafeProcessHandle(info.Process, ownsHandle: true);
            if (process.IsInvalid) throw new Win32Exception("Windows started nothing to wait for.");
            if (WaitForSingleObject(process, Infinite) != 0 || !GetExitCodeProcess(process, out var exitCode))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            return (int)exitCode;
        }
        finally
        {
            foreach (var pointer in text) Marshal.FreeHGlobal(pointer);
        }
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

    /// SHELLEXECUTEINFOW, with its strings as pointers so nothing in it needs marshalling.
    [StructLayout(LayoutKind.Sequential)]
    private struct ShellExecuteInfo
    {
        public int Size;
        public uint Mask;
        public IntPtr Window, Verb, File, Parameters, Directory;
        public int Show;
        public IntPtr InstApp, IdList, Class, ClassKey;
        public uint HotKey;
        public IntPtr IconOrMonitor, Process;
    }

    private sealed class SafeLocalAllocHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        protected override bool ReleaseHandle() => LocalFree(handle) == IntPtr.Zero;
    }

    private sealed class SafeServiceHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetDefaultDllDirectories(uint directoryFlags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetSystemWindowsDirectoryW([Out] char[] buffer, uint size);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int infoClass, out int info, int length, out int returned);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out SafeLocalAllocHandle descriptor, IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, ref SecurityAttributes attributes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern SafeServiceHandle OpenSCManagerW(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern SafeServiceHandle OpenServiceW(SafeServiceHandle manager, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(SafeServiceHandle service, int infoLevel, out ServiceStatusProcess status, int size, out int needed);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("shell32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteExW(ref ShellExecuteInfo info);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
}
