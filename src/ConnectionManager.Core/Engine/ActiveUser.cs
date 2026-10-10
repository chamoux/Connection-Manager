using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using ConnectionManager.Core.Ipc;
using ConnectionManager.Core.Logging;
using ConnectionManager.Core.Paths;
using Microsoft.Win32.SafeHandles;

namespace ConnectionManager.Core.Engine;

/// The user signed in at the console: their SID and folders. A folder is null when it is not a plain local path,
/// since the user can point it anywhere.
public sealed record UserProfile(string Sid, string? LocalAppData, string? RoamingAppData, string? Profile);

/// Who is signed in at the console, for the SYSTEM service: catalog paths such as %LOCALAPPDATA%\Google\... must name
/// that user's folders, never the service's own profile. Re-read every 30 s. Call from one thread.
public sealed class ActiveUser
{
    public static readonly TimeSpan RereadEvery = TimeSpan.FromSeconds(30);

    private const uint NoSession = 0xFFFFFFFF;

    // KF_FLAG_DONT_VERIFY: without it, Windows checks the folder exists, which as SYSTEM would follow a folder the user
    // redirected to a share and sign in to that server. The path is checked as a string (SafePaths) instead.
    private const uint DontVerify = 0x4000;

    // FOLDERID_LocalAppData, FOLDERID_RoamingAppData, FOLDERID_Profile (KnownFolders.h).
    private static readonly Guid LocalAppDataId = new("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");
    private static readonly Guid RoamingAppDataId = new("3EB685DB-65F9-4CF6-A03A-E3EF65729F3D");
    private static readonly Guid ProfileId = new("5E6C858F-0E22-4760-9AFE-EA3317B67173");

    private readonly ILog _log;
    private readonly TimeProvider _time;
    private readonly Func<UserProfile?> _read;
    private UserProfile? _current;
    private DateTimeOffset? _readAt;
    private string? _lastError;

    public ActiveUser(ILog log, TimeProvider? time = null)
        : this(log, time ?? TimeProvider.System, Read)
    {
    }

    internal ActiveUser(ILog log, TimeProvider time, Func<UserProfile?> read)
    {
        _log = log;
        _time = time;
        _read = read;
    }

    /// Null when no one is signed in at the console.
    public UserProfile? Current
    {
        get
        {
            var now = _time.GetUtcNow();
            if (_readAt is { } readAt && now >= readAt && now - readAt < RereadEvery) return _current;
            _readAt = now;
            UserProfile? next;
            try
            {
                next = _read();
                _lastError = null;
            }
            catch (Exception ex) // nobody to expand for is safer than a stale or wrong user
            {
                if (ex.Message != _lastError) _log.Error("Could not read the signed-in user", ex);
                _lastError = ex.Message;
                next = null;
            }
            if (next != _current) _log.Info(next is null ? "No one is signed in at the console" : $"Signed-in user: {next.Sid}");
            return _current = next;
        }
    }

    /// The text with %LOCALAPPDATA%, %APPDATA% and %USERPROFILE% naming the signed-in user's folders, and other variables
    /// from the service's environment. Without a user (or that folder), those variables stay as they are, so the
    /// patterns that need them match nothing.
    public string ExpandUserEnv(string text) => Expand(text, Current, Environment.GetEnvironmentVariable);

    /// One pass, like ExpandEnvironmentStrings: a value is never expanded again, so a folder named "a%PATH%b" stays so.
    public static string Expand(string text, UserProfile? user, Func<string, string?> variable)
    {
        var result = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var start = text.IndexOf('%', i);
            var end = start < 0 ? -1 : text.IndexOf('%', start + 1);
            if (end < 0)
            {
                result.Append(text, i, text.Length - i);
                break;
            }
            result.Append(text, i, start - i);
            var name = text[(start + 1)..end];
            if (name.Length > 0 && Value(name, user, variable) is { } value)
            {
                result.Append(value);
                i = end + 1;
            }
            else
            {
                result.Append(text, start, end - start); // "%NAME", and the closing '%' may open the next one
                i = end;
            }
        }
        return result.ToString();
    }

    private static string? Value(string name, UserProfile? user, Func<string, string?> variable) => name.ToUpperInvariant() switch
    {
        "LOCALAPPDATA" => user?.LocalAppData,
        "APPDATA" => user?.RoamingAppData,
        "USERPROFILE" => user?.Profile,
        _ => variable(name),
    };

    /// The console session's user, from their token. Outside a service (tests, a console run) that call fails, and the
    /// account running this process stands in, but never SYSTEM: a service with no one signed in has no user.
    public static UserProfile? Read()
    {
        var session = WTSGetActiveConsoleSessionId();
        if (session != NoSession && WTSQueryUserToken(session, out var token))
        {
            using (token)
            {
                return Checked(PipeNative.TokenUserSid(token).Value,
                    KnownFolder(LocalAppDataId, token), KnownFolder(RoamingAppDataId, token), KnownFolder(ProfileId, token));
            }
        }
        using var me = WindowsIdentity.GetCurrent();
        if (me.IsSystem || me.User is not { } sid) return null;
        return Checked(sid.Value, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    // The user can redirect their folders anywhere through their own registry: only plain local folders count.
    private static UserProfile Checked(string sid, string? local, string? roaming, string? profile) => new(sid, Safe(local), Safe(roaming), Safe(profile));

    private static string? Safe(string? folder) => SafePaths.TryLocal(folder, out var full) ? full : null;

    private static string? KnownFolder(Guid id, SafeAccessTokenHandle token)
    {
        var result = SHGetKnownFolderPath(id, DontVerify, token, out var path);
        try
        {
            return result == 0 ? Marshal.PtrToStringUni(path) : null;
        }
        finally
        {
            Marshal.FreeCoTaskMem(path); // owed even when the call fails
        }
    }

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint WTSGetActiveConsoleSessionId();

    // Needs SeTcbPrivilege, so only SYSTEM gets a token.
    [DllImport("wtsapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint sessionId, out SafeAccessTokenHandle token);

    [DllImport("shell32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int SHGetKnownFolderPath(in Guid folderId, uint flags, SafeAccessTokenHandle token, out IntPtr path);
}
