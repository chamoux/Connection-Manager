using ConnectionManager.Core.Wifi;

namespace ConnectionManager.Core.Tests;

/// Records every WLAN call in order. Interfaces and profiles are set by the test; codes can be made to fail.
internal sealed class FakeWlan : IWlan
{
    public List<string> Calls { get; } = [];
    public List<WlanInterface> Adapters { get; } = [];
    public Dictionary<Guid, List<string>> Profiles { get; } = [];
    public uint OpenCode { get; set; }
    public uint EnumCode { get; set; }
    public uint RegisterCode { get; set; }
    public uint ScanCode { get; set; }
    public uint StreamingCode { get; set; }
    public bool IsOpen { get; private set; }
    public int Opens { get; private set; }
    public Action<Guid>? Callback { get; private set; }

    public uint Open()
    {
        Calls.Add("open");
        if (OpenCode != 0) return OpenCode;
        Opens++;
        IsOpen = true;
        return 0;
    }

    public (uint Code, IReadOnlyList<WlanInterface> Interfaces) Interfaces()
    {
        Calls.Add("interfaces");
        return EnumCode != 0 ? (EnumCode, []) : (0, Adapters.ToList());
    }

    public uint SetBackgroundScan(Guid iface, bool enabled)
    {
        Calls.Add($"scan {Name(iface)} {(enabled ? "on" : "off")}");
        return ScanCode;
    }

    public uint SetStreamingMode(Guid iface, bool enabled)
    {
        Calls.Add($"streaming {Name(iface)} {(enabled ? "on" : "off")}");
        return StreamingCode;
    }

    public uint RegisterConnectionCallback(Action<Guid> onConnected)
    {
        Calls.Add("register");
        if (RegisterCode != 0) return RegisterCode;
        Callback = onConnected;
        return 0;
    }

    public uint Unregister()
    {
        Calls.Add("unregister");
        Callback = null;
        return 0;
    }

    public (uint Code, IReadOnlyList<string> Names) ProfileNames(Guid iface)
    {
        Calls.Add($"profiles {Name(iface)}");
        return (0, Profiles.TryGetValue(iface, out var names) ? names.ToList() : []);
    }

    public void Close()
    {
        Calls.Add("close");
        IsOpen = false;
        Callback = null;
    }

    public static readonly Guid A = new("aaaaaaaa-0000-0000-0000-000000000001");
    public static readonly Guid B = new("bbbbbbbb-0000-0000-0000-000000000002");

    private static string Name(Guid id) => id == A ? "A" : id == B ? "B" : id.ToString();
}
