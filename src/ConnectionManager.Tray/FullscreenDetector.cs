using System.Runtime.InteropServices;
using ConnectionManager.Core.Games;
using ConnectionManager.Core.Ipc;
using ConnectionManager.Core.Logging;
using ConnectionManager.Core.Native;

namespace ConnectionManager.Tray;

/// Finds games outside Steam. Every 2 s, on a pool thread and never the UI thread, it looks at the foreground window
/// and the 3D load of its process; FullscreenHeuristic decides, and each confirmed or released game goes to the
/// service, which checks it again itself. Runs only while SetEnabled(true): the option is on and the service is
/// connected. Never throws; each distinct problem is logged once. The sampling state (the heuristic, the PDH query)
/// belongs to the timer's thread, so the UI thread never waits for a sample.
internal sealed class FullscreenDetector : IDisposable
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(2);
    private const int MaxLogged = 64;

    private readonly Func<GameArgs, Task<Response?>> _report;
    private readonly ILog _log;
    private readonly object _gate = new();     // _running, _disposed, _generation and the timer
    private readonly object _sampling = new(); // _heuristic, _gpu and _sampledGeneration
    private readonly FullscreenHeuristic _heuristic = new([Environment.GetFolderPath(Environment.SpecialFolder.Windows)]);
    private readonly Gpu3DSampler _gpu = new();
    private readonly System.Threading.Timer _timer;
    private readonly HashSet<string> _logged = new(StringComparer.Ordinal);
    private bool _running;
    private bool _disposed;
    private int _generation;
    private int _sampledGeneration;

    /// report: sends ReportFullscreenGame and returns the reply, or null when the tray is exiting. Must not throw.
    public FullscreenDetector(Func<GameArgs, Task<Response?>> report, ILog log)
    {
        _report = report;
        _log = log;
        _timer = new System.Threading.Timer(_ => _ = TickAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// From the UI thread on each status and on disconnect. Turning it off forgets any game found and closes the PDH
    /// query, on the timer's thread straight away.
    public void SetEnabled(bool on)
    {
        lock (_gate)
        {
            if (_disposed || on == _running) return;
            _running = on;
            _generation++;
            _timer.Change(on ? Every : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
        _log.Info(on ? "Looking for games outside Steam" : "Stopped looking for games outside Steam");
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            (_disposed, _running) = (true, false);
            _timer.Dispose();
        }
        lock (_sampling) StopSampling();
    }

    // The timer is one-shot and armed again only after the report, if any, has gone, so ticks don't pile up.
    private async Task TickAsync()
    {
        try
        {
            bool running;
            int generation;
            lock (_gate) (running, generation) = (_running, _generation);

            FullscreenEvent? found = null;
            lock (_sampling)
            {
                if (!running || generation != _sampledGeneration) StopSampling(); // turned off, or back on: start afresh
                _sampledGeneration = generation;
                if (!running) return;
                try
                {
                    found = Sample();
                }
                catch (Exception ex)
                {
                    LogOnce("sample " + ex.GetType().Name + ex.Message, $"Full-screen detection failed: {ex.Message}");
                }
            }
            if (found is not null) await ReportAsync(found);
            lock (_gate)
            {
                if (_running && !_disposed && generation == _generation) _timer.Change(Every, Timeout.InfiniteTimeSpan);
            }
        }
        catch (Exception ex) // a timer callback: nothing may escape
        {
            LogOnce("tick " + ex.GetType().Name, $"Full-screen detection stopped a tick: {ex.Message}");
        }
    }

    // Under _sampling.
    private FullscreenEvent? Sample()
    {
        var state = Foreground.NotificationState();
        var (pid, fullscreen) = Foreground.Find();
        // The path matters only for a window that could be a game; reading it is one OpenProcess.
        var path = pid > 0 && (fullscreen || state == FullscreenHeuristic.RunningD3DFullScreen) ? NativeProcess.GetPath(pid) : null;

        double? gpu = null;
        if (_heuristic.IsCandidate(pid, path, fullscreen, state))
        {
            gpu = _gpu.Sample(pid);
            if (_gpu.Problem is { } problem) LogOnce("pdh " + problem, $"Can't read the GPU load, so only exclusive full screen counts: {problem}");
        }
        else _gpu.Stop();

        var alive = _heuristic.Active is not { } game || IsRunning(game);
        var now = DateTimeOffset.UnixEpoch.AddMilliseconds(Environment.TickCount64); // monotonic: a clock change can't confirm or release
        return _heuristic.Update(new FullscreenSample(now, pid, path, fullscreen, gpu, state, alive));
    }

    // The same program still has the pid: a pid Windows reuses for another program means the game ended.
    private static bool IsRunning(FullscreenConfirmed game) =>
        NativeProcess.GetPath(game.Pid) is { } path && string.Equals(path, game.Path, StringComparison.OrdinalIgnoreCase);

    private async Task ReportAsync(FullscreenEvent found)
    {
        var args = found is FullscreenConfirmed confirmed ? new GameArgs(confirmed.Pid, confirmed.Path) : new GameArgs(0, found.Path);
        _log.Info(args.Pid > 0 ? $"Full-screen game found: {found.Path} (pid {args.Pid})" : $"Full-screen game ended: {found.Path}");
        try
        {
            if (await _report(args) is { Ok: false } reply)
                LogOnce("refused " + reply.Error, $"The service did not take the report for {found.Path}: {reply.Error}");
        }
        catch (Exception ex)
        {
            LogOnce("report " + ex.GetType().Name, $"Could not report a full-screen game: {ex.Message}");
        }
    }

    private void StopSampling()
    {
        _gpu.Stop();
        _heuristic.Reset();
    }

    private void LogOnce(string key, string message)
    {
        bool first;
        lock (_logged) first = _logged.Count < MaxLogged && _logged.Add(key);
        if (first) _log.Warn(message);
    }

    /// The foreground window, from the tray's session. The tray is PerMonitorV2 DPI-aware (the csproj), so rectangles
    /// are physical pixels and a full-screen window matches its monitor exactly.
    private static class Foreground
    {
        private const int GwlStyle = -16;
        private const long WsCaption = 0x00C00000;
        private const uint MonitorDefaultToNull = 0;

        /// SHQueryUserNotificationState, or 0 when it fails.
        public static int NotificationState() => SHQueryUserNotificationState(out var state) == 0 ? state : 0;

        /// Pid 0 for no window, the shell or desktop, or the tray itself. Fullscreen: it covers its whole monitor and is
        /// not an ordinary maximized window with a title bar.
        public static (int Pid, bool Fullscreen) Find()
        {
            var window = GetForegroundWindow();
            if (window == IntPtr.Zero || window == GetShellWindow() || window == GetDesktopWindow()) return (0, false);
            if (ClassName(window) is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return (0, false);
            GetWindowThreadProcessId(window, out var id);
            var pid = (int)id;
            if (pid == 0 || pid == Environment.ProcessId) return (0, false);
            return (pid, CoversMonitor(window));
        }

        private static bool CoversMonitor(IntPtr window)
        {
            if (IsZoomed(window) && (GetWindowLongPtrW(window, GwlStyle).ToInt64() & WsCaption) == WsCaption) return false;
            var monitor = MonitorFromWindow(window, MonitorDefaultToNull);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (monitor == IntPtr.Zero || !GetMonitorInfoW(monitor, ref info) || !GetWindowRect(window, out var rect)) return false;
            var m = info.Monitor;
            return rect.Left <= m.Left && rect.Top <= m.Top && rect.Right >= m.Right && rect.Bottom >= m.Bottom;
        }

        private static string ClassName(IntPtr window)
        {
            var buffer = new char[64];
            var length = GetClassNameW(window, buffer, buffer.Length);
            return length > 0 ? new string(buffer, 0, length) : "";
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int Size;
            public Rect Monitor;
            public Rect Work;
            public uint Flags;
        }

        [DllImport("shell32.dll")]
        private static extern int SHQueryUserNotificationState(out int state);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();

        [DllImport("user32.dll")]
        private static extern bool IsZoomed(IntPtr window);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindowLongPtrW(IntPtr window, int index);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr window, out Rect rect);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassNameW(IntPtr window, [Out] char[] name, int max);
    }

    /// One process's 3D engine load through PDH, with the instance wildcard limited to that pid, so PDH expands only
    /// its few engine instances. No admin rights needed. Adding the counter costs about 135 ms and each sample about
    /// 1.5 ms. The handle is kept while the same pid is sampled, and closed when it changes or sampling stops.
    private sealed class Gpu3DSampler
    {
        private const uint FormatDouble = 0x00000200;
        private const uint FormatNoCap100 = 0x00008000;
        private const uint MoreData = 0x800007D2;    // PDH_MORE_DATA
        private const uint NoInstance = 0x800007D1;  // PDH_CSTATUS_NO_INSTANCE
        private const uint NoData = 0x800007D5;      // PDH_NO_DATA

        private IntPtr _query;
        private IntPtr _counter;
        private int _pid;

        /// Why the last sample failed, or null.
        public string? Problem { get; private set; }

        /// The sum of the pid's 3D engine instances, 0 to 100, or null when unknown: the first sample after adding the
        /// counter (it is a rate), no instances yet (the game has not made its Direct3D device; added again next
        /// time), or a PDH failure.
        public double? Sample(int pid)
        {
            Problem = null;
            if (pid != _pid || _query == IntPtr.Zero)
            {
                Open(pid);
                return null;
            }
            var status = PdhCollectQueryData(_query);
            if (status != 0) return Failed("PdhCollectQueryData", status);

            uint size = 0;
            status = PdhGetFormattedCounterArrayW(_counter, FormatDouble | FormatNoCap100, ref size, out _, IntPtr.Zero);
            if (status != MoreData || size == 0)
            {
                Stop(); // no instances yet: add the counter again on the next sample
                return null;
            }
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                status = PdhGetFormattedCounterArrayW(_counter, FormatDouble | FormatNoCap100, ref size, out var count, buffer);
                if (status != 0) return Failed("PdhGetFormattedCounterArrayW", status);
                var stride = Marshal.SizeOf<CounterValueItem>();
                double sum = 0;
                for (var i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<CounterValueItem>(buffer + i * stride);
                    if (item.Value.Status is 0 or 1) sum += item.Value.Double; // PDH_CSTATUS_VALID_DATA, NEW_DATA
                }
                return Math.Clamp(sum, 0, 100);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public void Stop()
        {
            if (_query != IntPtr.Zero) PdhCloseQuery(_query); // closes its counters too
            (_query, _counter, _pid) = (IntPtr.Zero, IntPtr.Zero, 0);
        }

        private void Open(int pid)
        {
            Stop();
            var status = PdhOpenQueryW(null, IntPtr.Zero, out var query);
            if (status != 0)
            {
                Failed("PdhOpenQueryW", status);
                return;
            }
            (_query, _pid) = (query, pid);
            status = PdhAddEnglishCounterW(_query, $@"\GPU Engine(pid_{pid}_*engtype_3D)\Utilization Percentage", IntPtr.Zero, out _counter);
            if (status != 0)
            {
                Failed("PdhAddEnglishCounterW", status);
                return;
            }
            PdhCollectQueryData(_query); // the first of the two samples a rate needs
        }

        // No instances or no data yet just means the game has no Direct3D device yet: tried again next time, quietly.
        private double? Failed(string call, uint status)
        {
            Stop();
            if (status is not (NoInstance or NoData)) Problem = $"{call} returned 0x{status:X8}";
            return null;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CounterValue
        {
            public uint Status;
            public double Double;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CounterValueItem
        {
            public IntPtr Name;
            public CounterValue Value;
        }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

        [DllImport("pdh.dll")]
        private static extern uint PdhCollectQueryData(IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr items);

        [DllImport("pdh.dll")]
        private static extern uint PdhCloseQuery(IntPtr query);
    }
}
