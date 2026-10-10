using System.Runtime.InteropServices;
using ConnectionManager.Core.Paths;

namespace ConnectionManager.Core.Native;

// Copied from NetWatch (src/NetWatch.Core/Capture/NativeProcess.cs).
public static class NativeProcess
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    /// The path Windows reports for a running process, exactly as reported: nothing on disk is looked at, so any process
    /// may be described this way, even one started from a network share. Null if it has exited or cannot be opened.
    public static string? GetImagePath(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new char[1024];
            var size = (uint)buffer.Length;
            return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// GetImagePath with 8.3 short names expanded (ToLongPath). Null if the process has exited or cannot be opened.
    public static string? GetPath(int pid) => GetImagePath(pid) is { } path ? ToLongPath(path) : null;

    /// The session a process runs in, or null when it has exited.
    public static int? GetSessionId(int pid) => ProcessIdToSessionId(pid, out var session) ? (int)session : null;

    /// Expands 8.3 short names (DOWNLO~1) so location checks, trust and blocks see the same path whichever way a
    /// process was found. Only a path that passes SafePaths is expanded (SafePaths does it); anything else, such as a
    /// network path, comes back unchanged without a lookup: as SYSTEM, looking it up would sign in to the server. So does
    /// a path that cannot be expanded (the file is gone, say).
    public static string ToLongPath(string path) => SafePaths.TryLocal(path, out var full) ? full : path;

    /// The long form of an existing path, or null when Windows cannot expand it. Looks the path up: SafePaths only.
    internal static string? TryLongPath(string path)
    {
        var buffer = new char[1024];
        var length = GetLongPathNameW(path, buffer, (uint)buffer.Length);
        if (length >= buffer.Length) // too small: the result is the size needed
        {
            buffer = new char[length];
            length = GetLongPathNameW(path, buffer, (uint)buffer.Length);
        }
        return length > 0 && length < buffer.Length ? new string(buffer, 0, (int)length) : null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, [Out] char[] exeName, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ProcessIdToSessionId(int processId, out uint sessionId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetLongPathNameW(string shortPath, [Out] char[] longPath, uint size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
