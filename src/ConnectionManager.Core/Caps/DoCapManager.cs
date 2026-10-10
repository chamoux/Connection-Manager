using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using ConnectionManager.Core.Logging;
using Microsoft.Win32;

namespace ConnectionManager.Core.Caps;

/// Applied: at least one cap of ours is in place. Skipped: values a policy (GPO or MDM) already sets, left alone.
public sealed record ApplyResult(bool Applied, IReadOnlyList<string> Skipped, string? Problem);

/// Complete: nothing of ours is left to undo. LeftAlone: values someone else changed after we set them.
public sealed record RestoreResult(bool Complete, IReadOnlyList<string> LeftAlone, string? Problem);

/// Caps Windows' own downloads (Windows Update, Store, Office, Edge) through the Delivery Optimization policy values,
/// and puts back exactly what was there. Each change is journaled before it is made, so one a crash leaves behind
/// is undone at the next start. Never throws: problems come back in the result for the status. One thread only.
public sealed class DoCapManager
{
    public const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization";

    /// REG_DWORD in KB/s. 0 means unlimited, so a cap is at least 1. Policy beats the user's choice in Settings,
    /// and deleting our values brings that choice back.
    public static readonly IReadOnlyList<string> ValueNames = ["DOMaxBackgroundDownloadBandwidth", "DOMaxForegroundDownloadBandwidth"];

    private const uint RpForce = 1;

    private readonly IRegistryStore _registry;
    private readonly PolicyJournal _journal;
    private readonly ILog _log;
    private readonly Action _refreshPolicy;
    private readonly HashSet<string> _foreignLogged = new(StringComparer.OrdinalIgnoreCase);

    public DoCapManager(IRegistryStore registry, PolicyJournal journal, ILog log, Action refreshPolicy)
    {
        _registry = registry;
        _journal = journal;
        _log = log;
        _refreshPolicy = refreshPolicy;
    }

    /// True while caps this instance wrote are in place.
    public bool Active { get; private set; }

    /// True when the journal holds caps to undo, from this run or one that crashed. A damaged journal is set aside.
    public bool HasJournal
    {
        get
        {
            var load = _journal.Load();
            return load.Unreadable || load.Entries.Any(e => e is RegistryEntry);
        }
    }

    /// Restores any caps still journaled, then caps every value no policy already sets.
    public ApplyResult Apply(int kbps)
    {
        if (kbps < 1) return new ApplyResult(false, [], $"A Windows download cap of {kbps} KB/s is not possible: 0 means unlimited.");
        var problems = new List<string>();
        try
        {
            var load = _journal.Load();
            if (load.Entries.Any(e => e is RegistryEntry))
            {
                var restored = Restore();
                if (restored.Problem is not null) problems.Add(restored.Problem);
                if (!restored.Complete) return Failed(problems, "The previous Windows download cap could not be undone, so the new one was not set.");
                load = _journal.Load();
            }
            if (load.Problem is not null) problems.Add(load.Problem);
            if (load.Unreadable) return Failed(problems); // writing a new journal would lose what the old one holds

            var keyExisted = _registry.KeyExists(PolicyKey);
            var skipped = new List<string>();
            var entries = new List<RegistryEntry>();
            foreach (var name in ValueNames)
            {
                if (_registry.Read(PolicyKey, name) is { } foreign)
                {
                    skipped.Add(name);
                    if (_foreignLogged.Add(name)) _log.Info($"Windows download cap: {name} is already set by a policy ({foreign.Data}), so it is left alone");
                    continue;
                }
                _foreignLogged.Remove(name); // logged again if a policy sets it later
                entries.Add(new RegistryEntry(PolicyKey, name, keyExisted, PriorExists: false, PriorKind: null, PriorData: null, (uint)kbps));
            }
            if (entries.Count == 0) return new ApplyResult(false, skipped, Join(problems));

            var others = load.Entries.Where(e => e is not RegistryEntry).ToList();
            _journal.Save([.. others, .. entries]);
            try
            {
                if (!keyExisted && !_registry.CreateKey(PolicyKey))
                    _journal.Save([.. others, .. entries.Select(e => e with { KeyExisted = true })]); // made by someone else just now
                foreach (var entry in entries) _registry.WriteDword(PolicyKey, entry.Name, entry.WrittenData);
            }
            catch (Exception ex)
            {
                _log.Error("Could not set the Windows download cap; undoing it", ex);
                var undo = Restore();
                if (undo.Problem is not null) problems.Add(undo.Problem);
                return Failed(problems, $"Could not limit Windows downloads: {ex.Message}");
            }

            Refresh();
            Active = true;
            _log.Info($"Windows downloads capped at {kbps} KB/s");
            return new ApplyResult(true, skipped, Join(problems));
        }
        catch (Exception ex)
        {
            _log.Error("Could not set the Windows download cap", ex);
            return Failed(problems, $"Could not limit Windows downloads: {ex.Message}");
        }
    }

    /// Puts back what each journaled value held, unless someone changed it since. Idempotent: entries that fail stay
    /// journaled for the next try, and the journal goes last.
    public RestoreResult Restore()
    {
        try
        {
            var load = _journal.Load();
            var ours = load.Entries.OfType<RegistryEntry>().ToList();
            if (load.Unreadable || ours.Count == 0)
            {
                if (!load.Unreadable) Active = false;
                return new RestoreResult(!load.Unreadable, [], load.Problem); // a damaged journal was set aside: nothing it listed is touched
            }

            var problems = new List<string>();
            var done = new List<RegistryEntry>();
            var kept = new List<RegistryEntry>();
            var leftAlone = new List<string>();
            foreach (var entry in ours)
            {
                if (!IsAllowed(entry))
                {
                    _log.Warn($@"The journal lists HKLM\{entry.Key}\{entry.Name}, which Connection Manager never writes; ignored it");
                    continue;
                }
                try
                {
                    if (!Undo(entry)) leftAlone.Add(entry.Name);
                    done.Add(entry);
                }
                catch (Exception ex)
                {
                    _log.Error($"Could not undo the Windows download cap {entry.Name}", ex);
                    problems.Add($"Could not undo the Windows download cap {entry.Name}: {ex.Message}");
                    kept.Add(entry);
                }
            }

            if (kept.Count == 0 && done.Count > 0 && done.All(e => !e.KeyExisted))
            {
                try
                {
                    _registry.DeleteKeyIfEmpty(PolicyKey);
                }
                catch (Exception ex)
                {
                    _log.Error("Could not delete the Delivery Optimization policy key", ex);
                    problems.Add($"Could not delete the Delivery Optimization policy key: {ex.Message}");
                    kept.AddRange(done); // the next restore finds the values already back and tries the key again
                }
            }
            if (leftAlone.Count > 0)
                problems.Add($"Something else changed the Windows download limit ({string.Join(", ", leftAlone)}) after Connection Manager set it, so it was left as it is.");

            if (done.Count > 0) Refresh();
            _journal.Save([.. load.Entries.Where(e => e is not RegistryEntry), .. kept]);
            Active = kept.Count > 0;
            if (done.Count > 0 && kept.Count == 0) _log.Info("Windows download caps restored");
            return new RestoreResult(kept.Count == 0, leftAlone, Join(problems));
        }
        catch (Exception ex)
        {
            _log.Error("Could not undo the Windows download cap", ex);
            return new RestoreResult(false, [], $"Could not undo the Windows download cap: {ex.Message}");
        }
    }

    /// Asks Windows to reload machine policy now. Delivery Optimization has a Group Policy change trigger, so the
    /// cap applies within seconds instead of at its next policy check.
    public static void RefreshMachinePolicy()
    {
        if (!RefreshPolicyEx(bMachine: true, RpForce)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    // False when the value was left alone because someone else changed it.
    private bool Undo(RegistryEntry entry)
    {
        var current = _registry.Read(entry.Key, entry.Name);
        if (current is { Kind: RegistryValueKind.DWord, Data: uint data } && data == entry.WrittenData)
        {
            if (!entry.PriorExists) _registry.DeleteValue(entry.Key, entry.Name);
            else if (PriorDword(entry) is { } prior) _registry.WriteDword(entry.Key, entry.Name, prior);
            else
            {
                _log.Warn($"Windows download cap {entry.Name} held a {entry.PriorKind} before, which cannot be put back; left it as it is");
                return false;
            }
            return true;
        }
        if (IsPrior(current, entry)) return true;
        _log.Warn($"Windows download cap {entry.Name} was changed by something else ({current?.Kind} {current?.Data}); left it as it is");
        return false;
    }

    private static bool IsPrior(RegistryValue? current, RegistryEntry entry)
    {
        if (!entry.PriorExists) return current is null;
        if (current is null || current.Kind.ToString() != entry.PriorKind || entry.PriorData is not { } prior) return false;
        return (current.Data, prior.ValueKind) switch
        {
            (uint dword, JsonValueKind.Number) => prior.TryGetUInt32(out var value) && value == dword,
            (ulong qword, JsonValueKind.Number) => prior.TryGetUInt64(out var value) && value == qword,
            (string text, JsonValueKind.String) => text == prior.GetString(),
            _ => false,
        };
    }

    private static uint? PriorDword(RegistryEntry entry) =>
        entry.PriorKind == nameof(RegistryValueKind.DWord) && entry.PriorData is { ValueKind: JsonValueKind.Number } prior
            && prior.TryGetUInt32(out var value) ? value : null;

    // Even a journal that names other values only ever gets these two touched.
    private static bool IsAllowed(RegistryEntry entry) =>
        string.Equals(entry.Key, PolicyKey, StringComparison.OrdinalIgnoreCase) && ValueNames.Contains(entry.Name, StringComparer.OrdinalIgnoreCase);

    private void Refresh()
    {
        try
        {
            _refreshPolicy();
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not ask Windows to reload policies, so Delivery Optimization sees the change at its next check: {ex.Message}");
        }
    }

    private static ApplyResult Failed(List<string> problems, string? why = null)
    {
        if (why is not null) problems.Add(why);
        return new ApplyResult(false, [], Join(problems));
    }

    private static string? Join(List<string> problems) => problems.Count == 0 ? null : string.Join(" ", problems);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool RefreshPolicyEx(bool bMachine, uint dwOptions);
}
