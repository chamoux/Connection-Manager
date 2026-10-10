using System.ComponentModel;

namespace ConnectionManager.Setup;

/// The folder the zip is unpacked into, in Windows\Temp. Users may create folders there too, so its name is random and
/// nobody can make it first. Only admins and SYSTEM may use it, and it inherits nothing from Windows\Temp.
internal static class Staging
{
    internal const string Dacl = "D:P(A;OICI;FA;;;BA)(A;OICI;FA;;;SY)";

    /// Creates <parent>\ConnectionManager-setup-<random guid>. Only an admin can make Administrators (BA) the owner, so
    /// tests pass their own account instead.
    public static string Create(string parent, string owner = "BA") =>
        CreateLocked(Path.Combine(parent, $"ConnectionManager-setup-{Guid.NewGuid()}"), owner);

    /// Creates the folder with its whole security descriptor in one step, so it is never anyone else's and nobody else can
    /// add to it. Throws IOException when anything is already at that path.
    internal static string CreateLocked(string path, string owner)
    {
        try
        {
            Native.CreateDirectory(path, $"O:{owner}{Dacl}");
        }
        catch (Win32Exception ex)
        {
            throw new IOException($"Could not create {path}: {ex.Message}", ex);
        }
        // A new folder is never a link, but the unpacking trusts this, so it is checked.
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new IOException($"{path} is a link to somewhere else.");
        return path;
    }

    /// Deletes the folder and everything in it as far as it can, read-only files included. Says what is left.
    public static void Delete(string path)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not delete {path}: {ex.Message}");
        }
    }
}
