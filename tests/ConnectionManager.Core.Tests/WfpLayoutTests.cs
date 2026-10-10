using System.Runtime.InteropServices;
using ConnectionManager.Core.Wfp;

namespace ConnectionManager.Core.Tests;

/// Pins the 64-bit layouts worked out from the SDK headers. A wrong layout here would corrupt every WFP call.
public class WfpLayoutTests
{
    [Fact]
    public void Runs_as_a_64_bit_process() => Assert.True(Environment.Is64BitProcess);

    [Fact]
    public void Struct_sizes_match_the_headers()
    {
        Assert.Equal(16, Marshal.SizeOf<FWP_BYTE_BLOB>());
        Assert.Equal(16, Marshal.SizeOf<FWP_VALUE0>());
        Assert.Equal(16, Marshal.SizeOf<FWP_CONDITION_VALUE0>());
        Assert.Equal(40, Marshal.SizeOf<FWPM_FILTER_CONDITION0>());
        Assert.Equal(20, Marshal.SizeOf<FWPM_ACTION0>());
        Assert.Equal(72, Marshal.SizeOf<FWPM_SESSION0>());
        Assert.Equal(72, Marshal.SizeOf<FWPM_SUBLAYER0>());
        Assert.Equal(200, Marshal.SizeOf<FWPM_FILTER0>());
    }

    [Theory]
    [InlineData(nameof(FWPM_FILTER0.layerKey), 64)]
    [InlineData(nameof(FWPM_FILTER0.weight), 96)]
    [InlineData(nameof(FWPM_FILTER0.filterCondition), 120)]
    [InlineData(nameof(FWPM_FILTER0.action), 128)]
    [InlineData(nameof(FWPM_FILTER0.context), 152)]
    [InlineData(nameof(FWPM_FILTER0.filterId), 176)]
    public void Filter_offsets_match_the_headers(string field, int offset) =>
        Assert.Equal(offset, (int)Marshal.OffsetOf<FWPM_FILTER0>(field));

    [Fact]
    public void Session_condition_and_sublayer_offsets_match_the_headers()
    {
        Assert.Equal(48, (int)Marshal.OffsetOf<FWPM_SESSION0>(nameof(FWPM_SESSION0.sid)));
        Assert.Equal(64, (int)Marshal.OffsetOf<FWPM_SESSION0>(nameof(FWPM_SESSION0.kernelMode)));
        Assert.Equal(24, (int)Marshal.OffsetOf<FWPM_FILTER_CONDITION0>(nameof(FWPM_FILTER_CONDITION0.conditionValue)));
        Assert.Equal(8, (int)Marshal.OffsetOf<FWP_CONDITION_VALUE0>(nameof(FWP_CONDITION_VALUE0.uint16))); // port conditions
        Assert.Equal(64, (int)Marshal.OffsetOf<FWPM_SUBLAYER0>(nameof(FWPM_SUBLAYER0.weight)));
    }
}
