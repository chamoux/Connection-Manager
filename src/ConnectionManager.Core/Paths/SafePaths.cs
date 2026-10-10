using ConnectionManager.Core.Native;

namespace ConnectionManager.Core.Paths;

/// The service runs as SYSTEM, so a path that came from a normal user (the pipe, Steam's files, the user's profile)
/// must pass here before the service opens it, lists it or hands it to WFP, which opens it too. Only plain paths on a
/// local drive pass: no network path (SYSTEM would sign in to it as the machine account), no device or NT path, no
/// stream, no name Windows would quietly change or treat as a device, and no link anywhere along the way.
public static class SafePaths
{
    /// The same as the IPC path limit.
    public const int MaxLength = 1024;

    // ':' after the drive letter opens a stream; < > and " are wildcards to FindFirstFile, like * and ?.
    private const string Reserved = ":*?\"<>|";

    private static readonly HashSet<string> DeviceNames = new(
        [
            "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
            "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
            "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
        ],
        StringComparer.OrdinalIgnoreCase);

    /// The normalised full path, with short 8.3 names expanded, when the service may open `path`. The service must
    /// use `full` from here on, never the input.
    public static bool TryLocal(string? path, out string full)
    {
        full = "";
        if (path is null || !IsPlain(path)) return false;
        var normal = Normalize(path);
        // The drive is checked before anything on it is touched.
        if (!IsPlain(normal) || !OnLocalDrive(normal) || !NoLinks(normal, out var existing)) return false;

        // Windows only makes short names with a '~' (only an admin can set others). Expanding one can bring back a
        // name that ends in a dot, so the long form is checked again.
        if (normal.Contains('~') && existing.Length > 3)
        {
            if (NativeProcess.TryLongPath(existing) is not { } longPrefix) return false;
            normal = longPrefix + normal[existing.Length..];
            if (!IsPlain(normal)) return false;
        }
        full = normal;
        return true;
    }

    /// A folder the service may list: it passes TryLocal and exists.
    public static bool IsSafeFolderToScan(string? folder) => TryLocal(folder, out var full) && Directory.Exists(full);

    /// The path with control characters replaced and its length capped, so a rejected path can go in a log line.
    public static string Printable(string? path)
    {
        if (path is null) return "";
        var text = path.Length > MaxLength ? path[..MaxLength] + "…" : path;
        return string.Create(text.Length, text, (chars, source) =>
        {
            for (var i = 0; i < source.Length; i++) chars[i] = char.IsControl(source[i]) ? '?' : source[i];
        });
    }

    // X:\ or X:/ only, which rules out UNC, \\?\, \\.\, \??\ and drive-relative C:x. Checked on the input too,
    // because GetFullPath drops a trailing dot or space and turns C:\a\NUL into \\.\NUL.
    private static bool IsPlain(string path)
    {
        if (path.Length is < 3 or > MaxLength || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] is not ('\\' or '/'))
            return false;
        foreach (var c in path.AsSpan(2))
            if (char.IsControl(c) || Reserved.Contains(c)) return false;
        foreach (var segment in path[3..].Split('\\', '/'))
        {
            if (segment is "" or "." or "..") continue;
            if (segment[^1] is '.' or ' ' || DeviceNames.Contains(DeviceStem(segment))) return false;
        }
        return true;
    }

    // Path.GetFullPath for the form IsPlain allows, without touching the disk: GetFullPath expands a path with a '~'
    // by looking it up, which would go through any link (even one to a network share) before NoLinks could see it.
    private static string Normalize(string path) => path[..2] + @"\" + JoinSegments(path[3..]);

    /// `rest` split at '\' and '/', without empty and "." segments, each ".." removing the one before it (never more
    /// than `rest` holds), joined with '\'. Strings only: nothing is looked up.
    internal static string JoinSegments(string rest)
    {
        var segments = new List<string>();
        foreach (var segment in rest.Split('\\', '/'))
        {
            if (segment is "" or ".") continue;
            if (segment != "..") segments.Add(segment);
            else if (segments.Count > 0) segments.RemoveAt(segments.Count - 1);
        }
        return string.Join('\\', segments);
    }

    // Windows treats "nul.txt" and "con .log" as devices too.
    private static string DeviceStem(string segment)
    {
        var dot = segment.IndexOf('.');
        return (dot < 0 ? segment : segment[..dot]).TrimEnd(' ');
    }

    // Drive letters as SYSTEM sees them: a user's own mapped drives are not visible to it.
    private static bool OnLocalDrive(string full) => new DriveInfo(full[..1]).DriveType is DriveType.Fixed or DriveType.Removable;

    // Each prefix from the drive root down, so nothing is ever looked up through a link. Stops at the first one that
    // does not exist, since nothing below it can. `existing` is the deepest prefix that does.
    private static bool NoLinks(string full, out string existing)
    {
        existing = full[..3];
        for (var end = 2; end < full.Length;)
        {
            end = full.IndexOf('\\', end + 1);
            if (end < 0) end = full.Length;
            var prefix = full[..end];
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(prefix);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false; // cannot tell whether it is a link
            }
            if (attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
            existing = prefix;
        }
        return true;
    }
}
