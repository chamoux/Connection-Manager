namespace ConnectionManager.Core.Wifi;

/// A Wi-Fi adapter as WLAN sees it. Id is the same GUID as the network adapter's (NetworkSnapshot.AdapterId).
public sealed record WlanInterface(Guid Id, bool Connected);

/// The Native Wifi calls the Wi-Fi features need, on one client handle. Codes are Win32 error codes, 0 for success.
/// Nothing throws for ordinary failures: no WLAN service, no adapter, a disconnected interface, a handle not open.
/// The scan pause and streaming mode last only while the handle is open.
public interface IWlan
{
    /// Opens the client handle. 0 when it is already open.
    uint Open();

    /// Every WLAN interface, connected or not. Empty with a code when the handle is not open or the call fails.
    (uint Code, IReadOnlyList<WlanInterface> Interfaces) Interfaces();

    /// enabled false pauses background scans. ERROR_INVALID_STATE when the interface is not connected.
    uint SetBackgroundScan(Guid iface, bool enabled);

    uint SetStreamingMode(Guid iface, bool enabled);

    /// onConnected runs on a WLAN thread with the interface that finished connecting. It must only queue or flag
    /// work, never block or call back into this client. Registering again replaces the callback.
    uint RegisterConnectionCallback(Action<Guid> onConnected);

    /// Once it returns, the callback is not running and will not run again.
    uint Unregister();

    /// The names of the profiles on an interface, in preference order.
    (uint Code, IReadOnlyList<string> Names) ProfileNames(Guid iface);

    /// Unregisters and closes the handle, which also undoes the scan pause and streaming mode. Safe to call twice.
    void Close();
}
