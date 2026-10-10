using System.Text.Json;
using System.Text.Json.Serialization;
using ConnectionManager.Core.Ipc;
using ConnectionManager.Core.Logging;
using Microsoft.Win32;

namespace ConnectionManager.Core.Caps;

/// One change made outside the data folder, recorded before it is made so a crash can be undone at the next start.
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(RegistryEntry), "registry")]
[JsonDerivedType(typeof(WlanCostEntry), "wlanCost")]
public abstract record JournalEntry;

/// A registry value we wrote. When PriorExists, PriorKind is a RegistryValueKind name and PriorData the raw value
/// that was there, a number or a string.
public sealed record RegistryEntry(string Key, string Name, bool KeyExisted, bool PriorExists, string? PriorKind,
    JsonElement? PriorData, uint WrittenData) : JournalEntry;

/// A Wi-Fi profile marked metered (Task 11).
public sealed record WlanCostEntry(string Profile) : JournalEntry;

/// Entries is empty when the file is missing or was set aside. Unreadable means the file is still there but could
/// not be read (locked, no access), so nothing may overwrite it yet.
public sealed record JournalLoad(IReadOnlyList<JournalEntry> Entries, string? Problem = null, bool Unreadable = false);

/// journal.json in the data folder: what the service changed outside it, so it can always be put back. Shared by
/// every feature that makes such changes; each one owns its own kind of entry and keeps the others when it saves.
public sealed class PolicyJournal
{
    public const int MaxBytes = 64 * 1024;
    private const int Version = 1;

    private readonly ILog _log;

    public PolicyJournal(string dataFolder, ILog log)
    {
        FilePath = Path.Combine(dataFolder, "journal.json");
        _log = log;
    }

    public string FilePath { get; }

    public bool Exists => File.Exists(FilePath);

    /// A damaged file is renamed to journal.json.bad, logged and reported: nothing it listed can be trusted to undo.
    public JournalLoad Load()
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
            return new JournalLoad([]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"Could not read {FilePath}: {ex.Message}");
            return new JournalLoad([], $"Connection Manager could not read its undo record {FilePath}: {ex.Message}", Unreadable: true);
        }
        if (tooBig) return SetAside($"it is larger than {MaxBytes / 1024} KB"); // set aside once the file is closed

        try
        {
            var file = JsonSerializer.Deserialize(bytes, JournalJson.Default.JournalFile);
            return file is null ? SetAside("it holds null") : Invalid(file) is { } why ? SetAside(why) : new JournalLoad(file.Entries);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException) // NotSupported: an entry without a kind
        {
            return SetAside(ex.Message);
        }
    }

    /// Flushed to disk, then swapped in, so the record is there before the change it describes. No entries deletes
    /// the file. Throws when it cannot be written: the caller must then not make the change.
    public void Save(IReadOnlyList<JournalEntry> entries)
    {
        if (entries.Count == 0)
        {
            File.Delete(FilePath);
            return;
        }
        var file = new JournalFile(Version, entries);
        if (Invalid(file) is { } why) throw new ArgumentException($"Journal entries are not valid: {why}", nameof(entries));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(file, JournalJson.Default.JournalFile);
        if (bytes.Length > MaxBytes) throw new InvalidOperationException($"The journal would be larger than {MaxBytes / 1024} KB");

        var temp = FilePath + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, FilePath, overwrite: true);
    }

    private JournalLoad SetAside(string why)
    {
        var bad = FilePath + ".bad";
        try
        {
            File.Move(FilePath, bad, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error($"{FilePath} is damaged ({why}) and could not be renamed", ex);
            return new JournalLoad([], $"Connection Manager's undo record {FilePath} is damaged and could not be set aside: {ex.Message}", Unreadable: true);
        }
        _log.Warn($"{FilePath} was damaged ({why}); kept it as {bad} and undid nothing it listed");
        return new JournalLoad([],
            $"Connection Manager's undo record was damaged, so earlier changes could not be undone automatically. It was kept as {bad}.");
    }

    private static string? Invalid(JournalFile file)
    {
        if (file.V != Version) return $"version {file.V} is not {Version}";
        foreach (var entry in file.Entries)
        {
            var why = entry switch
            {
                null => "an entry is null",
                RegistryEntry registry => Invalid(registry),
                WlanCostEntry wlan => IsName(wlan.Profile) ? null : "a Wi-Fi profile name is empty or too long",
                _ => "an entry has an unknown kind",
            };
            if (why is not null) return why;
        }
        return null;
    }

    private static string? Invalid(RegistryEntry entry)
    {
        if (!IsName(entry.Key) || !IsName(entry.Name)) return "a registry key or value name is empty or too long";
        if (entry.WrittenData < 1) return "a written value is 0, which means unlimited";
        if (!entry.PriorExists) return null;
        return Enum.GetNames<RegistryValueKind>().Contains(entry.PriorKind, StringComparer.Ordinal)
            && entry.PriorData is { ValueKind: JsonValueKind.Number or JsonValueKind.String }
            ? null
            : "a prior value has no valid kind or data";
    }

    private static bool IsName(string? text) => !string.IsNullOrEmpty(text) && text.Length <= IpcLimits.MaxName;
}

internal sealed record JournalFile(int V, IReadOnlyList<JournalEntry> Entries);

/// Strict, like the pipe: the folder is locked, but the service still checks everything it reads back.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    MaxDepth = 8)]
[JsonSerializable(typeof(JournalFile))]
internal sealed partial class JournalJson : JsonSerializerContext;
