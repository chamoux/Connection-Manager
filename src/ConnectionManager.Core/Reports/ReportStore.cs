using System.Text.Json;
using System.Text.Json.Serialization;
using ConnectionManager.Core.Ipc;
using ConnectionManager.Core.Logging;

namespace ConnectionManager.Core.Reports;

/// reports.json in the data folder: the newest post-game reports, each with whether the user dismissed it.
/// Reports are a convenience, so a damaged file or a failed save never stops the service. Call from one thread.
public sealed class ReportStore
{
    public const int MaxReports = 20;
    public const int MaxBytes = 1024 * 1024;
    private const int Version = 1;
    private const long MaxAppBytes = long.MaxValue / (2 * GameReportBuilder.MaxApps); // a summary's total can't overflow

    private readonly ILog _log;
    private readonly List<StoredReport> _reports; // newest first
    private string? _lastSaveError;

    /// Loads the file now. A missing one is an empty store; a damaged or oversize one is renamed to reports.json.bad.
    public ReportStore(string dataFolder, ILog log)
    {
        FilePath = Path.Combine(dataFolder, "reports.json");
        _log = log;
        _reports = Load();
    }

    public string FilePath { get; }

    /// Newest first, dismissed ones included.
    public IReadOnlyList<ReportDto> All() => _reports.Select(r => r.Report).ToList();

    /// The newest report the user hasn't dismissed.
    public ReportSummaryDto? LatestUndismissed() => _reports.FirstOrDefault(r => !r.Dismissed) is { } stored ? Summary(stored.Report) : null;

    public static ReportSummaryDto Summary(ReportDto report) =>
        new(report.Id, report.Game, report.EndedAt, report.Apps.Sum(a => a.BytesIn + a.BytesOut), report.Apps.Count);

    /// Saved at once; the oldest beyond MaxReports is dropped. Throws ArgumentException for a report that would not load back.
    public void Add(ReportDto report)
    {
        if (!IsValid(report)) throw new ArgumentException("The report has empty, too long or negative values", nameof(report));
        _reports.RemoveAll(r => r.Report.Id == report.Id);
        _reports.Insert(0, new StoredReport(report, Dismissed: false));
        if (_reports.Count > MaxReports) _reports.RemoveRange(MaxReports, _reports.Count - MaxReports);
        Save();
    }

    /// False when no report has this id.
    public bool Dismiss(string id)
    {
        var index = _reports.FindIndex(r => r.Report.Id == id);
        if (index < 0) return false;
        if (_reports[index].Dismissed) return true;
        _reports[index] = _reports[index] with { Dismissed = true };
        Save();
        return true;
    }

    private List<StoredReport> Load()
    {
        byte[] bytes;
        bool tooBig;
        try
        {
            using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            tooBig = stream.Length > MaxBytes;
            bytes = tooBig ? [] : new byte[stream.Length];
            stream.ReadExactly(bytes);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"Could not read {FilePath}, starting with no reports: {ex.Message}");
            return [];
        }
        if (tooBig) return SetAside($"it is larger than {MaxBytes / 1024} KB"); // set aside once the file is closed

        ReportFile? file;
        try
        {
            file = JsonSerializer.Deserialize(bytes, ReportJson.Default.ReportFile);
        }
        catch (JsonException ex)
        {
            return SetAside(ex.Message);
        }
        if (file is null) return SetAside("it holds null");
        if (file.V != Version) return SetAside($"version {file.V} is not {Version}");

        // One bad entry costs only that report.
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<StoredReport>();
        foreach (var entry in file.Reports)
            if (kept.Count < MaxReports && entry is not null && IsValid(entry.Report) && ids.Add(entry.Report.Id)) kept.Add(entry);
        if (kept.Count < file.Reports.Count) _log.Warn($"Dropped {file.Reports.Count - kept.Count} invalid, repeated or extra reports from {FilePath}");
        return kept;
    }

    private List<StoredReport> SetAside(string why)
    {
        var bad = FilePath + ".bad";
        try
        {
            File.Move(FilePath, bad, overwrite: true);
            _log.Warn($"{FilePath} was damaged ({why}); kept it as {bad} and started with no reports");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error($"{FilePath} is damaged ({why}) and could not be renamed; starting with no reports", ex);
        }
        return [];
    }

    // Flushed to disk, then swapped in, so a power cut leaves the old file or the new one, never half of one.
    private void Save()
    {
        var temp = FilePath + ".tmp";
        try
        {
            var bytes = Serialize();
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, FilePath, overwrite: true);
            _lastSaveError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception deleteEx) when (deleteEx is IOException or UnauthorizedAccessException)
            {
                // Overwritten by the next save.
            }
            if (ex.Message != _lastSaveError) _log.Error($"Could not save {FilePath}; reports are kept in memory until the next save", ex);
            _lastSaveError = ex.Message;
        }
    }

    // Long non-ASCII paths could in theory push the file past MaxBytes, which Load would refuse: drop the oldest until it fits.
    private byte[] Serialize()
    {
        while (true)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new ReportFile(Version, _reports), ReportJson.Default.ReportFile);
            if (bytes.Length <= MaxBytes || _reports.Count <= 1) return bytes;
            _reports.RemoveAt(_reports.Count - 1);
        }
    }

    internal static bool IsValid(ReportDto? report) =>
        report is { Apps.Count: <= GameReportBuilder.MaxApps }
        && IsText(report.Id, IpcLimits.MaxName)
        && IsText(report.Game, IpcLimits.MaxName)
        && report.Apps.All(a => a is { Name.Length: <= IpcLimits.MaxName, BytesIn: >= 0 and <= MaxAppBytes, BytesOut: >= 0 and <= MaxAppBytes }
            && IsText(a.Path, IpcLimits.MaxPath));

    private static bool IsText(string? text, int max) => !string.IsNullOrEmpty(text) && text.Length <= max;
}

internal sealed record StoredReport(ReportDto Report, bool Dismissed);

internal sealed record ReportFile(int V, IReadOnlyList<StoredReport?> Reports);

/// Strict, like the journal: the folder is locked, but the service still checks everything it reads back.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    MaxDepth = 8)]
[JsonSerializable(typeof(ReportFile))]
internal sealed partial class ReportJson : JsonSerializerContext;
