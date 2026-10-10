using System.Runtime.InteropServices;
using ConnectionManager.Core.Ipc;

namespace ConnectionManager.Core.Network;

/// The network name and cost Windows' Network List Manager (NLM) has for an adapter, and the connectivity hint as a
/// cost fallback. NLM is free-threaded, so this works on the service's MTA worker threads.
public static class NlmInterop
{
    private const int MaxConnections = 64;
    private static readonly Guid NetworkListManagerClsid = new("DCB00C01-570F-4A9B-8D69-199FDBA5723B");

    // RCWs are shared per COM object: one reader at a time, so one call's release can't cut off another's.
    private static readonly object Gate = new();

    /// False when NLM is unavailable or has no connection on this adapter (name null, cost Unknown). Never throws.
    public static bool TryRead(Guid adapterId, out string? name, out NetworkCost cost)
    {
        name = null;
        cost = NetworkCost.Unknown;
        lock (Gate)
        {
            object? nlm = null;
            IEnumNetworkConnections? connections = null;
            try
            {
                nlm = Activator.CreateInstance(Type.GetTypeFromCLSID(NetworkListManagerClsid, throwOnError: true)!)!;
                connections = ((INetworkListManager)nlm).GetNetworkConnections();
                for (var i = 0; i < MaxConnections; i++)
                {
                    if (connections.Next(1, out var connection, out var fetched) != 0 || fetched != 1 || connection is null) return false;
                    try
                    {
                        if (connection.GetAdapterId() != adapterId) continue;
                        var network = connection.GetNetwork();
                        try
                        {
                            name = CleanName(network.GetName());
                        }
                        finally
                        {
                            Release(network);
                        }
                        if (connection is INetworkConnectionCost costs && costs.GetCost(out var raw) == 0) cost = FromNlmCost(raw);
                        return true;
                    }
                    finally
                    {
                        Release(connection);
                    }
                }
                return false;
            }
            catch (Exception) // COM, casts, a missing service: all mean "NLM can't tell us"
            {
                name = null;
                cost = NetworkCost.Unknown;
                return false;
            }
            finally
            {
                Release(connections);
                Release(nlm);
            }
        }
    }

    /// Windows' cost hint for the preferred connection, or Unknown. Never throws.
    public static NetworkCost HintCost()
    {
        try
        {
            return GetNetworkConnectivityHint(out var hint) == 0 ? FromHintCost(hint.ConnectivityCost) : NetworkCost.Unknown;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) // before Windows 10 2004
        {
            return NetworkCost.Unknown;
        }
    }

    /// NLM_CONNECTION_COST: the level is in the low 16 bits, flags (roaming, over the limit) above.
    internal static NetworkCost FromNlmCost(uint raw) => (raw & 0xFFFF) switch
    {
        1 => NetworkCost.Unrestricted,
        2 => NetworkCost.Fixed,
        4 => NetworkCost.Variable,
        _ => NetworkCost.Unknown,
    };

    /// NL_NETWORK_CONNECTIVITY_COST_HINT.
    internal static NetworkCost FromHintCost(int raw) => raw switch
    {
        1 => NetworkCost.Unrestricted,
        2 => NetworkCost.Fixed,
        3 => NetworkCost.Variable,
        _ => NetworkCost.Unknown,
    };

    /// A network name comes from outside (an access point picks its own SSID): cap its length and replace control
    /// characters, so it is safe to log and to send to the UI.
    internal static string? CleanName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var length = Math.Min(raw.Length, IpcLimits.MaxName);
        if (length < raw.Length && char.IsHighSurrogate(raw[length - 1])) length--; // don't cut a character in half
        return string.Create(length, raw, (chars, source) =>
        {
            for (var i = 0; i < chars.Length; i++) chars[i] = char.IsControl(source[i]) ? '?' : source[i];
        });
    }

    private static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com)) Marshal.FinalReleaseComObject(com);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NetworkConnectivityHint
    {
        public int ConnectivityLevel;
        public int ConnectivityCost;
        public byte ApproachingDataLimit, OverDataLimit, Roaming;
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetNetworkConnectivityHint(out NetworkConnectivityHint hint);
}

// Typed NLM interfaces (netlistmgr.h), declared only up to the last method used; vtable order matters. `dynamic`
// can't call the GUID-returning methods, so these are typed.
[ComImport, Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface INetworkListManager
{
    [return: MarshalAs(UnmanagedType.Interface)] object GetNetworks(int flags);
    INetwork GetNetwork(Guid networkId);
    IEnumNetworkConnections GetNetworkConnections();
}

[ComImport, Guid("DCB00006-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IEnumNetworkConnections
{
    [return: MarshalAs(UnmanagedType.Interface)] object _NewEnum();
    [PreserveSig] int Next(uint count, [MarshalAs(UnmanagedType.Interface)] out INetworkConnection? connection, out uint fetched);
}

[ComImport, Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface INetworkConnection
{
    INetwork GetNetwork();
    bool IsConnectedToInternet { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
    bool IsConnected { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
    int GetConnectivity();
    Guid GetConnectionId();
    Guid GetAdapterId();
}

[ComImport, Guid("DCB00002-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface INetwork
{
    [return: MarshalAs(UnmanagedType.BStr)] string GetName();
}

[ComImport, Guid("DCB0000A-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface INetworkConnectionCost
{
    [PreserveSig] int GetCost(out uint cost);
}
