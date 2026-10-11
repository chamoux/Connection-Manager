namespace ConnectionManager.Core.Tray;

/// How the tray was started. The sign-in entry passes --background and only puts the icon in the tray; any other start
/// (the Start menu, Explorer, setup) means "show me the app" and opens the window.
public static class TrayLaunch
{
    public const string Background = "--background";

    public static bool OpensWindow(IReadOnlyList<string> args) => !(args.Count == 1 && args[0] == Background);

    /// Local\ is per session and the SID tells users apart, like the tray's single-instance mutex.
    public static string OpenEventName(string userSid) => $@"Local\ConnectionManager.Tray.{userSid}.Open";
}

/// Lets a second copy of the tray ask the running one to open its window, through a named event. Any program running
/// as this user in this session can set it too; all that does is open the window.
public sealed class OpenRequests : IDisposable
{
    private readonly EventWaitHandle _event;
    private readonly RegisteredWaitHandle _wait;

    /// onRequest runs on a thread-pool thread. Throws WaitHandleCannotBeOpenedException, UnauthorizedAccessException or
    /// IOException when another program holds the name.
    public OpenRequests(string name, Action onRequest)
    {
        _event = new EventWaitHandle(false, EventResetMode.AutoReset, name);
        _wait = ThreadPool.RegisterWaitForSingleObject(_event, (_, _) =>
        {
            try
            {
                onRequest();
            }
            catch (Exception)
            {
                // Nothing may escape onto the thread pool: it would end the process. The next request tries again.
            }
        }, null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// False when no copy is listening, or the name belongs to something else.
    public static bool Send(string name)
    {
        try
        {
            using var request = EventWaitHandle.OpenExisting(name);
            return request.Set();
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _wait.Unregister(null);
        _event.Dispose();
    }
}
