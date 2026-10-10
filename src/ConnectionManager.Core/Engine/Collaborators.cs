using System.Collections.Frozen;
using ConnectionManager.Core.Ipc;
using ConnectionManager.Core.Network;
using ConnectionManager.Core.Processes;
using ConnectionManager.Core.Settings;
using ConnectionManager.Core.Wfp;

namespace ConnectionManager.Core.Engine;

// What the engine drives. Each part is called on the engine thread only, once per tick (every 2 s) or per command,
// and must not throw: return a short plain problem for the status instead (the engine still catches and logs).

/// What the engine knows on a tick, after game detection and the network read. Valid for that tick only.
/// IsProtected: never blocked by Game Mode (Windows, Steam and its libraries, games, anti-cheat, this app).
public sealed record TickContext(
    DateTimeOffset Now,
    AppSettings Settings,
    NetworkSnapshot Network,
    IReadOnlyList<RunningProcess> Running,
    ControllerStatus Game,
    bool SaverActive,
    string? SteamPath,
    IReadOnlyList<string> SteamLibraries,
    Func<string, bool> IsProtected,
    string? ServiceExe);

/// The network the PC is on. NetworkWatcher.
public interface INetworkSource
{
    NetworkSnapshot Current { get; }

    /// Re-reads when something changed or the last read is old. True when Current changed.
    bool Refresh(DateTimeOffset now);
}

/// Data Saver's rules (Task 9): block-all, the essential permits, and what may still connect.
public interface IDataSaverPlanner
{
    /// Called on each tick while Data Saver is on. Rules use the SaverBlockAll, SaverPermit and EssentialPermit weights.
    IReadOnlySet<FilterRule> Plan(TickContext context);
}

/// No Data Saver rules: the default when no planner is wired (DataSaverPlanner is the real one).
public sealed class NoDataSaverRules : IDataSaverPlanner
{
    public static readonly NoDataSaverRules Instance = new();

    public IReadOnlySet<FilterRule> Plan(TickContext context) => FrozenSet<FilterRule>.Empty;
}

/// Windows download caps (DoCapController over DoCapManager): wanted while Data Saver is on and Windows downloads are allowed.
public interface ICapController
{
    /// Applies, changes or removes the cap to match the context; does nothing when nothing changed.
    string? Update(TickContext context);
}

/// The Wi-Fi scan pause (Task 12, WifiTuner): wanted while Game Mode is on and Wi-Fi tuning is allowed.
/// A WLAN notification must not act from its callback: set a flag and wake the engine (ServiceEngine.Wake); the next
/// Update re-applies.
/// Dispose undoes the pause.
public interface IWifiController : IDisposable
{
    string? Update(TickContext context);
}

/// The traffic meter (Task 13). TrafficMeter has exactly these members.
public interface ITrafficMeter : IDisposable
{
    bool Enabled { get; set; }

    /// Why it isn't counting, or null.
    string? Problem { get; }

    void Tick(DateTimeOffset now);

    /// The programs that used the most data in the last 24 hours.
    IReadOnlyList<UsageDto> Top(int n);

    /// Bytes per program since the meter started.
    IReadOnlyDictionary<string, long> Snapshot();
}

/// Post-game reports (Task 14, GameSessionReporter + ReportStore).
public interface IReportSource
{
    /// Builds and stores a report when a game session ends (Game.Game goes from active to idle).
    string? Update(TickContext context);

    /// The newest report the user hasn't dismissed.
    ReportSummaryDto? Latest { get; }

    IReadOnlyList<ReportDto> All();

    /// False when no report has this id.
    bool Dismiss(string id);
}

/// Per-app upload caps (Task 19, UploadCapController over UploadCapManager). Applied at start and after each change to
/// the caps or their switch, never on the tick: every call goes to Windows QoS, which can be slow. Unlike the other parts,
/// called on the engine's upload worker, one call at a time, never on the engine thread.
public interface IUploadCaps
{
    /// Makes our QoS policies match Settings.UploadCaps, or removes them all when UploadCapsEnabled is off.
    string? Apply(AppSettings settings);

    /// Removes every policy of ours, from this run or an earlier one: at start, before the first Apply, and at stop.
    string? RemoveAll();
}

/// Marking the current Wi-Fi as metered (Task 11, MeteredToggle).
public interface IMeteredToggle
{
    /// For the status: the current Wi-Fi's profile was found, and whether we marked it metered.
    (bool CanMark, bool MarkedByUs) Describe(NetworkSnapshot network);

    /// Null when done, otherwise a short plain message for the client.
    string? Set(NetworkSnapshot network, bool on);
}
