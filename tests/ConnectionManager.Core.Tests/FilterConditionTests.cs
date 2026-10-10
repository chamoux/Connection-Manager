using System.Runtime.InteropServices;
using ConnectionManager.Core.Wfp;

namespace ConnectionManager.Core.Tests;

/// The condition arrays WFP gets for each rule kind. Port and All rules need no engine, so they run unelevated.
public class FilterConditionTests
{
    private static FilterRule Rule(RuleKind kind, string value) => new(kind, value, RuleAction.Permit, RuleWeight.EssentialPermit, "test");

    [Fact]
    public void A_remote_port_is_one_uint16_condition()
    {
        using var condition = FilterCondition.For(Rule(RuleKind.RemotePort, "53"));

        var only = Assert.Single(condition.Values);
        Assert.Equal(WfpNative.ConditionIpRemotePort, only.fieldKey);
        Assert.Equal(WfpNative.MatchEqual, only.matchType);
        Assert.Equal(WfpNative.DataUint16, only.conditionValue.type);
        Assert.Equal(53, only.conditionValue.uint16);
    }

    [Fact]
    public void A_port_with_a_protocol_adds_a_uint8_protocol_condition()
    {
        using var condition = FilterCondition.For(Rule(RuleKind.LocalPort, "68/udp"));

        Assert.Equal(2, condition.Values.Length);
        Assert.Equal(WfpNative.ConditionIpLocalPort, condition.Values[0].fieldKey);
        Assert.Equal(68, condition.Values[0].conditionValue.uint16);
        Assert.Equal(WfpNative.ConditionIpProtocol, condition.Values[1].fieldKey);
        Assert.Equal(WfpNative.DataUint8, condition.Values[1].conditionValue.type);
        Assert.Equal(17, condition.Values[1].conditionValue.uint8);
    }

    [Fact]
    public void All_has_no_conditions()
    {
        using var condition = FilterCondition.For(Rule(RuleKind.All, ""));

        Assert.Empty(condition.Values);
    }

    [Theory]
    [InlineData(RuleKind.RemotePort, "dns")]
    [InlineData(RuleKind.LocalPort, "0")]
    [InlineData(RuleKind.All, "anything")]
    public void Malformed_rules_are_refused(RuleKind kind, string value) =>
        Assert.Throws<ArgumentException>(() => FilterCondition.For(Rule(kind, value)));

    [Fact]
    public void Conditions_are_written_as_one_contiguous_array()
    {
        using var condition = FilterCondition.For(Rule(RuleKind.RemotePort, "443/tcp"));
        var array = WfpEngine.AllocConditions(condition.Values);
        try
        {
            var second = Marshal.PtrToStructure<FWPM_FILTER_CONDITION0>(array + 40); // sizeof(FWPM_FILTER_CONDITION0) on x64
            Assert.Equal(WfpNative.ConditionIpProtocol, second.fieldKey);
            Assert.Equal(6, second.conditionValue.uint8);
            Assert.Equal(443, Marshal.PtrToStructure<FWPM_FILTER_CONDITION0>(array).conditionValue.uint16);
        }
        finally
        {
            Marshal.FreeHGlobal(array);
        }
    }

    [Fact]
    public void No_conditions_is_a_null_array() => Assert.Equal(IntPtr.Zero, WfpEngine.AllocConditions([]));

    [Fact]
    public void An_app_id_built_from_the_path_equals_the_one_WFP_makes()
    {
        var curl = Path.Combine(Environment.SystemDirectory, "curl.exe");

        Assert.Equal(WfpAppId(curl), FilterCondition.AppIdFromDosPath(curl));
    }

    [Fact]
    public void A_program_that_refuses_to_be_opened_still_gets_its_app_id()
    {
        using var tree = new TempTree();
        var exe = tree.Touch(@"Guarded App\guarded.exe");
        var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var deny = new System.Security.AccessControl.FileSystemAccessRule(me,
            System.Security.AccessControl.FileSystemRights.FullControl, // even attribute reads, like Kaspersky's self-protection
            System.Security.AccessControl.AccessControlType.Deny);
        var file = new FileInfo(exe);
        var security = file.GetAccessControl();
        security.AddAccessRule(deny);
        file.SetAccessControl(security);
        try
        {
            Assert.Equal(5u, WfpNative.FwpmGetAppIdFromFileName0(exe, out _)); // what Kaspersky's avp.exe does

            using var condition = FilterCondition.ForApp(exe);

            var only = Assert.Single(condition.Values);
            Assert.Equal(WfpNative.ConditionAleAppId, only.fieldKey);
            Assert.Equal(WfpNative.DataByteBlob, only.conditionValue.type);
            var blob = Marshal.PtrToStructure<FWP_BYTE_BLOB>(only.conditionValue.pointer);
            var bytes = new byte[blob.size];
            Marshal.Copy(blob.data, bytes, 0, bytes.Length);
            Assert.Equal(FilterCondition.AppIdFromDosPath(exe), bytes);
        }
        finally
        {
            security.RemoveAccessRule(deny);
            file.SetAccessControl(security);
        }
    }

    [Fact]
    public void A_missing_program_still_fails()
    {
        Assert.Throws<WfpException>(() => FilterCondition.ForApp(Path.Combine(Path.GetTempPath(), "cm-no-such-dir", "missing.exe")));
    }

    [Theory]
    [InlineData(@"\\server\share\a.exe")]
    [InlineData(@"relative\a.exe")]
    [InlineData(@"C:")]
    public void Only_drive_paths_get_a_built_app_id(string path) => Assert.Null(FilterCondition.AppIdFromDosPath(path));

    private static byte[] WfpAppId(string path)
    {
        Assert.Equal(0u, WfpNative.FwpmGetAppIdFromFileName0(path, out var pointer));
        try
        {
            var blob = Marshal.PtrToStructure<FWP_BYTE_BLOB>(pointer);
            var bytes = new byte[blob.size];
            Marshal.Copy(blob.data, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            WfpNative.FwpmFreeMemory0(ref pointer);
        }
    }
}
