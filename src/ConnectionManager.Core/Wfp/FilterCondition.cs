using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace ConnectionManager.Core.Wfp;

/// The conditions of one filter plus the unmanaged memory they point to. Dispose after the filters are added (WFP copies
/// the data). Conditions on different fields must all match.
internal sealed class FilterCondition : IDisposable
{
    private IntPtr _wfpBlob;    // from FwpmGetAppIdFromFileName0, freed with FwpmFreeMemory0
    private IntPtr _blobStruct; // our own FWP_BYTE_BLOB
    private IntPtr _bytes;      // our own blob bytes (a security descriptor, or an app id built from a path)

    public FWPM_FILTER_CONDITION0[] Values { get; private set; } = [];

    public static FilterCondition For(FilterRule rule) => rule.Kind switch
    {
        RuleKind.App => ForApp(rule.Value),
        RuleKind.Service => ForService(rule.Value),
        RuleKind.RemotePort => ForPort(WfpNative.ConditionIpRemotePort, rule.Value),
        RuleKind.LocalPort => ForPort(WfpNative.ConditionIpLocalPort, rule.Value),
        RuleKind.All when rule.Value.Length == 0 => new FilterCondition(), // no conditions: every connection matches
        _ => throw new ArgumentException($"Not a valid {rule.Kind} rule: '{rule.Value}'", nameof(rule)),
    };

    /// Matches connections made by one program file. The path has already passed SafePaths: a local drive, no links.
    public static FilterCondition ForApp(string path)
    {
        var code = WfpNative.FwpmGetAppIdFromFileName0(path, out var blob);
        // It opens the file, and some programs refuse that even to admins (Kaspersky's self-protection guards avp.exe).
        // The app id is only the file's device path, so build it from the path instead.
        if (code == WfpNative.ErrorAccessDenied && File.Exists(path) && AppIdFromDosPath(path) is { } appId)
            return FromBytes(WfpNative.ConditionAleAppId, WfpNative.DataByteBlob, appId);
        WfpEngine.Check("FwpmGetAppIdFromFileName0", code);
        return new FilterCondition
        {
            _wfpBlob = blob,
            Values = [Condition(WfpNative.ConditionAleAppId, new FWP_CONDITION_VALUE0 { type = WfpNative.DataByteBlob, pointer = blob })],
        };
    }

    /// What FwpmGetAppIdFromFileName0 returns, without opening the file: the NT device path in lowercase, UTF-16, with
    /// its terminating NUL ("C:\a.exe" becomes "\device\harddiskvolume3\a.exe"). Null for anything but a drive path.
    internal static byte[]? AppIdFromDosPath(string path)
    {
        if (path.Length < 4 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\') return null;
        var device = new char[1024];
        var length = QueryDosDevice(path[..2], device, device.Length);
        if (length == 0) return null;
        var end = Array.IndexOf(device, '\0');
        var target = new string(device, 0, end >= 0 ? end : (int)length);
        if (!target.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase)) return null; // a SUBST drive maps to \??\C:\...
        return Encoding.Unicode.GetBytes((target + path[2..]).ToLowerInvariant() + "\0");
    }

    /// Matches connections made by a Windows service, through its per-service SID (NT SERVICE\name).
    public static FilterCondition ForService(string serviceName) =>
        ForSid((SecurityIdentifier)new NTAccount("NT SERVICE", serviceName).Translate(typeof(SecurityIdentifier)));

    /// The engine runs an access check of the connecting process's token against this descriptor. CC is the
    /// FWP_ACTRL_MATCH_FILTER right, so the filter matches processes whose token holds the SID.
    /// AccessCheck needs an owner and a group, hence O:SY G:SY.
    public static FilterCondition ForSid(SecurityIdentifier sid)
    {
        var descriptor = new RawSecurityDescriptor($"O:SYG:SYD:(A;;CC;;;{sid.Value})");
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);

        return FromBytes(WfpNative.ConditionAleUserId, WfpNative.DataSecurityDescriptor, bytes);
    }

    // One condition whose value is a blob of our own bytes.
    private static FilterCondition FromBytes(Guid field, uint type, byte[] bytes)
    {
        var condition = new FilterCondition
        {
            _bytes = Marshal.AllocHGlobal(bytes.Length),
            _blobStruct = Marshal.AllocHGlobal(Marshal.SizeOf<FWP_BYTE_BLOB>()),
        };
        Marshal.Copy(bytes, 0, condition._bytes, bytes.Length);
        Marshal.StructureToPtr(new FWP_BYTE_BLOB { size = (uint)bytes.Length, data = condition._bytes }, condition._blobStruct, false);
        condition.Values = [Condition(field, new FWP_CONDITION_VALUE0 { type = type, pointer = condition._blobStruct })];
        return condition;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string deviceName, char[] targetPath, int max);

    /// A local or remote port, plus the protocol when the value names one ("68/udp").
    private static FilterCondition ForPort(Guid field, string value)
    {
        if (!FilterRule.TryParsePort(value, out var port, out var protocol)) throw new ArgumentException($"Not a valid port: '{value}'", nameof(value));
        var portCondition = Condition(field, new FWP_CONDITION_VALUE0 { type = WfpNative.DataUint16, uint16 = port });
        return new FilterCondition
        {
            Values = protocol == 0
                ? [portCondition]
                : [portCondition, Condition(WfpNative.ConditionIpProtocol, new FWP_CONDITION_VALUE0 { type = WfpNative.DataUint8, uint8 = protocol })],
        };
    }

    private static FWPM_FILTER_CONDITION0 Condition(Guid field, FWP_CONDITION_VALUE0 value) =>
        new() { fieldKey = field, matchType = WfpNative.MatchEqual, conditionValue = value };

    public void Dispose()
    {
        if (_wfpBlob != IntPtr.Zero) WfpNative.FwpmFreeMemory0(ref _wfpBlob);
        if (_blobStruct != IntPtr.Zero) Marshal.FreeHGlobal(_blobStruct);
        if (_bytes != IntPtr.Zero) Marshal.FreeHGlobal(_bytes);
        _blobStruct = IntPtr.Zero;
        _bytes = IntPtr.Zero;
    }
}
