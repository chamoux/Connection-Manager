namespace ConnectionManager.Core.Ipc;

/// Caps on what crosses the pipe. The service enforces the client limits.
public static class IpcLimits
{
    /// What a client may send, and the default for every read and write.
    public const int MaxFrame = 64 * 1024;

    /// What the service may send: a status carries every settings list, which can outgrow a client frame.
    public const int MaxServerFrame = 2 * 1024 * 1024;

    public const int MaxPath = 1024;
    public const int MaxName = 256;     // names and ids
    public const int MaxListEntries = 200; // each settings list
    public const int MaxHotkey = 64;
}
