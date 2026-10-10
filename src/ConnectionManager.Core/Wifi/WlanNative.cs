using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ConnectionManager.Core.Wifi;

// Native Wifi (wlanapi.h) declarations, 64-bit layouts. WlanLayoutTests pins the sizes and the offsets the code reads.
// The list structs are variable length: only the header and the first item are declared, and the code walks the
// items by hand from ListItemsOffset with a stride of Marshal.SizeOf of the item.

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct WLAN_INTERFACE_INFO
{
    public Guid InterfaceGuid;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strInterfaceDescription;
    public int isState; // WLAN_INTERFACE_STATE
}

[StructLayout(LayoutKind.Sequential)]
internal struct WLAN_INTERFACE_INFO_LIST
{
    public uint dwNumberOfItems;
    public uint dwIndex;
    public WLAN_INTERFACE_INFO InterfaceInfo; // the first of dwNumberOfItems
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct WLAN_PROFILE_INFO
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strProfileName;
    public uint dwFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WLAN_PROFILE_INFO_LIST
{
    public uint dwNumberOfItems;
    public uint dwIndex;
    public WLAN_PROFILE_INFO ProfileInfo; // the first of dwNumberOfItems
}

[StructLayout(LayoutKind.Sequential)]
internal struct WLAN_NOTIFICATION_DATA
{
    public uint NotificationSource;
    public uint NotificationCode;
    public Guid InterfaceGuid;
    public uint dwDataSize;
    public IntPtr pData;
}

internal sealed class SafeWlanHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeWlanHandle() : base(ownsHandle: true)
    {
    }

    // Closing the client handle also makes Windows drop the scan pause and streaming mode it set.
    protected override bool ReleaseHandle() => WlanNative.WlanCloseHandle(handle, IntPtr.Zero) == 0;
}

internal static class WlanNative
{
    public const uint ClientVersion = 2; // Vista and later
    public const int ListItemsOffset = 8;
    public const int InterfaceConnected = 1; // wlan_interface_state_connected

    public const int OpcodeBackgroundScanEnabled = 2; // wlan_intf_opcode_background_scan_enabled
    public const int OpcodeMediaStreamingMode = 3;    // wlan_intf_opcode_media_streaming_mode

    public const uint NotificationSourceNone = 0;
    public const uint NotificationSourceAcm = 0x8;
    public const uint AcmConnectionComplete = 10;  // wlan_notification_acm_connection_complete

    public const uint ERROR_FILE_NOT_FOUND = 2;
    public const uint ERROR_INVALID_HANDLE = 6;
    public const uint ERROR_NOT_SUPPORTED = 50;
    public const uint ERROR_MOD_NOT_FOUND = 126;     // wlanapi.dll missing (Server without the Wireless LAN feature)
    public const uint ERROR_PROC_NOT_FOUND = 127;
    public const uint ERROR_SERVICE_DOES_NOT_EXIST = 1060;
    public const uint ERROR_SERVICE_NOT_ACTIVE = 1062; // WlanSvc stopped or disabled
    public const uint RPC_S_SERVER_UNAVAILABLE = 1722;
    public const uint EPT_S_NOT_REGISTERED = 1753;
    public const uint ERROR_INVALID_STATE = 5023;    // the interface is not connected

    /// Codes that mean this PC has no usable WLAN service at all, so there is nothing to tune.
    public static bool IsNoWlan(uint code) => code is ERROR_FILE_NOT_FOUND or ERROR_MOD_NOT_FOUND or ERROR_PROC_NOT_FOUND
        or ERROR_SERVICE_DOES_NOT_EXIST or ERROR_SERVICE_NOT_ACTIVE or RPC_S_SERVER_UNAVAILABLE or EPT_S_NOT_REGISTERED;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate void WLAN_NOTIFICATION_CALLBACK(IntPtr data, IntPtr context);

    [DllImport("wlanapi.dll")]
    public static extern uint WlanOpenHandle(uint dwClientVersion, IntPtr pReserved, out uint pdwNegotiatedVersion, out SafeWlanHandle phClientHandle);

    [DllImport("wlanapi.dll")]
    public static extern uint WlanCloseHandle(IntPtr hClientHandle, IntPtr pReserved);

    [DllImport("wlanapi.dll")]
    public static extern uint WlanEnumInterfaces(SafeWlanHandle hClientHandle, IntPtr pReserved, out IntPtr ppInterfaceList);

    [DllImport("wlanapi.dll")]
    public static extern uint WlanGetProfileList(SafeWlanHandle hClientHandle, in Guid pInterfaceGuid, IntPtr pReserved, out IntPtr ppProfileList);

    [DllImport("wlanapi.dll")]
    public static extern uint WlanSetInterface(SafeWlanHandle hClientHandle, in Guid pInterfaceGuid, int opCode, uint dwDataSize,
        in int pData, IntPtr pReserved);

    [DllImport("wlanapi.dll")]
    public static extern uint WlanRegisterNotification(SafeWlanHandle hClientHandle, uint dwNotifSource,
        [MarshalAs(UnmanagedType.Bool)] bool bIgnoreDuplicate, WLAN_NOTIFICATION_CALLBACK? funcCallback, IntPtr pCallbackContext,
        IntPtr pReserved, out uint pdwPrevNotifSource);

    [DllImport("wlanapi.dll")]
    public static extern void WlanFreeMemory(IntPtr pMemory);
}
