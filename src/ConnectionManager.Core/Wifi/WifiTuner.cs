using System.Collections.Concurrent;
using ConnectionManager.Core.Logging;

namespace ConnectionManager.Core.Wifi;

/// Pauses Wi-Fi background scans and turns on streaming mode while a game runs, to avoid the lag spikes a scan
/// causes. One WLAN handle stays open for the whole game: Windows drops both settings when the handle closes, when the
/// service dies, or when the Wi-Fi disconnects, so nothing here can outlive the game. A reconnect during the game is
/// tuned again. Never throws; a PC without Wi-Fi gets a silent no-op.
public sealed class WifiTuner : IDisposable
{
    public const int MaxPending = 32;
    public const string NotSupportedProblem = "This Wi-Fi adapter doesn't support pausing scans";

    private readonly IWlan _wlan;
    private readonly ILog _log;
    private readonly object _gate = new();
    private readonly ConcurrentQueue<Guid> _pending = new();
    private readonly HashSet<Guid> _scanPaused = [];
    private readonly HashSet<Guid> _streaming = [];
    private readonly HashSet<string> _logged = new(StringComparer.Ordinal);
    private readonly Action<Guid> _queueConnect;
    private bool _open;
    private bool _openTried;
    private bool _disposed;
    private int _signalErrorLogged;

    public WifiTuner(IWlan wlan, ILog log)
    {
        _wlan = wlan;
        _log = log;
        _queueConnect = QueueConnect;
    }

    /// Raised on a WLAN thread when a Wi-Fi connection completes during a game. Handlers must only wake the Engine
    /// loop (set a flag, post a message), never block: the next SetActive does the work.
    public event Action? ReapplyRequested;

    /// True while a WLAN handle is open for a game.
    public bool Active
    {
        get
        {
            lock (_gate) return _open;
        }
    }

    /// For the status, only when it matters to the user. Null outside a game.
    public string? Problem { get; private set; }

    public bool ReapplyPending => !_pending.IsEmpty;

    /// Called by the Engine every tick. Idempotent: opens and tunes on the first active call, re-tunes queued
    /// reconnects, and puts everything back on the first inactive one.
    public void SetActive(bool gameModeActive, bool enabled)
    {
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                if (gameModeActive && enabled)
                {
                    if (!_open && !_openTried) Start();
                    while (_open && _pending.TryDequeue(out var iface)) Apply(iface);
                }
                else
                {
                    if (_open) Stop();
                    _openTried = false; // the next game tries again
                    _pending.Clear();
                }
            }
            catch (Exception ex)
            {
                LogOnce($"unexpected:{ex.Message}", () => _log.Error("Wi-Fi tuning failed", ex));
            }
        }
    }

    /// Tunes an interface that just connected, when a game is on. Call it on the Engine loop, never from the WLAN callback.
    public void OnConnected(Guid iface)
    {
        lock (_gate)
        {
            if (_disposed || !_open) return;
            try
            {
                Apply(iface);
            }
            catch (Exception ex)
            {
                LogOnce($"unexpected:{ex.Message}", () => _log.Error("Wi-Fi tuning failed", ex));
            }
        }
    }

    /// Puts everything back and closes the handle.
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (_open) Stop();
            }
            catch (Exception ex)
            {
                _log.Error("Could not put the Wi-Fi settings back", ex);
            }
        }
    }

    private void Start()
    {
        _openTried = true; // once per game: a PC without WLAN is not asked again every tick
        var code = _wlan.Open();
        if (code != 0)
        {
            if (WlanNative.IsNoWlan(code)) LogOnce($"open:{code}", () => _log.Info($"Wi-Fi tuning: no WLAN service on this PC (error {code}), nothing to tune"));
            else LogOnce($"open:{code}", () => _log.Warn($"Wi-Fi tuning: could not open the WLAN service (error {code})"));
            return;
        }
        _open = true;

        // Registered before tuning, so a connection that completes in between is not missed.
        var registered = _wlan.RegisterConnectionCallback(_queueConnect);
        if (registered != 0)
            LogOnce($"register:{registered}", () => _log.Warn($"Wi-Fi tuning: a reconnect during a game will not be tuned again (error {registered})"));

        var (listed, interfaces) = _wlan.Interfaces();
        if (listed != 0) LogOnce($"interfaces:{listed}", () => _log.Warn($"Wi-Fi tuning: could not list the Wi-Fi adapters (error {listed})"));
        foreach (var iface in interfaces.Where(i => i.Connected)) Apply(iface.Id);
        if (_scanPaused.Count > 0 || _streaming.Count > 0)
            _log.Info($"Wi-Fi tuning on: scans paused on {_scanPaused.Count} adapter(s), streaming mode on {_streaming.Count}");
    }

    private void Apply(Guid iface)
    {
        var scan = _wlan.SetBackgroundScan(iface, false);
        if (scan == 0) _scanPaused.Add(iface);
        else
        {
            if (scan == WlanNative.ERROR_NOT_SUPPORTED) Problem = NotSupportedProblem;
            Report("pause scans", scan);
        }

        var streaming = _wlan.SetStreamingMode(iface, true);
        if (streaming == 0) _streaming.Add(iface);
        else Report("turn on streaming mode", streaming);
    }

    private void Stop()
    {
        // Unregistered first, so putting things back raises no notification of our own.
        var unregistered = _wlan.Unregister();
        if (unregistered != 0) LogOnce($"unregister:{unregistered}", () => _log.Warn($"Wi-Fi tuning: could not unregister (error {unregistered})"));

        // Closing the handle undoes both settings anyway; setting them back first is the belt to those braces.
        // ERROR_INVALID_STATE means the Wi-Fi disconnected, which already undid them.
        foreach (var iface in _scanPaused) RevertReport("resume scans", _wlan.SetBackgroundScan(iface, true));
        foreach (var iface in _streaming) RevertReport("turn off streaming mode", _wlan.SetStreamingMode(iface, false));
        var tuned = _scanPaused.Count > 0 || _streaming.Count > 0;
        _wlan.Close();

        _scanPaused.Clear();
        _streaming.Clear();
        _open = false;
        Problem = null;
        _pending.Clear();
        if (tuned) _log.Info("Wi-Fi tuning off: scans and streaming mode back to normal");
    }

    private void Report(string what, uint code)
    {
        if (code == WlanNative.ERROR_INVALID_STATE)
            LogOnce($"{what}:{code}", () => _log.Info($"Wi-Fi tuning: could not {what}, the Wi-Fi is not connected (error {code})"));
        else if (code == WlanNative.ERROR_NOT_SUPPORTED)
            LogOnce($"{what}:{code}", () => _log.Info($"Wi-Fi tuning: this adapter cannot {what} (error {code})"));
        else
            LogOnce($"{what}:{code}", () => _log.Warn($"Wi-Fi tuning: could not {what} (error {code})"));
    }

    private void RevertReport(string what, uint code)
    {
        if (code is 0 or WlanNative.ERROR_INVALID_STATE) return;
        LogOnce($"{what}:{code}", () => _log.Warn($"Wi-Fi tuning: could not {what}; closing the handle undoes it (error {code})"));
    }

    private void LogOnce(string key, Action log)
    {
        if (_logged.Add(key)) log();
    }

    // On a WLAN thread: queue and signal only. Never takes _gate, so Unregister, which waits for this to finish,
    // cannot deadlock against it.
    private void QueueConnect(Guid iface)
    {
        if (_pending.Count < MaxPending) _pending.Enqueue(iface);
        try
        {
            ReapplyRequested?.Invoke();
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _signalErrorLogged, 1) == 0) _log.Error("Wi-Fi tuning: the reconnect signal failed", ex);
        }
    }
}
