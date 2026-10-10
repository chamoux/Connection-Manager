using ConnectionManager.Core.Wifi;

namespace ConnectionManager.Core.Engine;

/// Drives WifiTuner from the tick: scans paused while Game Mode is on and Wi-Fi tuning is allowed. A reconnect during a
/// game arrives on a WLAN thread, which only wakes the engine (ServiceEngine.Wake); that early tick tunes it again.
public sealed class WifiController : IWifiController
{
    private readonly WifiTuner _tuner;
    private readonly Action? _wake;

    /// wake must be safe from any thread and must not block.
    public WifiController(WifiTuner tuner, Action? wake)
    {
        _tuner = tuner;
        _wake = wake;
        if (wake is not null) _tuner.ReapplyRequested += wake;
    }

    public string? Update(TickContext context)
    {
        _tuner.SetActive(context.Game.Active, context.Settings.WifiTuning);
        return _tuner.Problem;
    }

    /// Puts the scan settings back and closes the WLAN handle.
    public void Dispose()
    {
        if (_wake is not null) _tuner.ReapplyRequested -= _wake;
        _tuner.Dispose();
    }
}
