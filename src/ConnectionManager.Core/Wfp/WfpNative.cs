using System.Runtime.InteropServices;

namespace ConnectionManager.Core.Wfp;

// Windows Filtering Platform declarations, checked against fwpmu.h, fwpmtypes.h and fwptypes.h from SDK 10.0.26100.
// The layouts are for 64-bit processes only. WfpLayoutTests pins the struct sizes and key offsets, which were also
// checked against an MSVC build of the SDK headers. All structs are blittable.

[StructLayout(LayoutKind.Sequential)]
internal struct FWPM_DISPLAY_DATA0
{
    public IntPtr name;        // wchar_t*
    public IntPtr description; // wchar_t*
}

[StructLayout(LayoutKind.Sequential)]
internal struct FWP_BYTE_BLOB
{
    public uint size;
    public IntPtr data;
}

[StructLayout(LayoutKind.Explicit)]
internal struct FWP_VALUE0
{
    [FieldOffset(0)] public uint type;
    [FieldOffset(8)] public byte uint8;
    [FieldOffset(8)] public uint uint32;
    [FieldOffset(8)] public IntPtr pointer;
}

[StructLayout(LayoutKind.Explicit)]
internal struct FWP_CONDITION_VALUE0
{
    [FieldOffset(0)] public uint type;
    [FieldOffset(8)] public byte uint8;
    [FieldOffset(8)] public ushort uint16;
    [FieldOffset(8)] public uint uint32;
    [FieldOffset(8)] public IntPtr pointer;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FWPM_FILTER_CONDITION0
{
    public Guid fieldKey;
    public uint matchType;
    public FWP_CONDITION_VALUE0 conditionValue;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FWPM_ACTION0
{
    public uint type;
    public Guid filterTypeOrCalloutKey;
}

// The anonymous union { UINT64 rawContext; GUID providerContextKey; } inside FWPM_FILTER0.
[StructLayout(LayoutKind.Explicit)]
internal struct FWPM_FILTER_CONTEXT
{
    [FieldOffset(0)] public ulong rawContext;
    [FieldOffset(0)] public Guid providerContextKey;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FWPM_FILTER0
{
    public Guid filterKey;
    public FWPM_DISPLAY_DATA0 displayData;
    public uint flags;
    public IntPtr providerKey;
    public FWP_BYTE_BLOB providerData;
    public Guid layerKey;
    public Guid subLayerKey;
    public FWP_VALUE0 weight;
    public uint numFilterConditions;
    public IntPtr filterCondition;
    public FWPM_ACTION0 action;
    public FWPM_FILTER_CONTEXT context;
    public IntPtr reserved;
    public ulong filterId;
    public FWP_VALUE0 effectiveWeight;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FWPM_SESSION0
{
    public Guid sessionKey;
    public FWPM_DISPLAY_DATA0 displayData;
    public uint flags;
    public uint txnWaitTimeoutInMSec;
    public uint processId;
    public IntPtr sid;
    public IntPtr username;
    public int kernelMode;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FWPM_SUBLAYER0
{
    public Guid subLayerKey;
    public FWPM_DISPLAY_DATA0 displayData;
    public uint flags;
    public IntPtr providerKey;
    public FWP_BYTE_BLOB providerData;
    public ushort weight;
}

internal static class WfpNative
{
    public const uint RpcCAuthnWinNt = 10;            // RPC_C_AUTHN_WINNT
    public const uint SessionFlagDynamic = 0x1;       // FWPM_SESSION_FLAG_DYNAMIC

    public const uint DataUint8 = 1;                  // FWP_UINT8
    public const uint DataUint16 = 2;                 // FWP_UINT16
    public const uint DataUint32 = 3;                 // FWP_UINT32
    public const uint DataByteBlob = 12;              // FWP_BYTE_BLOB_TYPE
    public const uint DataSecurityDescriptor = 14;    // FWP_SECURITY_DESCRIPTOR_TYPE

    public const uint MatchEqual = 0;                 // FWP_MATCH_EQUAL
    public const uint MatchFlagsAllSet = 6;           // FWP_MATCH_FLAGS_ALL_SET

    public const uint ActionBlock = 0x1001;           // FWP_ACTION_BLOCK
    public const uint ActionPermit = 0x1002;          // FWP_ACTION_PERMIT
    public const uint ConditionFlagIsLoopback = 0x1;  // FWP_CONDITION_FLAG_IS_LOOPBACK

    public const uint ErrorFilterNotFound = 0x80320003; // FWP_E_FILTER_NOT_FOUND
    public const uint ErrorAccessDenied = 5;            // ERROR_ACCESS_DENIED, as FwpmGetAppIdFromFileName0 returns it

    public static readonly Guid LayerAleAuthConnectV4 = new("c38d57d1-05a7-4c33-904f-7fbceee60e82");
    public static readonly Guid LayerAleAuthConnectV6 = new("4a72393b-319f-44bc-84c3-ba54dcb3b6b4");
    public static readonly Guid LayerAleAuthRecvAcceptV4 = new("e1cd9fe7-f4b5-4273-96c0-592e487b8650");
    public static readonly Guid LayerAleAuthRecvAcceptV6 = new("a3b42c97-9f04-4672-b87e-cee9c483257f");

    public static readonly Guid ConditionAleAppId = new("d78e1e87-8644-4ea5-9437-d809ecefc971");
    public static readonly Guid ConditionAleUserId = new("af043a0a-b34d-4f86-979c-c90371af6e66");
    public static readonly Guid ConditionFlags = new("632ce23b-5167-435c-86d7-e903684aa80c");
    public static readonly Guid ConditionIpProtocol = new("3971ef2b-623e-4f9a-8cb1-6e79b806b9a7");   // FWP_UINT8
    public static readonly Guid ConditionIpLocalPort = new("0c1ba1af-5765-453f-af22-a8f791ac775b");  // FWP_UINT16
    public static readonly Guid ConditionIpRemotePort = new("c35a604d-d22b-4e1a-91b4-68f674ee674b"); // FWP_UINT16

    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode)]
    public static extern uint FwpmEngineOpen0(string? serverName, uint authnService, IntPtr authIdentity, ref FWPM_SESSION0 session, out IntPtr engineHandle);

    [DllImport("fwpuclnt.dll")]
    public static extern uint FwpmEngineClose0(IntPtr engineHandle);

    [DllImport("fwpuclnt.dll")]
    public static extern uint FwpmTransactionBegin0(IntPtr engineHandle, uint flags);

    [DllImport("fwpuclnt.dll")]
    public static extern uint FwpmTransactionCommit0(IntPtr engineHandle);

    [DllImport("fwpuclnt.dll")]
    public static extern uint FwpmTransactionAbort0(IntPtr engineHandle);

    [DllImport("fwpuclnt.dll")]
    public static extern uint FwpmSubLayerAdd0(IntPtr engineHandle, ref FWPM_SUBLAYER0 subLayer, IntPtr sd);

    [DllImport("fwpuclnt.dll")]
    public static extern uint FwpmFilterAdd0(IntPtr engineHandle, ref FWPM_FILTER0 filter, IntPtr sd, out ulong id);

    [DllImport("fwpuclnt.dll")]
    public static extern uint FwpmFilterDeleteById0(IntPtr engineHandle, ulong id);

    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode)]
    public static extern uint FwpmGetAppIdFromFileName0(string fileName, out IntPtr appId);

    [DllImport("fwpuclnt.dll")]
    public static extern void FwpmFreeMemory0(ref IntPtr p);
}
