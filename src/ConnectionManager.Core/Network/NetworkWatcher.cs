using System.Net;
using System.Net.NetworkInformation;
using ConnectionManager.Core.Engine;
using ConnectionManager.Core.Logging;

namespace ConnectionManager.Core.Network;

/// Tracks the network the PC is on. Windows' change events only mark the snapshot stale; Refresh does the reading, on
/// the caller's thread, when something changed or every 15 s. Never throws: when a read fails it keeps the last
/// snapshot. Call Refresh from one thread; Current can be read from any.
public sealed class NetworkWatcher : INetworkSource, IDisposable
{
    public static readonly TimeSpan RereadEvery = TimeSpan.FromSeconds(15);

    private const int MaxAdapters = 64;
    private const int MaxGateways = 8;

    private readonly TimeProvider _time;
    private readonly ILog _log;
    private readonly Func<IReadOnlyList<AdapterFacts>> _readAdapters;
    private readonly Func<Guid, (string? Name, NetworkCost Cost)> _readNetwork;
    private volatile bool _dirty = true;
    private volatile NetworkSnapshot _current = NetworkSnapshot.None;
    private DateTimeOffset? _readAt;
    private string? _lastError;
    private bool _nlmMissing;
    private bool _subscribed;

    public NetworkWatcher(ILog log, TimeProvider? time = null)
        : this(time ?? TimeProvider.System, log, ReadAdapters, null, subscribe: true)
    {
    }

    /// The seams are for tests: readAdapters lists the adapters, readNetwork gives an adapter's name and cost.
    internal NetworkWatcher(TimeProvider time, ILog log, Func<IReadOnlyList<AdapterFacts>> readAdapters,
        Func<Guid, (string? Name, NetworkCost Cost)>? readNetwork, bool subscribe)
    {
        _time = time;
        _log = log;
        _readAdapters = readAdapters;
        _readNetwork = readNetwork ?? ReadNetwork;
        if (!subscribe) return;
        _subscribed = true;
        try
        {
            NetworkChange.NetworkAddressChanged += OnChanged;
            NetworkChange.NetworkAvailabilityChanged += OnChanged;
        }
        catch (Exception ex) // without the events the 15 s re-read still notices changes, only later
        {
            Dispose();
            _log.Warn($"Network change events are unavailable, checking every {RereadEvery.TotalSeconds} s: {ex.Message}");
        }
    }

    public NetworkSnapshot Current => _current;

    public bool Refresh() => Refresh(_time.GetUtcNow());

    /// Re-reads when something changed or the last read is 15 s old. True when the snapshot changed.
    public bool Refresh(DateTimeOffset now)
    {
        if (!_dirty && _readAt is { } readAt && now >= readAt && now - readAt < RereadEvery) return false;
        _dirty = false; // cleared before reading, so a change during the read causes another one
        _readAt = now;
        try
        {
            var next = Read();
            _lastError = null;
            if (next.Equals(_current)) return false;
            _current = next;
            return true;
        }
        catch (Exception ex) // the service loop must not stop because a read failed
        {
            if (ex.Message != _lastError) _log.Error("Could not read the network state", ex);
            _lastError = ex.Message;
            return false;
        }
    }

    /// Marks the snapshot stale. Windows' change events call this on their own thread, so it does nothing else.
    internal void MarkDirty() => _dirty = true;

    private void OnChanged(object? sender, EventArgs e) => _dirty = true;

    private NetworkSnapshot Read()
    {
        var (kind, adapter) = AdapterSelector.Select(_readAdapters());
        if (adapter is null) return NetworkSnapshot.None;
        var (name, cost) = _readNetwork(adapter.Id);
        return new NetworkSnapshot(kind, adapter.Id, name, adapter.Gateways, cost, adapter.IfIndex);
    }

    // NLM gives the name and this connection's cost. Windows' connectivity hint is the cost fallback.
    private (string? Name, NetworkCost Cost) ReadNetwork(Guid adapterId)
    {
        var found = NlmInterop.TryRead(adapterId, out var name, out var cost);
        if (!found && !_nlmMissing) _log.Warn($"Network List Manager has no data for adapter {adapterId}; using the cost hint");
        _nlmMissing = !found;
        return (name, cost == NetworkCost.Unknown ? NlmInterop.HintCost() : cost);
    }

    private static List<AdapterFacts> ReadAdapters()
    {
        var adapters = new List<AdapterFacts>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Take(MaxAdapters))
        {
            if (!Guid.TryParse(nic.Id, out var id)) continue;
            var type = nic.NetworkInterfaceType;
            if (nic.OperationalStatus != OperationalStatus.Up || AdapterSelector.KindOf(type) == NetworkKind.None)
            {
                adapters.Add(new AdapterFacts(type, nic.OperationalStatus == OperationalStatus.Up, id, []));
                continue;
            }
            var ip = nic.GetIPProperties();
            var gateways = ip.GatewayAddresses.Select(g => g.Address)
                .Where(a => !a.Equals(IPAddress.Any) && !a.Equals(IPAddress.IPv6Any))
                .Select(a => a.ToString()).Take(MaxGateways).ToList();
            var ifIndex = nic.Supports(NetworkInterfaceComponent.IPv4) ? ip.GetIPv4Properties().Index
                : nic.Supports(NetworkInterfaceComponent.IPv6) ? ip.GetIPv6Properties().Index : 0;
            adapters.Add(new AdapterFacts(type, true, id, gateways, ifIndex));
        }
        return adapters;
    }

    public void Dispose()
    {
        if (!_subscribed) return;
        _subscribed = false;
        NetworkChange.NetworkAddressChanged -= OnChanged; // removing a handler that was never added does nothing
        NetworkChange.NetworkAvailabilityChanged -= OnChanged;
    }
}
