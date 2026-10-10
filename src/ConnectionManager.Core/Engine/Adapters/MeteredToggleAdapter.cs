using ConnectionManager.Core.Caps;
using ConnectionManager.Core.Network;

namespace ConnectionManager.Core.Engine;

/// The engine's view of MeteredToggle: what the status shows for the current Wi-Fi, and the SetMetered command.
/// Engine thread only, like the toggle.
public sealed class MeteredToggleAdapter : IMeteredToggle
{
    private readonly MeteredToggle _toggle;

    public MeteredToggleAdapter(MeteredToggle toggle) => _toggle = toggle;

    /// The profile lookup is cached per network, and the journal is read only on Wi-Fi with a profile found.
    public (bool CanMark, bool MarkedByUs) Describe(NetworkSnapshot network)
    {
        var profile = _toggle.ProfileFor(network);
        return (profile is not null, _toggle.IsMarkedByUs(profile));
    }

    public string? Set(NetworkSnapshot network, bool on)
    {
        if (on) return Done(_toggle.Mark(network), "Could not mark this network as metered.");
        if (_toggle.ProfileFor(network) is not { } profile) return "Connection Manager could not find the Wi-Fi profile for this network.";
        return Done(_toggle.Unmark(profile), "Could not make this network unmetered.");
    }

    private static string? Done(MeteredResult result, string fallback) => result.Ok ? null : result.Problem ?? fallback;
}
