using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ConnectionManager.Setup.Tests;

/// Without admin rights only your own account can own a new folder, so these pass it in place of Administrators, in a
/// temporary folder rather than Windows\Temp.
public sealed class StagingTests : IDisposable
{
    private static readonly string Me = WindowsIdentity.GetCurrent().User!.Value;

    private readonly string _root = Directory.CreateTempSubdirectory("cm-setup-test-").FullName;
    private readonly List<string> _locked = [];
    private readonly List<string> _junctions = [];

    [Fact]
    public void The_folder_is_new_random_and_only_admins_and_system_may_use_it()
    {
        var folder = Locked(Staging.Create(_root, Me));

        Assert.Equal(_root, Path.GetDirectoryName(folder));
        Assert.Matches("^ConnectionManager-setup-[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", Path.GetFileName(folder));
        var security = new DirectoryInfo(folder).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        Assert.Equal(Me, security.GetOwner(typeof(SecurityIdentifier))?.Value);
        Assert.True(security.AreAccessRulesProtected);
        // .NET lists the entries in its own order, so the SDDL is compared after the same round trip.
        var expected = new DirectorySecurity();
        expected.SetSecurityDescriptorSddlForm(Staging.Dacl);
        Assert.Equal(expected.GetSecurityDescriptorSddlForm(AccessControlSections.Access), security.GetSecurityDescriptorSddlForm(AccessControlSections.Access));
    }

    [Fact]
    public void Two_folders_never_share_a_name() =>
        Assert.NotEqual(Locked(Staging.Create(_root, Me)), Locked(Staging.Create(_root, Me)));

    [Fact]
    public void An_existing_folder_is_refused()
    {
        var path = Directory.CreateDirectory(Path.Combine(_root, "taken")).FullName;

        var error = Assert.Throws<IOException>(() => Staging.CreateLocked(path, Me));

        Assert.Contains(path, error.Message);
    }

    [Fact]
    public void A_link_someone_made_at_that_path_is_refused()
    {
        var target = Directory.CreateDirectory(Path.Combine(_root, "elsewhere")).FullName;
        var link = Junction(Path.Combine(_root, "link"), target);

        Assert.Throws<IOException>(() => Staging.CreateLocked(link, Me));
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
    }

    [Fact]
    public void Delete_removes_everything_read_only_files_included()
    {
        var folder = Path.Combine(_root, "staging");
        var file = Path.Combine(folder, "app", "x.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "x");
        File.SetAttributes(file, FileAttributes.ReadOnly);

        Staging.Delete(folder);

        Assert.False(Directory.Exists(folder));
    }

    private string Locked(string folder)
    {
        _locked.Add(folder);
        return folder;
    }

    // mklink /J needs no admin rights, unlike a symbolic link.
    private string Junction(string link, string target)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in (string[])["/c", "mklink", "/J", link, target]) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        _junctions.Add(link);
        return link;
    }

    public void Dispose()
    {
        // Not even this account may delete a locked folder, but as its owner it may change who can.
        foreach (var folder in _locked)
        {
            var security = new DirectorySecurity();
            security.SetAccessRule(new FileSystemAccessRule(new SecurityIdentifier(Me), FileSystemRights.FullControl, AccessControlType.Allow));
            new DirectoryInfo(folder).SetAccessControl(security);
            Directory.Delete(folder);
        }
        foreach (var link in _junctions) Directory.Delete(link);
        Directory.Delete(_root, recursive: true);
    }
}
