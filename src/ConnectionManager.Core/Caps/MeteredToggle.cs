using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using ConnectionManager.Core.Ipc;
using ConnectionManager.Core.Logging;
using ConnectionManager.Core.Network;
using ConnectionManager.Core.Wifi;

namespace ConnectionManager.Core.Caps;

/// Ok: the change was made, or there was nothing to undo. Problem: text for the status, which can come with Ok.
public sealed record MeteredResult(bool Ok, string? Problem = null);

/// Marks the current Wi-Fi network as metered, like "Metered connection" in Settings, so Windows itself holds back
/// updates and syncing on it, and puts it back. The profile is journaled before netsh runs, so a mark a crash leaves
/// behind is undone at the next start. Only profiles this class marked are ever unmarked. Never throws. One thread only.
public sealed class MeteredToggle
{
    public static readonly TimeSpan NetshTimeout = TimeSpan.FromSeconds(10);
    internal const int TimedOut = -1;

    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(2);

    private readonly IWlan _wlan;
    private readonly PolicyJournal _journal;
    private readonly ILog _log;
    private readonly Func<string, int> _runNetsh;
    private readonly HashSet<string> _logged = new(StringComparer.Ordinal);
    private NetworkSnapshot? _lookedUp;
    private string? _profile;

    /// wlan must be this toggle's own: it is opened and closed around each profile lookup. runNetsh takes netsh's
    /// argument string and returns its exit code; tests pass a fake so netsh never runs.
    public MeteredToggle(IWlan wlan, PolicyJournal journal, ILog log, Func<string, int>? runNetsh = null)
    {
        _wlan = wlan;
        _journal = journal;
        _log = log;
        _runNetsh = runNetsh ?? (args => RunNetsh(args, log));
    }

    /// The cost lives in the WLAN service, not in the profile XML, so netsh is the only way to set it.
    public static string NetshPath => Path.Combine(Environment.SystemDirectory, "netsh.exe");

    /// True on Wi-Fi when a WLAN profile with the network's name exists on a connected interface.
    public bool CanMark(NetworkSnapshot network) => ProfileFor(network) is not null;

    /// The WLAN profile behind the network, or null. Looked up once per network; a lookup WLAN could not answer is
    /// tried again next time.
    public string? ProfileFor(NetworkSnapshot network)
    {
        if (network.Kind != NetworkKind.Wifi || !IsValidProfileName(network.Name)) return null;
        if (network.Equals(_lookedUp)) return _profile;
        try
        {
            var (complete, profile) = FindProfile(network.Name, network.AdapterId);
            if (complete)
            {
                _lookedUp = network;
                _profile = profile;
            }
            return profile;
        }
        catch (Exception ex)
        {
            LogOnce($"lookup:{ex.Message}", () => _log.Error("Could not look up the Wi-Fi profile", ex));
            return null;
        }
    }

    /// True when the journal holds a mark of ours for this profile.
    public bool IsMarkedByUs(string? profile)
    {
        if (profile is null) return false;
        try
        {
            var load = _journal.Load();
            return load.Entries.Contains(new WlanCostEntry(profile));
        }
        catch (Exception ex)
        {
            LogOnce($"journal:{ex.Message}", () => _log.Error("Could not read the journal", ex));
            return false;
        }
    }

    /// Only for a Wi-Fi network Windows reports as unrestricted: journals the profile, then sets cost=Fixed.
    public MeteredResult Mark(NetworkSnapshot network)
    {
        try
        {
            if (network.Kind != NetworkKind.Wifi) return Refused("Only a Wi-Fi network can be marked as metered.");
            if (network.Cost != NetworkCost.Unrestricted)
                return Refused(network.Metered ? "This network is already metered."
                    : "Windows has not reported whether this network is metered yet, so it was left as it is.");
            if (ProfileFor(network) is not { } profile) return Refused("Connection Manager could not find the Wi-Fi profile for this network.");

            var load = _journal.Load();
            if (load.Unreadable) return Refused(load.Problem); // saving would lose what the journal holds
            var entry = new WlanCostEntry(profile);
            var journaled = load.Entries.Contains(entry);
            if (!journaled) _journal.Save([.. load.Entries, entry]);

            if (Run(profile, metered: true))
            {
                _log.Info($"Marked the Wi-Fi network {profile} as metered");
                return new MeteredResult(true, load.Problem);
            }
            if (!journaled) Forget(profile);
            return Refused(Join(load.Problem, $"Windows did not accept marking {profile} as metered."));
        }
        catch (Exception ex)
        {
            _log.Error("Could not mark the Wi-Fi network as metered", ex);
            return Refused($"Could not mark this network as metered: {ex.Message}");
        }
    }

    /// Sets cost=Default on a profile this class marked, then forgets it. A profile it did not mark is left alone.
    public MeteredResult Unmark(string profile)
    {
        try
        {
            var load = _journal.Load();
            if (load.Unreadable) return Refused(load.Problem);
            if (!IsValidProfileName(profile) || !load.Entries.Contains(new WlanCostEntry(profile)))
                return Refused("Connection Manager did not mark this network as metered, so it left it as it is.");
            if (!Undo(profile)) return Refused(Join(load.Problem, $"Windows did not accept making {profile} unmetered again."));
            Forget(profile);
            return new MeteredResult(true, load.Problem);
        }
        catch (Exception ex)
        {
            _log.Error("Could not make the Wi-Fi network unmetered", ex);
            return Refused($"Could not make this network unmetered: {ex.Message}");
        }
    }

    /// Unmarks every journaled profile, at service start and stop and on "Restore everything". Entries that fail stay
    /// journaled for the next try; other features' entries are kept as they are.
    public MeteredResult RestoreAll()
    {
        try
        {
            var load = _journal.Load();
            if (load.Unreadable) return Refused(load.Problem);
            var ours = load.Entries.OfType<WlanCostEntry>().Select(e => e.Profile).Distinct(StringComparer.Ordinal).ToList();
            if (ours.Count == 0) return new MeteredResult(true, load.Problem);

            var kept = new List<string>();
            foreach (var profile in ours)
            {
                if (!IsValidProfileName(profile))
                {
                    _log.Warn("The journal lists a Wi-Fi profile name that is not safe to pass to netsh; ignored it");
                    continue;
                }
                if (!Undo(profile)) kept.Add(profile);
            }
            _journal.Save([.. load.Entries.Where(e => e is not WlanCostEntry), .. kept.Select(p => new WlanCostEntry(p))]);
            if (kept.Count == 0)
            {
                _log.Info("Metered Wi-Fi networks restored");
                return new MeteredResult(true, load.Problem);
            }
            return Refused(Join(load.Problem,
                $"Could not make these Wi-Fi networks unmetered again: {string.Join(", ", kept)}. Connection Manager tries again at its next start."));
        }
        catch (Exception ex)
        {
            _log.Error("Could not restore the metered Wi-Fi networks", ex);
            return Refused($"Could not make the Wi-Fi networks Connection Manager marked unmetered again: {ex.Message}");
        }
    }

    /// Not empty, at most 256 characters, and no quote or control character, so it can never end netsh's quoted value
    /// or smuggle in another parameter.
    public static bool IsValidProfileName([NotNullWhen(true)] string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= IpcLimits.MaxName && !name.Any(c => c == '"' || char.IsControl(c));

    /// netsh's documented form, name="<profile>", as one string rather than ArgumentList. ArgumentList would send
    /// "name=<profile>" quoted as a whole, which is not the documented syntax; a single string keeps it, and lets the
    /// injected runner be checked against the exact command line. This is safe because the name is validated: with no
    /// quote in it, it cannot close the quoted value. netsh splits its command line with the C runtime's rules, where
    /// backslashes are literal except right before a quote, so a trailing run of them is doubled to keep the closing
    /// quote a quote.
    public static string BuildArguments(string profile, bool metered)
    {
        if (!IsValidProfileName(profile)) throw new ArgumentException("The Wi-Fi profile name is not safe to pass to netsh", nameof(profile));
        var trailing = profile.Length - profile.TrimEnd('\\').Length;
        return $"wlan set profileparameter name=\"{profile}{new string('\\', trailing)}\" cost={(metered ? "Fixed" : "Default")}";
    }

    /// Runs System32's netsh without a shell or a window and returns its exit code, TimedOut after 10 s (it is then
    /// killed). The output is drained so a full pipe cannot stall it, but never parsed: it is localized.
    internal static int RunNetsh(string arguments, ILog log)
    {
        var start = new ProcessStartInfo(NetshPath, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("netsh did not start");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(NetshTimeout))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                // It exited on its own just now.
            }
            log.Warn($"netsh did not finish within {NetshTimeout.TotalSeconds} s and was stopped");
            Drain(output, error);
            return TimedOut;
        }
        Drain(output, error);
        return process.ExitCode;
    }

    // Complete is false when WLAN could not answer, so the result is not cached.
    private (bool Complete, string? Profile) FindProfile(string name, Guid? adapterId)
    {
        var opened = _wlan.Open();
        if (opened != 0)
        {
            if (!WlanNative.IsNoWlan(opened)) LogOnce($"open:{opened}", () => _log.Warn($"Could not open the WLAN service to find the Wi-Fi profile (error {opened})"));
            return (false, null);
        }
        try
        {
            var (listed, interfaces) = _wlan.Interfaces();
            if (listed != 0) return (false, null);
            // The adapter NLM reported first; its WLAN interface has the same GUID.
            foreach (var iface in interfaces.Where(i => i.Connected).OrderBy(i => i.Id == adapterId ? 0 : 1))
            {
                var (code, names) = _wlan.ProfileNames(iface.Id);
                if (code != 0) return (false, null);
                if (names.Contains(name, StringComparer.Ordinal)) return (true, name);
            }
            return (true, null);
        }
        finally
        {
            _wlan.Close();
        }
    }

    // netsh exits 0 even when the profile is not found, so a 0 alone proves little: callers check that the profile
    // exists first, and the network's cost in the next NLM read is the real confirmation. Non-zero is a sure failure.
    private bool Run(string profile, bool metered)
    {
        var arguments = BuildArguments(profile, metered);
        int exit;
        try
        {
            exit = _runNetsh(arguments);
        }
        catch (Exception ex)
        {
            _log.Error($"Could not run netsh {arguments}", ex);
            return false;
        }
        if (exit != 0) _log.Warn($"netsh {arguments} exited with {exit}");
        return exit == 0;
    }

    // True when the profile is back to its default cost, or is gone: a profile the user forgot has no cost left to undo.
    private bool Undo(string profile)
    {
        if (Run(profile, metered: false)) return true;
        if (!ProfileGone(profile)) return false;
        _log.Info($"The Wi-Fi profile {profile} no longer exists, so there is no metered setting left to undo");
        return true;
    }

    // Only a definite answer counts: with no adapter present (a USB one unplugged) or WLAN failing, the profile may
    // still exist, so the journal entry is kept.
    private bool ProfileGone(string profile)
    {
        try
        {
            if (_wlan.Open() != 0) return false;
            try
            {
                var (listed, interfaces) = _wlan.Interfaces();
                if (listed != 0 || interfaces.Count == 0) return false;
                foreach (var iface in interfaces)
                {
                    var (code, names) = _wlan.ProfileNames(iface.Id);
                    if (code != 0 || names.Contains(profile, StringComparer.Ordinal)) return false;
                }
                return true;
            }
            finally
            {
                _wlan.Close();
            }
        }
        catch (Exception ex)
        {
            _log.Error("Could not check the Wi-Fi profiles", ex);
            return false;
        }
    }

    // After a failed netsh nothing changed, so the entry goes. If that save fails the entry stays, and the next
    // restore sets cost=Default on a profile that already has it, which is harmless.
    private void Forget(string profile)
    {
        try
        {
            var load = _journal.Load();
            if (load.Unreadable) return;
            var entry = new WlanCostEntry(profile);
            _journal.Save(load.Entries.Where(e => !e.Equals(entry)).ToList());
        }
        catch (Exception ex)
        {
            _log.Error($"Could not remove {profile} from the journal", ex);
        }
    }

    private static void Drain(Task output, Task error)
    {
        try
        {
            Task.WaitAll([output, error], DrainTimeout);
        }
        catch (AggregateException)
        {
            // The text is not used; only a reader left faulted and unobserved is avoided.
        }
    }

    private void LogOnce(string key, Action log)
    {
        if (_logged.Add(key)) log();
    }

    private static MeteredResult Refused(string? why) => new(false, why);

    private static string? Join(string? first, string second) => first is null ? second : $"{first} {second}";
}
