using System.IO.Compression;

namespace ConnectionManager.Setup;

/// The release zip inside the exe. It is ours, so the check only refuses what no release holds: it is cheap, and it runs
/// before anything is unpacked as admin. ZipFile.ExtractToDirectory refuses a path outside the folder too.
internal static class Payload
{
    public const string ResourceName = "payload.zip";
    public const int MaxEntries = 5000;
    public const long MaxBytes = 1L << 30; // uncompressed, every entry together

    /// Null when every entry is a plain relative path (either slash) and the zip is within the limits, otherwise why not.
    internal static string? Problem(Stream zip, long maxBytes = MaxBytes)
    {
        try
        {
            zip.Position = 0;
            using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count > MaxEntries) return $"it holds {archive.Entries.Count} entries, more than {MaxEntries}";
            long total = 0;
            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName;
                if (Path.IsPathRooted(name) || name.Contains(':') || name.Split('/', '\\').Contains(".."))
                    return $"the entry '{name}' is not a path inside its folder";
                total += entry.Length;
                if (total > maxBytes) return $"it unpacks to more than {maxBytes:N0} bytes";
            }
            return null;
        }
        catch (InvalidDataException ex)
        {
            return ex.Message;
        }
    }
}
