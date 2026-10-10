using System.Globalization;
using System.Text;
using ConnectionManager.Core.Modes;

namespace ConnectionManager.Core.Ipc;

/// The tray icon's colour. Problem beats everything, then the active modes; Off only when both modes are off.
public enum TrayIconKind { Disconnected, Off, Waiting, GameActive, SaverActive, Both, Problem }

/// OpensReports: clicking it opens the settings window on the reports page.
public sealed record TrayBalloon(string Title, string Text, bool IsWarning = false, bool OpensReports = false);

/// What the tray shows for a service status: icon, tooltip, menu lines and the balloons for a change. Pure, so the
/// tray itself stays a thin shell around it.
public static class TrayView
{
    public const int MaxTooltip = 63; // NOTIFYICONDATA's classic szTip limit
    public const int MaxBalloonTitle = 63;
    public const int MaxBalloonText = 255;
    public const string NotRunning = "Connection Manager service is not running";

    public static TrayIconKind Icon(StatusDto? status, bool connected)
    {
        if (!connected || status is null) return TrayIconKind.Disconnected;
        if (status.Problems.Count > 0) return TrayIconKind.Problem;
        if (status.GameActive && status.SaverActive) return TrayIconKind.Both;
        if (status.GameActive) return TrayIconKind.GameActive;
        if (status.SaverActive) return TrayIconKind.SaverActive;
        return status.GameMode == ModeSetting.Off && status.DataSaver == ModeSetting.Off ? TrayIconKind.Off : TrayIconKind.Waiting;
    }

    public static string Tooltip(StatusDto? status, bool connected)
    {
        if (!connected || status is null) return NotRunning;
        if (status.Problems.Count > 0) return Shorten("Problem: " + Clean(status.Problems[0]), MaxTooltip);

        var saver = "Data Saver: " + (status.SaverActive ? "on" : Idle(status.DataSaver, "waiting"));
        var budget = MaxTooltip - saver.Length - 1;
        string game;
        if (!status.GameActive) game = "Game Mode: " + Idle(status.GameMode, "waiting");
        else if (status.GraceEndsAt is not null) game = "Game Mode: ending soon";
        else if (status.GameName is { Length: > 0 } name) game = "Game Mode: on for " + Shorten(Clean(name), budget - "Game Mode: on for ".Length);
        else game = "Game Mode: on";
        return game + "\n" + saver;
    }

    /// The menu's first status line; unlike the tooltip it has room for the details.
    public static string GameLine(StatusDto status)
    {
        if (!status.GameActive) return status.GameMode switch
        {
            ModeSetting.Off => "Game Mode off",
            ModeSetting.On => "Game Mode starting",
            _ => "Game Mode waiting for a game",
        };
        var blocked = string.Create(CultureInfo.InvariantCulture, $"{status.BlockedCount} blocked");
        if (status.GraceEndsAt is not null) return $"Game Mode ending soon: {blocked}";
        if (status.GameName is { Length: > 0 } name) return $"Game Mode on for {Clean(name)}: {blocked}";
        return $"Game Mode on (manual): {blocked}";
    }

    public static string SaverLine(StatusDto status)
    {
        if (!status.SaverActive) return status.DataSaver switch
        {
            ModeSetting.Off => "Data Saver off",
            ModeSetting.On => "Data Saver starting",
            _ => "Data Saver waiting for a hotspot",
        };
        return $"Data Saver on: {Count(status.AllowedCount, "app")} allowed";
    }

    public static bool ShowsHotspotToggle(StatusDto status) => IsWifi(status) && !string.IsNullOrEmpty(status.Network.Name);

    public static bool ShowsMeteredToggle(StatusDto status) => IsWifi(status) && status.Network.CanMarkMetered;

    /// The "This network is my hotspot" checkbox: the name is on the user's list. Case-sensitive, like the classifier.
    public static bool IsMarkedHotspot(StatusDto status) =>
        status.Network.Name is { } name && status.Settings.HotspotNetworks.Contains(name, StringComparer.Ordinal);

    /// The balloons for going from previous to next, most important first. Problems always show; the rest follow
    /// the Notifications option. With no previous status (first contact) nothing counts as on before, and the
    /// latest report counts as already seen.
    public static IReadOnlyList<TrayBalloon> Balloons(StatusDto? previous, StatusDto next)
    {
        var balloons = new List<TrayBalloon>();
        var oldProblems = previous?.Problems ?? [];
        var added = next.Problems.Where(p => !oldProblems.Contains(p, StringComparer.Ordinal)).ToList();
        if (added.Count > 0)
        {
            var more = added.Count > 1 ? string.Create(CultureInfo.InvariantCulture, $" (and {added.Count - 1} more)") : "";
            balloons.Add(Balloon("Connection Manager problem", Shorten(Clean(added[0]), MaxBalloonText - more.Length) + more, warning: true));
        }
        if (!next.Settings.Options.Notifications) return balloons;

        if (next.GameActive != (previous?.GameActive ?? false))
            balloons.Add(next.GameActive
                ? Balloon("Game Mode on", string.Create(CultureInfo.InvariantCulture,
                    $"{next.BlockedCount} updaters can't use the internet until you stop playing."))
                : Balloon("Game Mode off", "Updaters can use the internet again."));

        if (next.SaverActive != (previous?.SaverActive ?? false))
            balloons.Add(next.SaverActive
                ? Balloon("Data Saver on", Reason(next) + "Only the apps you allow can use the internet.")
                : Balloon("Data Saver off", Reason(next) + "Every app can use the internet again."));

        if (previous is not null && next.LatestReport is { } report && IsNewer(report, previous.LatestReport))
            balloons.Add(Balloon("Post-game report",
                $"{Count(report.AppCount, "app")} used {Megabytes(report.TotalBytes)} MB during your game. Open Settings to review.") with { OpensReports = true });
        return balloons;
    }

    /// A command the service refused or could not run. Response.Error is short and plain, but still from another process.
    public static TrayBalloon CommandError(string error) => Balloon("Connection Manager", Clean(error), warning: true);

    /// Control characters (a newline in a game name, say) would break a one-line label.
    public static string Clean(string text)
    {
        if (!text.Any(char.IsControl)) return text;
        var builder = new StringBuilder(text.Length);
        foreach (var c in text) builder.Append(char.IsControl(c) ? ' ' : c);
        return builder.ToString();
    }

    private static TrayBalloon Balloon(string title, string text, bool warning = false) =>
        new(Shorten(title, MaxBalloonTitle), Shorten(text, MaxBalloonText), warning);

    // Dismissing the newest report brings back an older one, already announced: only a later game is news.
    private static bool IsNewer(ReportSummaryDto report, ReportSummaryDto? before) =>
        before is null || (report.Id != before.Id && report.EndedAt > before.EndedAt);

    private static bool IsWifi(StatusDto status) => string.Equals(status.Network.Kind, "wifi", StringComparison.OrdinalIgnoreCase);

    private static string Idle(ModeSetting setting, string auto) => setting switch
    {
        ModeSetting.Off => "off",
        ModeSetting.On => "starting",
        _ => auto,
    };

    private static string Reason(StatusDto status)
    {
        var reason = Clean(status.SaverReason).Trim().TrimEnd('.');
        return reason.Length == 0 ? "" : Shorten(reason, 100) + ". ";
    }

    private static string Count(int n, string noun) => string.Create(CultureInfo.InvariantCulture, $"{n} {noun}{(n == 1 ? "" : "s")}");

    private static string Megabytes(long bytes)
    {
        var mb = Math.Max(0, bytes) / (1024.0 * 1024.0);
        return mb.ToString(mb < 10 ? "0.#" : "N0", CultureInfo.InvariantCulture);
    }

    /// At most max characters, ending in "…" when cut, never splitting a surrogate pair.
    private static string Shorten(string text, int max)
    {
        if (text.Length <= max) return text;
        if (max <= 1) return max == 1 ? "…" : "";
        var cut = max - 1;
        if (char.IsHighSurrogate(text[cut - 1])) cut--;
        return text[..cut] + "…";
    }
}
