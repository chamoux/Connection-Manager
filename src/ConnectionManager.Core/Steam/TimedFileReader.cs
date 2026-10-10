using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using ConnectionManager.Core.Logging;
using Microsoft.Win32.SafeHandles;

namespace ConnectionManager.Core.Steam;

/// Reads files a normal user can write (Steam's libraryfolders.vdf) for the engine loop, which must never wait on one:
/// the user can hold an oplock on the file, and then any open of it waits until they let go. Each read runs on a thread
/// of its own for at most `timeout`. Past it, the last text read from that path comes back (the first time is logged),
/// Windows is asked to cancel the open, and no other read starts until that one has ended, so stuck threads can't pile
/// up. Call from one thread.
public sealed class TimedFileReader
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    private const int MaxRemembered = 16;

    private readonly Func<string, string?> _read;
    private readonly TimeSpan _timeout;
    private readonly ILog _log;
    private readonly Dictionary<string, string?> _lastGood = new(StringComparer.OrdinalIgnoreCase);
    private Task? _stuck;
    private bool _logged;

    /// SteamLibraries.ReadUntrustedFile with the default timeout.
    public TimedFileReader(ILog log) : this(SteamLibraries.ReadUntrustedFile, DefaultTimeout, log) { }

    internal TimedFileReader(Func<string, string?> read, TimeSpan timeout, ILog log)
    {
        _read = read;
        _timeout = timeout;
        _log = log;
    }

    public string? Read(string path)
    {
        if (_stuck is { IsCompleted: false }) return _lastGood.GetValueOrDefault(path);
        var read = new ThreadRead(path, _read);
        if (read.Done.Wait(_timeout))
        {
            if (read.Error is { } error) error.Throw();
            if (_lastGood.Count >= MaxRemembered && !_lastGood.ContainsKey(path)) _lastGood.Clear();
            return _lastGood[path] = read.Text;
        }
        read.Cancel();
        _stuck = read.Done;
        if (!_logged) _log.Warn($"Reading {path} did not finish in {_timeout.TotalSeconds:0.#} s (another program may be holding it); using what was read before");
        _logged = true;
        return _lastGood.GetValueOrDefault(path);
    }

    private sealed class ThreadRead
    {
        private const uint ThreadTerminate = 0x0001; // the right CancelSynchronousIo needs

        private readonly object _gate = new();
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private SafeWaitHandle? _thread; // the reading thread, while it reads

        public ThreadRead(string path, Func<string, string?> read) =>
            new Thread(() => Run(path, read)) { IsBackground = true, Name = "Connection Manager file read" }.Start();

        public Task Done => _done.Task;
        public string? Text { get; private set; }
        public ExceptionDispatchInfo? Error { get; private set; }

        /// Asks Windows to end the read's open or read, which then fails like any unreadable file. Does nothing once it ended.
        public void Cancel()
        {
            lock (_gate)
                if (_thread is { IsInvalid: false } thread) CancelSynchronousIo(thread);
        }

        private void Run(string path, Func<string, string?> read)
        {
            lock (_gate) _thread = OpenThread(ThreadTerminate, false, GetCurrentThreadId());
            try
            {
                Text = read(path);
            }
            catch (Exception ex)
            {
                Error = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                lock (_gate)
                {
                    _thread?.Dispose();
                    _thread = null;
                }
                _done.TrySetResult();
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeWaitHandle OpenThread(uint access, bool inheritHandle, uint threadId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelSynchronousIo(SafeWaitHandle thread);
}
