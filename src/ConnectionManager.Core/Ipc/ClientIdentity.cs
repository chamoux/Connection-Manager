using System.Globalization;
using System.IO.Pipes;
using System.Security.Principal;
using ConnectionManager.Core.Native;

namespace ConnectionManager.Core.Ipc;

/// Who is on the other end of a pipe connection, read from the client's token, never from anything it sent.
/// Pid and ExePath are for logging only: a process id can be reused by the time anyone looks at it. ExePath is as Windows
/// reports it, never looked up on disk: a client started from a share must not make SYSTEM sign in to the server.
/// MayChange: the client's session is the active one (WTSActive). Only an active session may change anything.
public sealed record ClientIdentity(
    SecurityIdentifier User, int SessionId, int IntegrityRid, bool IsAppContainer, int Pid, string? ExePath, bool MayChange)
{
    public const int MediumRid = 0x2000;

    /// Null when the client may use the pipe, otherwise why not. Services run in session 0; a sandboxed or
    /// Low-integrity process (a browser tab, say) must not drive a SYSTEM service.
    internal string? Refusal(bool allowSession0) =>
        SessionId == 0 && !allowSession0 ? "it runs in session 0"
        : IntegrityRid < MediumRid ? "its integrity level is below Medium"
        : IsAppContainer ? "it runs in an AppContainer"
        : null;

    internal static ClientIdentity FromPipe(NamedPipeServerStream pipe)
    {
        using var token = PipeNative.OpenClientToken(pipe);
        var session = PipeNative.TokenSession(token);
        var pid = PipeNative.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var id) ? (int)id : 0;
        return new ClientIdentity(PipeNative.TokenUserSid(token), session, Rid(PipeNative.TokenIntegritySid(token)),
            PipeNative.TokenAppContainer(token), pid, pid == 0 ? null : NativeProcess.GetImagePath(pid), PipeNative.IsSessionActive(session));
    }

    internal static bool IsSessionActive(int sessionId) => PipeNative.IsSessionActive(sessionId);

    // S-1-16-<rid>. Anything unexpected counts as Untrusted (0), which is refused.
    private static int Rid(string label) =>
        label.StartsWith("S-1-16-", StringComparison.Ordinal)
        && int.TryParse(label.AsSpan(7), NumberStyles.None, CultureInfo.InvariantCulture, out var rid) ? rid : 0;
}
