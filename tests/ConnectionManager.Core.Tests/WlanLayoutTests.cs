using System.Runtime.InteropServices;
using ConnectionManager.Core.Wifi;

namespace ConnectionManager.Core.Tests;

/// Pins the 64-bit wlanapi.h layouts. The list structs are read by hand from a pointer, so a wrong size or offset
/// would read garbage.
public class WlanLayoutTests
{
    [Fact]
    public void Struct_sizes_match_the_headers()
    {
        Assert.Equal(532, Marshal.SizeOf<WLAN_INTERFACE_INFO>());
        Assert.Equal(516, Marshal.SizeOf<WLAN_PROFILE_INFO>());
        Assert.Equal(40, Marshal.SizeOf<WLAN_NOTIFICATION_DATA>());
    }

    [Fact]
    public void Offsets_match_the_headers()
    {
        Assert.Equal(16, (int)Marshal.OffsetOf<WLAN_INTERFACE_INFO>(nameof(WLAN_INTERFACE_INFO.strInterfaceDescription)));
        Assert.Equal(528, (int)Marshal.OffsetOf<WLAN_INTERFACE_INFO>(nameof(WLAN_INTERFACE_INFO.isState)));
        Assert.Equal(512, (int)Marshal.OffsetOf<WLAN_PROFILE_INFO>(nameof(WLAN_PROFILE_INFO.dwFlags)));
        Assert.Equal(8, (int)Marshal.OffsetOf<WLAN_NOTIFICATION_DATA>(nameof(WLAN_NOTIFICATION_DATA.InterfaceGuid)));
        Assert.Equal(32, (int)Marshal.OffsetOf<WLAN_NOTIFICATION_DATA>(nameof(WLAN_NOTIFICATION_DATA.pData)));
    }

    [Fact]
    public void List_items_start_after_the_two_counters()
    {
        Assert.Equal(WlanNative.ListItemsOffset, (int)Marshal.OffsetOf<WLAN_INTERFACE_INFO_LIST>(nameof(WLAN_INTERFACE_INFO_LIST.InterfaceInfo)));
        Assert.Equal(WlanNative.ListItemsOffset, (int)Marshal.OffsetOf<WLAN_PROFILE_INFO_LIST>(nameof(WLAN_PROFILE_INFO_LIST.ProfileInfo)));
    }

    [Fact]
    public void A_real_client_opens_and_closes_without_throwing_even_without_wifi()
    {
        using var client = new WlanClient();

        var open = client.Open();
        var (code, interfaces) = client.Interfaces();
        var register = client.RegisterConnectionCallback(_ => { });
        var unregister = client.Unregister();
        client.Close();
        client.Close();

        // On a PC without the WLAN service Open fails and every other call reports that it is not open.
        Assert.True(open == 0 || code != 0);
        Assert.True(open != 0 || (code == 0 && register == 0 && unregister == 0));
        Assert.Equal(WlanNative.ERROR_INVALID_HANDLE, client.SetBackgroundScan(Guid.NewGuid(), true)); // closed now
        Assert.NotNull(interfaces);
    }
}
