using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace ConnectionManager.Core.Wifi;

/// The real IWlan over wlanapi.dll, one client handle. Call it from one thread; the connection callback arrives on a
/// WLAN thread. A PC without the WLAN service, or without wlanapi.dll, gets error codes, never exceptions.
public sealed class WlanClient : IWlan, IDisposable
{
    // Caps on what a list from the WLAN service may make us read.
    private const int MaxInterfaces = 32;
    private const int MaxProfiles = 512;

    // Rooted for the life of the process, so Windows can never call a collected delegate. The context Windows hands
    // back is a registration number looked up here, not a GCHandle, so a late call finds nothing instead of a freed
    // handle.
    private static readonly WlanNative.WLAN_NOTIFICATION_CALLBACK Callback = OnNotification;
    private static readonly ConcurrentDictionary<long, Action<Guid>> Registrations = new();
    private static long _lastRegistration;

    private SafeWlanHandle? _handle;
    private long _registration;

    public uint Open()
    {
        if (_handle is not null) return 0;
        try
        {
            var code = WlanNative.WlanOpenHandle(WlanNative.ClientVersion, IntPtr.Zero, out _, out var handle);
            if (code != 0)
            {
                handle.Dispose();
                return code;
            }
            _handle = handle;
            return 0;
        }
        catch (DllNotFoundException)
        {
            return WlanNative.ERROR_MOD_NOT_FOUND;
        }
        catch (EntryPointNotFoundException)
        {
            return WlanNative.ERROR_PROC_NOT_FOUND;
        }
    }

    public (uint Code, IReadOnlyList<WlanInterface> Interfaces) Interfaces()
    {
        if (_handle is null) return (WlanNative.ERROR_INVALID_HANDLE, []);
        var code = WlanNative.WlanEnumInterfaces(_handle, IntPtr.Zero, out var list);
        if (code != 0) return (code, []);
        try
        {
            var items = ReadList<WLAN_INTERFACE_INFO>(list, MaxInterfaces);
            return (0, items.Select(i => new WlanInterface(i.InterfaceGuid, i.isState == WlanNative.InterfaceConnected)).ToList());
        }
        finally
        {
            WlanNative.WlanFreeMemory(list);
        }
    }

    public uint SetBackgroundScan(Guid iface, bool enabled) => Set(iface, WlanNative.OpcodeBackgroundScanEnabled, enabled);

    public uint SetStreamingMode(Guid iface, bool enabled) => Set(iface, WlanNative.OpcodeMediaStreamingMode, enabled);

    public uint RegisterConnectionCallback(Action<Guid> onConnected)
    {
        if (_handle is null) return WlanNative.ERROR_INVALID_HANDLE;
        if (_registration != 0)
        {
            Registrations[_registration] = onConnected;
            return 0;
        }
        var id = Interlocked.Increment(ref _lastRegistration);
        Registrations[id] = onConnected;
        var code = WlanNative.WlanRegisterNotification(_handle, WlanNative.NotificationSourceAcm, bIgnoreDuplicate: true, Callback,
            (IntPtr)id, IntPtr.Zero, out _);
        if (code != 0)
        {
            Registrations.TryRemove(id, out _);
            return code;
        }
        _registration = id;
        return 0;
    }

    public uint Unregister()
    {
        if (_registration == 0) return 0;
        // WLAN waits for a running callback to finish before this returns, so it must never be called from one.
        var code = _handle is null ? 0
            : WlanNative.WlanRegisterNotification(_handle, WlanNative.NotificationSourceNone, bIgnoreDuplicate: true, null,
                IntPtr.Zero, IntPtr.Zero, out _);
        Registrations.TryRemove(_registration, out _); // even when that failed: a late notification then finds nothing
        _registration = 0;
        return code;
    }

    public (uint Code, IReadOnlyList<string> Names) ProfileNames(Guid iface)
    {
        if (_handle is null) return (WlanNative.ERROR_INVALID_HANDLE, []);
        var code = WlanNative.WlanGetProfileList(_handle, iface, IntPtr.Zero, out var list);
        if (code != 0) return (code, []);
        try
        {
            var items = ReadList<WLAN_PROFILE_INFO>(list, MaxProfiles);
            return (0, items.Select(p => p.strProfileName).Where(n => !string.IsNullOrEmpty(n)).ToList());
        }
        finally
        {
            WlanNative.WlanFreeMemory(list);
        }
    }

    public void Close()
    {
        Unregister();
        _handle?.Dispose();
        _handle = null;
    }

    public void Dispose() => Close();

    private uint Set(Guid iface, int opcode, bool enabled)
    {
        if (_handle is null) return WlanNative.ERROR_INVALID_HANDLE;
        var value = enabled ? 1 : 0;
        return WlanNative.WlanSetInterface(_handle, iface, opcode, sizeof(int), value, IntPtr.Zero);
    }

    // A WLAN_*_LIST: two uint counters, then the items.
    private static List<T> ReadList<T>(IntPtr list, int max) where T : struct
    {
        var count = (int)Math.Min((uint)Marshal.ReadInt32(list), (uint)max);
        var stride = Marshal.SizeOf<T>();
        var items = new List<T>(count);
        for (var i = 0; i < count; i++) items.Add(Marshal.PtrToStructure<T>(list + WlanNative.ListItemsOffset + i * stride));
        return items;
    }

    // Runs on a WLAN thread. It only hands the interface to the registered callback, which only queues it.
    private static void OnNotification(IntPtr data, IntPtr context)
    {
        try
        {
            if (data == IntPtr.Zero) return;
            var notification = Marshal.PtrToStructure<WLAN_NOTIFICATION_DATA>(data);
            if (notification.NotificationSource != WlanNative.NotificationSourceAcm
                || notification.NotificationCode != WlanNative.AcmConnectionComplete) return;
            if (Registrations.TryGetValue((long)context, out var onConnected)) onConnected(notification.InterfaceGuid);
        }
        catch (Exception)
        {
            // Nothing may unwind into the WLAN thread. The tuner's callback logs its own failures.
        }
    }
}
